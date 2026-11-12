using Microsoft.EntityFrameworkCore;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;

namespace Ranked.Backend.Api;

public static class LeaderboardEndpoints
{
	public static void Map(WebApplication app)
	{
		// Top players by rating, plus the caller's own row (if signed in and ranked).
		// Only players with at least one finished game appear.
		app.MapGet("/leaderboard", async (HttpContext ctx, RankedDb db, int? limit) =>
		{
			var me = await CurrentUser.GetAsync(ctx, db);
			var take = Math.Clamp(limit ?? 100, 1, 500);

			var ranked = db.Users.Where(u => u.NicknameKey != null && u.Rating!.Games > 0)
				.OrderByDescending(u => u.Rating!.Mmr).ThenByDescending(u => u.Rating!.Wins).ThenBy(u => u.Rating!.Games);

			var top = await ranked.Take(take)
				.Select(u => new { u.Id, name = u.DisplayName, mmr = u.Rating!.Mmr, games = u.Rating.Games, wins = u.Rating.Wins, losses = u.Rating.Losses })
				.ToListAsync();

			object? meRow = null;
			if (me?.Rating is { Games: > 0 } r)
			{
				var better = await db.Users.CountAsync(u => u.NicknameKey != null && u.Rating!.Games > 0 && u.Rating.Mmr > r.Mmr);
				meRow = new { rank = better + 1, name = me.DisplayName, mmr = r.Mmr, games = r.Games, wins = r.Wins, losses = r.Losses };
			}

			return Results.Ok(new
			{
				total = await ranked.CountAsync(),
				rows = top.Select((u, i) => new { rank = i + 1, u.name, u.mmr, u.games, u.wins, u.losses, isYou = u.Id == me?.Id }),
				me = meRow
			});
		});
	}
}
