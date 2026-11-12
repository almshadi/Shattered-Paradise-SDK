using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;
using Ranked.Backend.Ratings;

namespace Ranked.Backend.Api;

public static class LeaderboardEndpoints
{
	public static void Map(WebApplication app)
	{
		// All seasons, newest first.
		app.MapGet("/seasons", async (RankedDb db) =>
			Results.Ok(await db.Seasons.OrderByDescending(s => s.Id)
				.Select(s => new { id = s.Id, name = s.Name, startedAt = s.StartedAt, endedAt = s.EndedAt, isActive = s.IsActive })
				.ToListAsync()));

		// Top players by rating, plus the caller's own row (if signed in).
		// Current standings: only placed players (Rating:PlacementGames games this season) who are not banned.
		// ?season={id} of an ended season returns its archived final standings instead.
		app.MapGet("/leaderboard", async (HttpContext ctx, RankedDb db, IOptions<RatingOptions> ratingOptions, int? limit, int? season) =>
		{
			var me = await CurrentUser.GetAsync(ctx, db);
			var take = Math.Clamp(limit ?? 100, 1, 500);
			var placementGames = ratingOptions.Value.PlacementGames;

			var active = await db.Seasons.Where(s => s.IsActive).OrderByDescending(s => s.Id).FirstOrDefaultAsync();
			if (season != null && season != active?.Id)
			{
				var archived = await db.Seasons.FirstOrDefaultAsync(s => s.Id == season);
				if (archived == null)
					return Results.Json(new { error = "season_not_found" }, statusCode: 404);

				return Results.Ok(await Archived(db, archived, me, take));
			}

			var now = DateTime.UtcNow;
			var ranked = db.Users.Where(u => u.NicknameKey != null && u.Rating!.Games >= placementGames
					&& (u.BannedUntil == null || u.BannedUntil <= now))
				.OrderByDescending(u => u.Rating!.Mmr).ThenByDescending(u => u.Rating!.Wins).ThenBy(u => u.Rating!.Games);

			var top = await ranked.Take(take)
				.Select(u => new { u.Id, name = u.DisplayName, mmr = u.Rating!.Mmr, deviation = u.Rating.Deviation, games = u.Rating.Games, wins = u.Rating.Wins, losses = u.Rating.Losses })
				.ToListAsync();

			object? meRow = null;
			if (me?.Rating is { } r)
			{
				var placed = Placement.IsPlaced(r, placementGames);
				var banned = me.BannedUntil != null && me.BannedUntil > now;
				int? rank = null;
				if (placed && !banned && me.NicknameKey != null)
				{
					// Same ordering as the list: higher rating, or equal rating with more wins / fewer games.
					rank = 1 + await ranked.CountAsync(u => u.Rating!.Mmr > r.Mmr
						|| (u.Rating.Mmr == r.Mmr && (u.Rating.Wins > r.Wins || (u.Rating.Wins == r.Wins && u.Rating.Games < r.Games))));
				}

				meRow = new
				{
					rank,
					name = me.DisplayName,
					mmr = r.Mmr,
					deviation = r.Deviation,
					games = r.Games,
					wins = r.Wins,
					losses = r.Losses,
					placed,
					placementGamesLeft = Placement.GamesLeft(r, placementGames)
				};
			}

			return Results.Ok(new
			{
				season = active == null ? null : new { id = active.Id, name = active.Name },
				total = await ranked.CountAsync(),
				rows = top.Select((u, i) => new { rank = i + 1, u.name, u.mmr, u.deviation, u.games, u.wins, u.losses, isYou = u.Id == me?.Id }),
				me = meRow
			});
		});
	}

	// Frozen final standings of an ended season (see SeasonService).
	static async Task<object> Archived(RankedDb db, Season season, User? me, int take)
	{
		var standings = db.SeasonStandings.Where(s => s.SeasonId == season.Id);
		var top = await standings
			.Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { s.UserId, s.Rank, name = u.DisplayName, s.Mmr, s.Games, s.Wins, s.Losses })
			.OrderBy(s => s.Rank).Take(take)
			.ToListAsync();

		object? meRow = null;
		if (me != null)
		{
			var mine = await standings.FirstOrDefaultAsync(s => s.UserId == me.Id);
			if (mine != null)
				meRow = new { rank = mine.Rank, name = me.DisplayName, mmr = mine.Mmr, games = mine.Games, wins = mine.Wins, losses = mine.Losses, placed = true, placementGamesLeft = 0 };
		}

		return new
		{
			season = new { id = season.Id, name = season.Name },
			total = await standings.CountAsync(),
			rows = top.Select(s => new { rank = s.Rank, s.name, mmr = s.Mmr, games = s.Games, wins = s.Wins, losses = s.Losses, isYou = s.UserId == me?.Id }),
			me = meRow
		};
	}
}
