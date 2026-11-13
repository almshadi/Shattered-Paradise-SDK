using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Data;
using Ranked.Backend.Notifications;

namespace Ranked.Backend.Auth;

// Optional "Sign in with Discord", alongside Google. Reuses the LoginAttempt / poll flow of AuthEndpoints:
// POST /auth/login → open discordBrowserUrl → Discord consent (scope "identify") → callback → client polls.
public static class DiscordAuthEndpoints
{
	const string AuthorizeUrl = "https://discord.com/oauth2/authorize";
	const string TokenUrl = "https://discord.com/api/oauth2/token";
	const string MeUrl = "https://discord.com/api/users/@me";

	public static string StartUrl(RankedOptions o, Guid loginId) => $"{o.PublicBaseUrl.TrimEnd('/')}/auth/discord/start?login={loginId}";

	static string RedirectUri(RankedOptions o) => $"{o.PublicBaseUrl.TrimEnd('/')}/auth/discord/callback";

	const string NotConfigured = "Discord sign-in is not configured on this server. Please use Google sign-in instead.";

	public static void Map(WebApplication app)
	{
		var g = app.MapGroup("/auth/discord");

		g.MapGet("/start", async (Guid? login, RankedDb db, IOptions<RankedOptions> opt, IOptions<DiscordOptions> discord) =>
		{
			var o = opt.Value;
			var d = discord.Value;
			if (!d.LoginConfigured)
				return AuthEndpoints.Page(NotConfigured, false);

			var attempt = login == null ? null : await db.LoginAttempts.FindAsync(login.Value);
			if (attempt == null || attempt.Status != LoginStatus.Pending || attempt.ExpiresAt < DateTime.UtcNow)
				return AuthEndpoints.Page("This login link has expired. Start the login again from the game.", false);

			var url = AuthorizeUrl
				+ "?client_id=" + Uri.EscapeDataString(d.ClientId)
				+ "&redirect_uri=" + Uri.EscapeDataString(RedirectUri(o))
				+ "&response_type=code"
				+ "&scope=identify"
				+ "&prompt=consent"
				+ "&state=" + attempt.Id;

			return Results.Redirect(url);
		});

		g.MapGet("/callback", async (string? code, string? state, string? error, RankedDb db, IOptions<RankedOptions> opt,
			IOptions<DiscordOptions> discord, IHttpClientFactory http, ILogger<DiscordOptions> log) =>
		{
			var o = opt.Value;
			var d = discord.Value;
			if (!d.LoginConfigured)
				return AuthEndpoints.Page(NotConfigured, false);

			if (!Guid.TryParse(state, out var loginId))
				return AuthEndpoints.Page("Invalid login state.", false);

			var attempt = await db.LoginAttempts.FindAsync(loginId);
			if (attempt == null || attempt.Status != LoginStatus.Pending || attempt.ExpiresAt < DateTime.UtcNow)
				return AuthEndpoints.Page("This login has expired. Start the login again from the game.", false);

			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
			{
				attempt.Status = LoginStatus.Failed;
				attempt.Error = error ?? "no_code";
				await db.SaveChangesAsync();
				return AuthEndpoints.Page("Sign-in was cancelled.", false);
			}

			string discordId;
			try
			{
				var client = http.CreateClient();
				client.Timeout = TimeSpan.FromSeconds(15);

				using var tokenRes = await client.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
				{
					["client_id"] = d.ClientId,
					["client_secret"] = d.ClientSecret,
					["grant_type"] = "authorization_code",
					["code"] = code,
					["redirect_uri"] = RedirectUri(o)
				}));

				var tokenBody = await tokenRes.Content.ReadAsStringAsync();
				if (!tokenRes.IsSuccessStatusCode)
					throw new InvalidOperationException($"Token exchange failed ({(int)tokenRes.StatusCode}): {tokenBody}");

				var accessToken = JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString()!;

				using var meReq = new HttpRequestMessage(HttpMethod.Get, MeUrl);
				meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
				using var meRes = await client.SendAsync(meReq);
				var meBody = await meRes.Content.ReadAsStringAsync();
				if (!meRes.IsSuccessStatusCode)
					throw new InvalidOperationException($"users/@me failed ({(int)meRes.StatusCode}): {meBody}");

				discordId = JsonDocument.Parse(meBody).RootElement.GetProperty("id").GetString()!;
				if (string.IsNullOrEmpty(discordId))
					throw new InvalidOperationException("Discord returned no user id");
			}
			catch (Exception e)
			{
				log.LogWarning(e, "Discord login failed");
				attempt.Status = LoginStatus.Failed;
				attempt.Error = "discord_verification_failed";
				await db.SaveChangesAsync();
				return AuthEndpoints.Page("Sign-in with Discord failed. Please try again.", false);
			}

			var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == discordId);
			if (user == null)
			{
				// Only Discord's stable account id is kept. The player picks a public nickname afterwards.
				user = new User { DiscordId = discordId };
				db.Users.Add(user);
				db.Ratings.Add(new Rating { UserId = user.Id, Mmr = o.Matchmaking.InitialRating });
			}

			user.LastSeenAt = DateTime.UtcNow;

			attempt.Status = LoginStatus.Complete;
			attempt.UserId = user.Id;
			attempt.IssuedToken = await AuthEndpoints.IssueSession(db, user, o);
			await db.SaveChangesAsync();

			return AuthEndpoints.Page("Signed in with Discord. You can close this tab and return to Shattered Paradise.", true);
		});
	}
}
