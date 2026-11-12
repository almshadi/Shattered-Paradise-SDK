using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;
using Ranked.Backend.Ratings;
using static Ranked.Backend.Api.ApiShared;

namespace Ranked.Backend.Api;

// The signed-in player's own account: profile, nickname, deletion.
public static class AccountEndpoints
{
	public static void Map(WebApplication app)
	{
		app.MapGet("/me", async (HttpContext ctx, RankedDb db, IOptions<RatingOptions> rating) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			await db.SaveChangesAsync();
			return Results.Ok(UserDto(user, rating.Value.PlacementGames));
		});

		app.MapPut("/me/nickname", async (HttpContext ctx, NicknameRequest req, RankedDb db, IOptions<RatingOptions> rating) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			var nick = (req.Nickname ?? "").Trim();
			if (nick.Length < 3 || nick.Length > 16 || !nick.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
				return Results.Json(new { error = "invalid_nickname" }, statusCode: 400);

			var key = nick.ToLowerInvariant();
			if (await db.Users.AnyAsync(u => u.NicknameKey == key && u.Id != user.Id))
				return Results.Json(new { error = "nickname_taken" }, statusCode: 409);

			user.DisplayName = nick;
			user.NicknameKey = key;
			await db.SaveChangesAsync();
			return Results.Ok(UserDto(user, rating.Value.PlacementGames));
		});

		// GDPR: lets a player remove their account and all their data.
		app.MapDelete("/me", async (HttpContext ctx, RankedDb db) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			db.Users.Remove(user);
			await db.SaveChangesAsync();
			return Results.NoContent();
		});
	}
}
