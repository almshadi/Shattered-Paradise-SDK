using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Data;

namespace Ranked.Backend.Auth;

public static class Tokens
{
	public static string NewSecret(int bytes = 32) =>
		Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	public static string Hash(string value) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

	public static bool FixedTimeEquals(string a, string b) =>
		CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

public static class CurrentUser
{
	// Resolves "Authorization: Bearer <token>" to a user, or null.
	public static async Task<User?> GetAsync(HttpContext ctx, RankedDb db)
	{
		var header = ctx.Request.Headers.Authorization.ToString();
		if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
			return null;

		var hash = Tokens.Hash(header["Bearer ".Length..].Trim());
		var session = await db.AuthSessions.Include(s => s.User).ThenInclude(u => u!.Rating)
			.FirstOrDefaultAsync(s => s.TokenHash == hash);

		if (session == null || session.ExpiresAt < DateTime.UtcNow)
			return null;

		session.User!.LastSeenAt = DateTime.UtcNow;
		return session.User;
	}
}

public static class AuthEndpoints
{
	const string GoogleAuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
	const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";

	public static void Map(WebApplication app)
	{
		var g = app.MapGroup("/auth");
		var rating = app.Services.GetRequiredService<IOptions<Ratings.RatingOptions>>().Value;

		// 1) Game client starts a login. Returns the URL to open in the browser and a poll secret.
		g.MapPost("/login", async (RankedDb db, IOptions<RankedOptions> opt, IOptions<Notifications.DiscordOptions> discord) =>
		{
			var o = opt.Value;
			var pollSecret = Tokens.NewSecret();
			var attempt = new LoginAttempt
			{
				PollSecretHash = Tokens.Hash(pollSecret),
				Status = LoginStatus.Pending,
				ExpiresAt = DateTime.UtcNow.AddMinutes(10)
			};

			db.LoginAttempts.Add(attempt);
			await db.SaveChangesAsync();

			return Results.Ok(new
			{
				loginId = attempt.Id,
				pollSecret,
				browserUrl = $"{o.PublicBaseUrl.TrimEnd('/')}/auth/google/start?login={attempt.Id}",
				googleConfigured = !string.IsNullOrEmpty(o.Auth.GoogleClientId),
				discordConfigured = discord.Value.LoginConfigured,
				discordBrowserUrl = DiscordAuthEndpoints.StartUrl(o, attempt.Id),
				devLogin = o.Auth.DevLogin
			});
		});

		// 2) Browser lands here and is redirected to Google's consent screen.
		g.MapGet("/google/start", async (Guid login, RankedDb db, IOptions<RankedOptions> opt) =>
		{
			var o = opt.Value;
			if (string.IsNullOrEmpty(o.Auth.GoogleClientId))
				return Page("Google sign-in is not configured on this server yet.", false);

			var attempt = await db.LoginAttempts.FindAsync(login);
			if (attempt == null || attempt.Status != LoginStatus.Pending || attempt.ExpiresAt < DateTime.UtcNow)
				return Page("This login link has expired. Start the login again from the game.", false);

			var url = GoogleAuthUrl
				+ "?client_id=" + Uri.EscapeDataString(o.Auth.GoogleClientId)
				+ "&redirect_uri=" + Uri.EscapeDataString(RedirectUri(o))
				+ "&response_type=code"
				+ "&scope=" + Uri.EscapeDataString("openid email profile")
				+ "&prompt=select_account"
				+ "&state=" + attempt.Id;

			return Results.Redirect(url);
		});

		// 3) Google redirects back with an authorization code. We exchange it and verify the ID token.
		g.MapGet("/google/callback", async (string? code, string? state, string? error, RankedDb db,
			IOptions<RankedOptions> opt, IHttpClientFactory http, ILogger<LoginAttempt> log) =>
		{
			var o = opt.Value;
			if (!Guid.TryParse(state, out var loginId))
				return Page("Invalid login state.", false);

			var attempt = await db.LoginAttempts.FindAsync(loginId);
			if (attempt == null || attempt.Status != LoginStatus.Pending || attempt.ExpiresAt < DateTime.UtcNow)
				return Page("This login has expired. Start the login again from the game.", false);

			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
			{
				attempt.Status = LoginStatus.Failed;
				attempt.Error = error ?? "no_code";
				await db.SaveChangesAsync();
				return Page("Sign-in was cancelled.", false);
			}

			GoogleJsonWebSignature.Payload payload;
			try
			{
				var res = await http.CreateClient().PostAsync(GoogleTokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
				{
					["code"] = code,
					["client_id"] = o.Auth.GoogleClientId,
					["client_secret"] = o.Auth.GoogleClientSecret,
					["redirect_uri"] = RedirectUri(o),
					["grant_type"] = "authorization_code"
				}));

				var body = await res.Content.ReadAsStringAsync();
				if (!res.IsSuccessStatusCode)
					throw new InvalidOperationException($"Token exchange failed ({(int)res.StatusCode}): {body}");

				var idToken = JsonDocument.Parse(body).RootElement.GetProperty("id_token").GetString()!;
				payload = await GoogleJsonWebSignature.ValidateAsync(idToken, new GoogleJsonWebSignature.ValidationSettings
				{
					Audience = new[] { o.Auth.GoogleClientId }
				});
			}
			catch (Exception e)
			{
				log.LogWarning(e, "Google login failed");
				attempt.Status = LoginStatus.Failed;
				attempt.Error = "google_verification_failed";
				await db.SaveChangesAsync();
				return Page("Sign-in with Google failed. Please try again.", false);
			}

			var user = await db.Users.FirstOrDefaultAsync(u => u.GoogleSub == payload.Subject);
			if (user == null)
			{
				// Only Google's stable account id is kept. The player picks a public nickname afterwards.
				user = new User { GoogleSub = payload.Subject };
				db.Users.Add(user);
				db.Ratings.Add(new Rating { UserId = user.Id, Mmr = o.Matchmaking.InitialRating, Deviation = rating.InitialDeviation, Volatility = rating.InitialVolatility });
			}

			user.Email = payload.Email;
			user.LastSeenAt = DateTime.UtcNow;

			attempt.Status = LoginStatus.Complete;
			attempt.UserId = user.Id;
			attempt.IssuedToken = await IssueSession(db, user, o);
			await db.SaveChangesAsync();

			return Page("Signed in. You can close this tab and return to Shattered Paradise.", true);
		});

		// 4) Game client polls until the login completes. The token is handed out exactly once.
		g.MapPost("/login/{id:guid}/poll", async (Guid id, PollRequest req, RankedDb db) =>
		{
			var attempt = await db.LoginAttempts.FindAsync(id);
			if (attempt == null || !Tokens.FixedTimeEquals(attempt.PollSecretHash, Tokens.Hash(req.PollSecret ?? "")))
				return Results.NotFound();

			if (attempt.Status == LoginStatus.Pending && attempt.ExpiresAt < DateTime.UtcNow)
				return Results.Ok(new { status = "expired" });

			if (attempt.Status == LoginStatus.Failed)
				return Results.Ok(new { status = "failed", error = attempt.Error });

			if (attempt.Status == LoginStatus.Complete && attempt.IssuedToken != null)
			{
				var token = attempt.IssuedToken;
				attempt.IssuedToken = null;
				await db.SaveChangesAsync();
				return Results.Ok(new { status = "complete", token });
			}

			return Results.Ok(new { status = attempt.Status == LoginStatus.Complete ? "collected" : "pending" });
		});

		// Testing only: log in as a named fake user without Google.
		g.MapPost("/dev-login", async (DevLoginRequest req, RankedDb db, IOptions<RankedOptions> opt) =>
		{
			var o = opt.Value;
			if (!o.Auth.DevLogin)
				return Results.NotFound();

			var name = SanitizeName(req.Name);
			var key = name.ToLowerInvariant();
			var user = await db.Users.FirstOrDefaultAsync(u => u.NicknameKey == key);
			if (user != null && !user.IsDevUser)
				return Results.Json(new { error = "nickname_taken" }, statusCode: 409);

			if (user == null)
			{
				user = new User { DisplayName = name, NicknameKey = key, IsDevUser = true };
				db.Users.Add(user);
				db.Ratings.Add(new Rating { UserId = user.Id, Mmr = o.Matchmaking.InitialRating, Deviation = rating.InitialDeviation, Volatility = rating.InitialVolatility });
			}

			var token = await IssueSession(db, user, o);
			await db.SaveChangesAsync();
			return Results.Ok(new { status = "complete", token });
		});

		g.MapPost("/logout", async (HttpContext ctx, RankedDb db) =>
		{
			var header = ctx.Request.Headers.Authorization.ToString();
			if (header.StartsWith("Bearer ", StringComparison.Ordinal))
			{
				var hash = Tokens.Hash(header["Bearer ".Length..].Trim());
				await db.AuthSessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync();
			}

			return Results.NoContent();
		});
	}

	static string RedirectUri(RankedOptions o) => $"{o.PublicBaseUrl.TrimEnd('/')}/auth/google/callback";

	internal static Task<string> IssueSession(RankedDb db, User user, RankedOptions o)
	{
		var token = Tokens.NewSecret();
		db.AuthSessions.Add(new AuthSession
		{
			UserId = user.Id,
			TokenHash = Tokens.Hash(token),
			ExpiresAt = DateTime.UtcNow.AddDays(o.Auth.SessionDays)
		});

		return Task.FromResult(token);
	}

	// Player names end up in the game lobby; keep them short and free of odd characters.
	public static string SanitizeName(string? name)
	{
		var clean = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.').ToArray()).Trim();
		if (clean.Length > 16)
			clean = clean[..16].Trim();

		return clean.Length == 0 ? "Player" : clean;
	}

	internal static IResult Page(string message, bool ok) => Results.Content($$"""
		<!doctype html><html><head><meta charset="utf-8"><title>Shattered Paradise Ranked</title>
		<style>body{font-family:system-ui,sans-serif;background:#111;color:#eee;display:grid;place-items:center;height:100vh;margin:0}
		div{max-width:28rem;padding:2rem;border:1px solid {{(ok ? "#3a7" : "#a33")}};border-radius:8px;text-align:center}</style></head>
		<body><div><h2>{{(ok ? "Signed in" : "Sign-in problem")}}</h2><p>{{message}}</p></div></body></html>
		""", "text/html");
}

public record PollRequest(string? PollSecret);
public record DevLoginRequest(string? Name);
