using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;
using Ranked.Backend.Matchmaking;

namespace Ranked.Backend.Api;

// Endpoints called by the dedicated game servers we spawn (X-Server-Secret header).
public static class ServerEndpoints
{
	public static void Map(WebApplication app)
	{
		var g = app.MapGroup("/server/matches/{id:guid}").AddEndpointFilter(async (ctx, next) =>
		{
			var secret = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<RankedOptions>>().Value.ServerSecret;
			var given = ctx.HttpContext.Request.Headers["X-Server-Secret"].ToString();
			if (string.IsNullOrEmpty(secret) || !Tokens.FixedTimeEquals(secret, given))
				return Results.Unauthorized();

			return await next(ctx);
		});

		// The server learns who may join. It gets ticket hashes only, never the tickets themselves.
		g.MapGet("", async (Guid id, RankedDb db) =>
		{
			var m = await Load(db, id);
			if (m == null)
				return Results.NotFound();

			return Results.Ok(new
			{
				id = m.Id,
				status = m.Status.ToString(),
				mapUid = m.MapUid,
				rated = m.Rated,
				players = m.Players.Select(p => new
				{
					userId = p.UserId,
					name = p.User!.DisplayName,
					ticketHash = Tokens.Hash(p.Ticket),
					faction = p.Faction ?? "Random",
					color = p.Color
				})
			});
		});

		// Server is listening; clients may now connect.
		g.MapPost("/ready", async (Guid id, RankedDb db, MatchmakingLock gate) =>
			await Locked(gate, async () =>
			{
				var m = await Load(db, id);
				if (m == null || m.Status != MatchStatus.Starting)
					return Results.Conflict();

				m.Status = MatchStatus.WaitingForPlayers;
				await db.SaveChangesAsync();
				return Results.NoContent();
			}));

		g.MapPost("/joined", async (Guid id, PlayerEvent e, RankedDb db, MatchmakingLock gate) =>
			await Locked(gate, async () =>
			{
				var m = await Load(db, id);
				var p = m?.Players.SingleOrDefault(x => x.UserId == e.UserId);
				if (p == null)
					return Results.NotFound();

				p.Joined = true;
				await db.SaveChangesAsync();
				return Results.NoContent();
			}));

		g.MapPost("/started", async (Guid id, StartedEvent e, RankedDb db, MatchmakingLock gate) =>
			await Locked(gate, async () =>
			{
				var m = await Load(db, id);
				if (m == null || m.Status != MatchStatus.WaitingForPlayers)
					return Results.Conflict();

				m.Status = MatchStatus.InProgress;
				m.StartedAt = DateTime.UtcNow;
				foreach (var f in e.Factions ?? new())
				{
					var p = m.Players.SingleOrDefault(x => x.UserId == f.UserId);
					if (p != null)
						p.Faction = f.Faction;
				}

				await db.SaveChangesAsync();
				return Results.NoContent();
			}));

		g.MapPost("/result", async (Guid id, ResultEvent e, RankedDb db, MatchmakingLock gate, Matchmaker mm) =>
			await Locked(gate, async () =>
			{
				var m = await Load(db, id);
				if (m == null || m.Status != MatchStatus.InProgress || m.Players.All(p => p.UserId != e.WinnerUserId))
					return Results.Conflict();

				await mm.Complete(db, m, e.WinnerUserId, e.Reason ?? "defeat");
				await db.SaveChangesAsync();
				return Results.NoContent();
			}));

		g.MapPost("/abort", async (Guid id, AbortEvent e, RankedDb db, MatchmakingLock gate, Matchmaker mm) =>
			await Locked(gate, async () =>
			{
				var m = await Load(db, id);
				if (m == null || !MatchStates.Active.Contains(m.Status))
					return Results.Conflict();

				mm.Close(m, m.Status == MatchStatus.InProgress ? MatchStatus.Void : MatchStatus.Cancelled, e.Reason ?? "aborted");
				await db.SaveChangesAsync();
				return Results.NoContent();
			}));
	}

	static Task<Match?> Load(RankedDb db, Guid id) =>
		db.Matches.Include(m => m.Players).ThenInclude(p => p.User).FirstOrDefaultAsync(m => m.Id == id);

	static async Task<IResult> Locked(MatchmakingLock gate, Func<Task<IResult>> action)
	{
		await gate.Gate.WaitAsync();
		try
		{
			return await action();
		}
		finally
		{
			gate.Gate.Release();
		}
	}
}

public record PlayerEvent(Guid UserId);
public record FactionChoice(Guid UserId, string Faction);
public record StartedEvent(List<FactionChoice>? Factions);
public record ResultEvent(Guid WinnerUserId, string? Reason);
public record AbortEvent(string? Reason);
