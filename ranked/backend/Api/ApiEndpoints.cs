using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;
using Ranked.Backend.Matchmaking;

namespace Ranked.Backend.Api;

// Endpoints used by the game client (Authorization: Bearer <session token>).
public static class ApiEndpoints
{
	public static void Map(WebApplication app)
	{
		app.MapGet("/me", async (HttpContext ctx, RankedDb db) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			await db.SaveChangesAsync();
			return Results.Ok(UserDto(user));
		});

		app.MapPut("/me/nickname", async (HttpContext ctx, NicknameRequest req, RankedDb db) =>
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
			return Results.Ok(UserDto(user));
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

		app.MapPost("/queue", async (HttpContext ctx, QueueRequest? req, RankedDb db, MatchmakingLock gate, IOptions<RankedOptions> opt) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			if (user.NeedsNickname)
				return Results.Json(new { error = "nickname_required" }, statusCode: 409);

			// The game server validates the faction against the map rules; here we only keep it sane.
			var faction = req?.Faction is { Length: > 0 and <= 32 } f && f.All(char.IsAsciiLetterOrDigit) ? f : "Random";
			var color = req?.Color is { Length: 6 } c && c.All(char.IsAsciiHexDigit) ? c.ToUpperInvariant() : null;

			var required = opt.Value.RequiredClientVersion;
			var version = ctx.Request.Headers["X-Client-Version"].ToString();
			if (!string.IsNullOrEmpty(required) && version != required)
				return Results.Json(new { error = "version_mismatch", required, yours = version }, statusCode: 409);

			await gate.Gate.WaitAsync();
			try
			{
				if (await ActiveMatchFor(db, user.Id) != null)
					return Results.Json(new { error = "already_in_match" }, statusCode: 409);

				var entry = await db.Queue.FindAsync(user.Id);
				if (entry == null)
					db.Queue.Add(new QueueEntry { UserId = user.Id, MmrSnapshot = user.Rating!.Mmr, Faction = faction, Color = color });
				else
				{
					entry.LastPollAt = DateTime.UtcNow;
					entry.Faction = faction;
					entry.Color = color;
				}

				await db.SaveChangesAsync();
			}
			finally
			{
				gate.Gate.Release();
			}

			return Results.Ok(new { state = "queued" });
		});

		app.MapDelete("/queue", async (HttpContext ctx, RankedDb db, MatchmakingLock gate) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			await gate.Gate.WaitAsync();
			try
			{
				await db.Queue.Where(q => q.UserId == user.Id).ExecuteDeleteAsync();
			}
			finally
			{
				gate.Gate.Release();
			}

			return Results.NoContent();
		});

		// Polled by the client every ~2s while searching. Also keeps the queue entry alive.
		app.MapGet("/queue/status", async (HttpContext ctx, RankedDb db) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			var match = await ActiveMatchFor(db, user.Id);
			if (match != null)
			{
				await db.SaveChangesAsync();
				var me = match.Players.Single(p => p.UserId == user.Id);
				var opponent = match.Players.Single(p => p.UserId != user.Id);
				return Results.Ok(new
				{
					state = match.Status == MatchStatus.Starting ? "match_starting" : "match_ready",
					match = new
					{
						id = match.Id,
						status = match.Status.ToString(),
						host = match.Host,
						port = match.Port,
						password = match.Password,
						ticket = me.Ticket,
						mapUid = match.MapUid,
						rated = match.Rated,
						opponent = new { name = opponent.User!.DisplayName, mmr = Math.Round(opponent.MmrBefore) }
					}
				});
			}

			var entry = await db.Queue.FindAsync(user.Id);
			if (entry == null)
			{
				await db.SaveChangesAsync();
				return Results.Ok(new { state = "idle" });
			}

			entry.LastPollAt = DateTime.UtcNow;
			await db.SaveChangesAsync();
			return Results.Ok(new
			{
				state = "queued",
				waitSeconds = (int)(DateTime.UtcNow - entry.JoinedAt).TotalSeconds,
				queueSize = await db.Queue.CountAsync()
			});
		});

		// The player backed out of a match before it started (left the lobby / closed the dialog).
		// The match is cancelled for both; nobody is re-queued automatically.
		app.MapPost("/matches/{id:guid}/leave", async (Guid id, HttpContext ctx, RankedDb db, MatchmakingLock gate, Matchmaker mm) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			await gate.Gate.WaitAsync();
			try
			{
				var m = await db.Matches.Include(x => x.Players).FirstOrDefaultAsync(x => x.Id == id);
				if (m == null || m.Players.All(p => p.UserId != user.Id))
					return Results.NotFound();

				// Once the game runs, leaving is handled by the game server (forfeit after disconnect).
				if (m.Status is MatchStatus.Starting or MatchStatus.WaitingForPlayers)
				{
					mm.Close(m, MatchStatus.Cancelled, $"left_lobby:{user.Id}");
					await db.SaveChangesAsync();
				}

				await db.Queue.Where(q => q.UserId == user.Id).ExecuteDeleteAsync();
				return Results.NoContent();
			}
			finally
			{
				gate.Gate.Release();
			}
		});

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

		app.MapGet("/config", (IOptions<RankedOptions> opt) => Results.Ok(new
		{
			mode = opt.Value.Matchmaking.Mode,
			ratingUpdates = opt.Value.Matchmaking.RatingUpdates,
			devLogin = opt.Value.Auth.DevLogin,
			googleConfigured = !string.IsNullOrEmpty(opt.Value.Auth.GoogleClientId),
			requiredClientVersion = opt.Value.RequiredClientVersion
		}));
	}

	public static Task<Match?> ActiveMatchFor(RankedDb db, Guid userId) =>
		db.Matches.Include(m => m.Players).ThenInclude(p => p.User)
			.Where(m => MatchStates.Active.Contains(m.Status) && m.Players.Any(p => p.UserId == userId))
			.OrderByDescending(m => m.CreatedAt)
			.FirstOrDefaultAsync();

	static object UserDto(User u) => new
	{
		id = u.Id,
		name = u.DisplayName,
		needsNickname = u.NeedsNickname,
		isDevUser = u.IsDevUser,
		mmr = u.Rating?.Mmr,
		games = u.Rating?.Games,
		wins = u.Rating?.Wins,
		losses = u.Rating?.Losses
	};
}

public record NicknameRequest(string? Nickname);
public record QueueRequest(string? Faction, string? Color);
