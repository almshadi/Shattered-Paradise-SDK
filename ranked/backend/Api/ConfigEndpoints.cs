using Microsoft.Extensions.Options;
using Ranked.Backend.Auth;

namespace Ranked.Backend.Api;

// Public backend configuration the client reads on startup.
public static class ConfigEndpoints
{
	public static void Map(WebApplication app)
	{
		app.MapGet("/config", (HttpContext ctx, ModVersionReader versions, IOptions<RankedOptions> opt, IOptions<Notifications.DiscordOptions> discord) =>
		{
			var required = versions.Required;
			return Results.Ok(new
			{
				mode = opt.Value.Matchmaking.Mode,
				ratingUpdates = opt.Value.Matchmaking.RatingUpdates,
				devLogin = opt.Value.Auth.DevLogin,
				googleConfigured = !string.IsNullOrEmpty(opt.Value.Auth.GoogleClientId),
				discordConfigured = discord.Value.LoginConfigured,
				requiredClientVersion = required,
				clientDownloadUrl = opt.Value.ClientDownloadUrl,
				versionOk = ClientVersion.IsOk(required, ClientVersion.Of(ctx))
			});
		});
	}
}
