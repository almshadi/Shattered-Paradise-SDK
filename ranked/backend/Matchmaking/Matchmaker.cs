using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;

namespace Ranked.Backend.Matchmaking;

// Serialises all queue/match mutations. A single backend instance is assumed for the prototype.
public class MatchmakingLock
{
	public readonly SemaphoreSlim Gate = new(1, 1);
}

public static class MatchStates
{
	public static readonly MatchStatus[] Active = { MatchStatus.Starting, MatchStatus.WaitingForPlayers, MatchStatus.InProgress };
}

// Elo for now. Kept behind one method so Glicko-2 can replace it later without touching callers.
public static class RatingSystem
{
	public static (double Winner, double Loser) Elo(double winner, double loser, double k)
	{
		var expectedWin = 1.0 / (1.0 + Math.Pow(10, (loser - winner) / 400.0));
		var delta = k * (1 - expectedWin);
		return (winner + delta, loser - delta);
	}
}

public class Matchmaker : BackgroundService
{
	readonly IServiceScopeFactory scopes;
	readonly GameServerManager servers;
	readonly MatchmakingLock gate;
	readonly RankedOptions options;
	readonly ILogger<Matchmaker> log;

	public Matchmaker(IServiceScopeFactory scopes, GameServerManager servers, MatchmakingLock gate,
		IOptions<RankedOptions> options, ILogger<Matchmaker> log)
	{
		this.scopes = scopes;
		this.servers = servers;
		this.gate = gate;
		this.options = options.Value;
		this.log = log;
	}

	protected override async Task ExecuteAsync(CancellationToken stop)
	{
		log.LogInformation("Matchmaker running: mode={Mode}, ratingUpdates={Rated}",
			options.Matchmaking.Mode, options.Matchmaking.RatingUpdates);

		await CleanupAfterRestart();

		while (!stop.IsCancellationRequested)
		{
			try
			{
				await gate.Gate.WaitAsync(stop);
				try
				{
					using var scope = scopes.CreateScope();
					var db = scope.ServiceProvider.GetRequiredService<RankedDb>();
					await DropStaleQueueEntries(db);
					await CreateMatches(db);
					await ExpireMatches(db);
				}
				finally
				{
					gate.Gate.Release();
				}
			}
			catch (OperationCanceledException) when (stop.IsCancellationRequested)
			{
				break;
			}
			catch (Exception e)
			{
				log.LogError(e, "Matchmaker tick failed");
			}

			await Task.Delay(1000, stop).ContinueWith(_ => { });
		}
	}

	// Game server processes don't survive a backend restart; close any matches that were still open.
	async Task CleanupAfterRestart()
	{
		using var scope = scopes.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<RankedDb>();
		var open = await db.Matches.Where(m => MatchStates.Active.Contains(m.Status)).ToListAsync();
		foreach (var m in open)
			Close(m, m.Status == MatchStatus.InProgress ? MatchStatus.Void : MatchStatus.Cancelled, "backend_restarted");

		await db.Queue.ExecuteDeleteAsync();
		await db.SaveChangesAsync();
	}

	async Task DropStaleQueueEntries(RankedDb db)
	{
		var cutoff = DateTime.UtcNow.AddSeconds(-options.Matchmaking.QueueStaleSeconds);
		var removed = await db.Queue.Where(q => q.LastPollAt < cutoff).ExecuteDeleteAsync();
		if (removed > 0)
			log.LogInformation("Dropped {Count} stale queue entries", removed);
	}

	async Task CreateMatches(RankedDb db)
	{
		var queue = await db.Queue.Include(q => q.User).OrderBy(q => q.JoinedAt).ToListAsync();
		while (queue.Count >= 2)
		{
			var pair = options.Matchmaking.Mode.Equals("mmr", StringComparison.OrdinalIgnoreCase)
				? FindMmrPair(queue)
				: (queue[0], queue[1]);

			if (pair == null)
				break;

			var (a, b) = pair.Value;
			queue.Remove(a);
			queue.Remove(b);
			await CreateMatch(db, a, b);
		}
	}

	// Pairs the longest-waiting player with the closest-rated opponent inside its search window.
	// The window grows the longer someone waits, so nobody waits forever in a small population.
	(QueueEntry, QueueEntry)? FindMmrPair(List<QueueEntry> queue)
	{
		var mm = options.Matchmaking;
		var now = DateTime.UtcNow;
		double Window(QueueEntry q) => mm.MmrInitialWindow + mm.MmrWindowGrowthPerSecond * (now - q.JoinedAt).TotalSeconds;

		foreach (var a in queue)
		{
			var best = queue.Where(b => b != a)
				.Select(b => (b, diff: Math.Abs(a.MmrSnapshot - b.MmrSnapshot)))
				.Where(x => x.diff <= Math.Min(Window(a), Window(x.b)))
				.OrderBy(x => x.diff)
				.FirstOrDefault();

			if (best.b != null)
				return (a, best.b);
		}

		return null;
	}

	async Task CreateMatch(RankedDb db, QueueEntry a, QueueEntry b)
	{
		var gs = options.GameServers;
		var match = new Match
		{
			Status = MatchStatus.Starting,
			MatchmakingMode = options.Matchmaking.Mode,
			Rated = options.Matchmaking.RatingUpdates,
			MapUid = gs.MapPool.Length > 0 ? gs.MapPool[Random.Shared.Next(gs.MapPool.Length)] : "",
			Host = gs.PublicHost,
			Password = Tokens.NewSecret(12)
		};

		foreach (var q in new[] { a, b })
			match.Players.Add(new MatchPlayer
			{
				UserId = q.UserId,
				Ticket = Tokens.NewSecret(),
				MmrBefore = q.MmrSnapshot,
				Faction = q.Faction,
				Color = q.Color
			});

		db.Matches.Add(match);
		db.Queue.RemoveRange(a, b);

		var port = servers.AllocatePort();
		if (port == null)
		{
			Close(match, MatchStatus.Cancelled, "no_free_server_port");
		}
		else
		{
			match.Port = port.Value;
			try
			{
				// Stays "Starting" until the server calls /ready, so clients don't connect before it listens.
				servers.Start(match.Id, match.Port, match.Password, match.MapUid);
			}
			catch (Exception e)
			{
				log.LogError(e, "Could not start game server");
				Close(match, MatchStatus.Cancelled, "server_start_failed");
			}
		}

		await db.SaveChangesAsync();
		log.LogInformation("Match {Match}: {A} vs {B} ({Mode}, rated={Rated}) -> {Status}",
			match.Id, a.User?.DisplayName, b.User?.DisplayName, match.MatchmakingMode, match.Rated, match.Status);
	}

	async Task ExpireMatches(RankedDb db)
	{
		var now = DateTime.UtcNow;
		var mm = options.Matchmaking;
		var open = await db.Matches.Where(m => MatchStates.Active.Contains(m.Status)).ToListAsync();
		foreach (var m in open)
		{
			if (m.Status != MatchStatus.InProgress && m.CreatedAt < now.AddSeconds(-mm.MatchStartTimeoutSeconds))
				Close(m, MatchStatus.Cancelled, "start_timeout");
			else if (m.Status == MatchStatus.InProgress && m.StartedAt < now.AddMinutes(-mm.MatchMaxDurationMinutes))
				Close(m, MatchStatus.Void, "max_duration");
			else if (!servers.IsRunning(m.Id))
				Close(m, m.Status == MatchStatus.InProgress ? MatchStatus.Void : MatchStatus.Cancelled, "server_exited");
		}

		await db.SaveChangesAsync();
	}

	public void Close(Match m, MatchStatus status, string reason)
	{
		m.Status = status;
		m.StatusReason = reason;
		m.EndedAt = DateTime.UtcNow;
		servers.Stop(m.Id);
		log.LogInformation("Match {Match} closed: {Status} ({Reason})", m.Id, status, reason);
	}

	// Records a finished game and, if the match is rated, updates both ratings.
	public async Task Complete(RankedDb db, Match m, Guid winnerId, string reason)
	{
		var winner = m.Players.Single(p => p.UserId == winnerId);
		var loser = m.Players.Single(p => p.UserId != winnerId);
		winner.Result = MatchResult.Win;
		loser.Result = MatchResult.Loss;

		var ratings = await db.Ratings.Where(r => r.UserId == winner.UserId || r.UserId == loser.UserId).ToDictionaryAsync(r => r.UserId);
		var rw = ratings[winner.UserId];
		var rl = ratings[loser.UserId];

		if (m.Rated)
		{
			var (newW, newL) = RatingSystem.Elo(rw.Mmr, rl.Mmr, options.Matchmaking.EloKFactor);
			rw.Mmr = Math.Round(newW, 1);
			rl.Mmr = Math.Round(newL, 1);
		}

		winner.MmrAfter = rw.Mmr;
		loser.MmrAfter = rl.Mmr;
		rw.Games++; rw.Wins++; rw.UpdatedAt = DateTime.UtcNow;
		rl.Games++; rl.Losses++; rl.UpdatedAt = DateTime.UtcNow;

		m.Status = MatchStatus.Completed;
		m.StatusReason = reason;
		m.EndedAt = DateTime.UtcNow;

		servers.StopLater(m.Id, TimeSpan.FromMinutes(15));
		log.LogInformation("Match {Match} completed: winner {Winner} ({Reason}), rated={Rated}", m.Id, winner.UserId, reason, m.Rated);
	}
}
