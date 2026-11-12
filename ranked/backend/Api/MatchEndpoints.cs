using Microsoft.EntityFrameworkCore;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;

namespace Ranked.Backend.Api;

// Match details and the player's recent matches.
public static class MatchEndpoints
{
	public static void Map(WebApplication app)
	{
		app.MapGet("/matches/{id:guid}", async (Guid id, RankedDb db) =>
		{
			var m = await db.Matches.Include(x => x.Players).ThenInclude(p => p.User).FirstOrDefaultAsync(x => x.Id == id);
			if (m == null)
				return Results.NotFound();

			return Results.Ok(new
			{
				id = m.Id,
				status = m.Status.ToString(),
				reason = m.StatusReason,
				rated = m.Rated,
				mode = m.MatchmakingMode,
				mapUid = m.MapUid,
				createdAt = m.CreatedAt,
				endedAt = m.EndedAt,
				players = m.Players.Select(p => new
				{
					userId = p.UserId,
					name = p.User!.DisplayName,
					result = p.Result.ToString(),
					mmrBefore = p.MmrBefore,
					mmrAfter = p.MmrAfter
				})
			});
		});

		app.MapGet("/matches/recent", async (HttpContext ctx, RankedDb db) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			var matches = await db.MatchPlayers.Where(p => p.UserId == user.Id)
				.OrderByDescending(p => p.Match!.CreatedAt).Take(10)
				.Select(p => new
				{
					id = p.MatchId,
					status = p.Match!.Status.ToString(),
					reason = p.Match.StatusReason,
					leftByMe = p.Match.StatusReason == "left_lobby:" + user.Id,
					result = p.Result.ToString(),
					mmrBefore = p.MmrBefore,
					mmrAfter = p.MmrAfter,
					endedAt = p.Match.EndedAt
				}).ToListAsync();

			return Results.Ok(matches);
		});
	}
}
