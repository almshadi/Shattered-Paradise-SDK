using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;
using Ranked.Backend.Matchmaking;
using static Ranked.Backend.Api.ApiShared;

namespace Ranked.Backend.Api;

// Joining / leaving the matchmaking queue and backing out of a match before it starts.
public static class QueueEndpoints
{
	public static void Map(WebApplication app)
	{
		app.MapPost("/queue", async (HttpContext ctx, QueueRequest? req, RankedDb db, MatchmakingLock gate, ModVersionReader versions, IOptions<RankedOptions> opt) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			if (user.NeedsNickname)
				return Results.Json(new { error = "nickname_required" }, statusCode: 409);

			// The game server validates the faction against the map rules; here we only keep it sane.
			var faction = req?.Faction is { Length: > 0 and <= 32 } f && f.All(char.IsAsciiLetterOrDigit) ? f : "Random";
			var color = req?.Color is { Length: 6 } c && c.All(char.IsAsciiHexDigit) ? c.ToUpperInvariant() : null;

			if (ClientVersion.Check(ctx, versions, opt.Value) is { } mismatch)
				return mismatch;

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
		app.MapGet("/queue/status", async (HttpContext ctx, RankedDb db, MatchmakingLock gate, ModVersionReader versions, IOptions<RankedOptions> opt) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			// The server build changed (or the client did) while searching: drop the entry so the player is never matched.
			if (ClientVersion.Check(ctx, versions, opt.Value) is { } mismatch)
			{
				await db.SaveChangesAsync();
				await gate.Gate.WaitAsync();
				try
				{
					await db.Queue.Where(q => q.UserId == user.Id).ExecuteDeleteAsync();
				}
				finally
				{
					gate.Gate.Release();
				}

				return mismatch;
			}

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
	}
}
