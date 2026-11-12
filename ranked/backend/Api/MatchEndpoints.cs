using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;
using Ranked.Backend.Replays;

namespace Ranked.Backend.Api;

// Match details, the player's recent matches / match history and replay downloads.
public static class MatchEndpoints
{
	public static void Map(WebApplication app)
	{
		app.MapGet("/matches/{id:guid}", async (Guid id, RankedDb db, ReplayStore replays) =>
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
				hasReplay = replays.PathFor(m.ReplayFile) != null,
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

		// Paged match history of the current player, newest first. Page with ?before=<createdAt of the last entry>.
		app.MapGet("/matches/history", async (HttpContext ctx, RankedDb db, int? limit, string? before) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			var take = Math.Clamp(limit ?? 20, 1, 100);
			var userId = user.Id;
			var query = db.MatchPlayers.Where(p => p.UserId == userId);
			if (!string.IsNullOrEmpty(before))
			{
				if (!DateTimeOffset.TryParse(before, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var beforeTime))
					return Results.BadRequest(new { error = "invalid_before" });

				var cutoff = beforeTime.UtcDateTime;
				query = query.Where(p => p.Match!.CreatedAt < cutoff);
			}

			// One round trip for the page plus one for the other players (split query), no N+1.
			var rows = await query
				.OrderByDescending(p => p.Match!.CreatedAt).Take(take)
				.Select(p => new
				{
					m = p.Match!,
					me = p,
					others = p.Match!.Players.Where(o => o.UserId != userId)
						.OrderBy(o => o.Team)
						.Select(o => new { o.User!.DisplayName, o.Faction, o.Result, o.Team })
						.ToList()
				})
				.AsNoTracking()
				.AsSplitQuery()
				.ToListAsync();

			return Results.Ok(rows.Select(r =>
			{
				// Factions are chosen secretly before queueing and only revealed once the game started.
				var started = r.m.StartedAt != null;
				return new
				{
					id = r.m.Id,
					createdAt = r.m.CreatedAt,
					startedAt = r.m.StartedAt,
					endedAt = r.m.EndedAt,
					durationSeconds = r.m.StartedAt != null && r.m.EndedAt != null
						? (int?)Math.Max(0, (int)Math.Round((r.m.EndedAt.Value - r.m.StartedAt.Value).TotalSeconds))
						: null,
					status = r.m.Status.ToString(),
					reason = r.m.StatusReason,
					rated = r.m.Rated,
					mapUid = r.m.MapUid,
					queueType = r.m.QueueType,
					me = new
					{
						faction = r.me.Faction,
						result = r.me.Result.ToString(),
						mmrBefore = r.me.MmrBefore,
						mmrAfter = r.me.MmrAfter,
						placement = r.me.Placement
					},
					opponents = r.others.Select(o => new
					{
						name = o.DisplayName,
						faction = started ? o.Faction : null,
						result = o.Result.ToString(),
						team = o.Team
					}),
					hasReplay = r.m.ReplayFile != null
				};
			}));
		});

		// The recorded replay (.orarep) of a match, if the game was played and the file is still kept.
		app.MapGet("/matches/{id:guid}/replay", async (Guid id, RankedDb db, ReplayStore replays) =>
		{
			var m = await db.Matches.AsNoTracking().Include(x => x.Players).ThenInclude(p => p.User)
				.FirstOrDefaultAsync(x => x.Id == id);
			var path = replays.PathFor(m?.ReplayFile);
			if (m == null || path == null)
				return Results.NotFound();

			return Results.File(path, "application/octet-stream", ReplayDownloadName(m));
		});
	}

	// e.g. "2026-11-13_Alice-vs-Bob.orarep" (team mates joined with "+" in team games).
	static string ReplayDownloadName(Match m)
	{
		// Without real team numbers (all players on team 0), every player is their own side.
		var sides = m.Players.Select(p => p.Team).Distinct().Count() > 1
			? m.Players.GroupBy(p => p.Team).OrderBy(g => g.Key).Select(g => g.ToList())
			: m.Players.OrderBy(p => p.User?.DisplayName).Select(p => new List<MatchPlayer> { p });
		var teams = sides.Select(g => string.Join("+", g.Select(p => SafeName(p.User?.DisplayName))));
		var date = (m.StartedAt ?? m.CreatedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		return $"{date}_{string.Join("-vs-", teams)}.orarep";
	}

	static string SafeName(string? name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var safe = new string((name ?? "").Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) || c == '"' ? '_' : c).ToArray());
		return safe.Length == 0 ? "player" : safe;
	}
}
