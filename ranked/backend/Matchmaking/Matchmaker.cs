using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Api;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;
using Ranked.Backend.Ratings;

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

public class Matchmaker : BackgroundService
{
	readonly IServiceScopeFactory scopes;
	readonly GameServerManager servers;
	readonly MapPicker maps;
	readonly RatingService ratingService;
	readonly MatchmakingLock gate;
	readonly RankedOptions options;
	readonly QueueOptions queueOptions;
	readonly ILogger<Matchmaker> log;

	public Matchmaker(IServiceScopeFactory scopes, GameServerManager servers, MapPicker maps, RatingService ratingService,
		MatchmakingLock gate, IOptions<RankedOptions> options, IOptions<QueueOptions> queueOptions, ILogger<Matchmaker> log)
	{
		this.scopes = scopes;
		this.servers = servers;
		this.maps = maps;
		this.ratingService = ratingService;
		this.gate = gate;
		this.options = options.Value;
		this.queueOptions = queueOptions.Value;
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
		await servers.DeleteStaleSupportDirs(db);
	}

	async Task DropStaleQueueEntries(RankedDb db)
	{
		var now = DateTime.UtcNow;
		var cutoff = now.AddSeconds(-options.Matchmaking.QueueStaleSeconds);

		// Also drops players who were banned while searching.
		var removed = await db.Queue.Where(q => q.LastPollAt < cutoff || q.User!.BannedUntil > now).ExecuteDeleteAsync();
		if (removed > 0)
			log.LogInformation("Dropped {Count} stale queue entries", removed);
	}

	async Task CreateMatches(RankedDb db)
	{
		var queue = await db.Queue.Include(q => q.User).OrderBy(q => q.JoinedAt).ToListAsync();
		while (queue.Count >= 2)
		{
			// All game server ports busy: keep everyone searching instead of creating a match that is cancelled at once.
			if (servers.AllocatePort() == null)
			{
				log.LogDebug("No free game server port; {Count} players keep waiting", queue.Count);
				break;
			}

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
			MapUid = maps.PickMap(new[] { a, b }),
			Host = gs.PublicHost,
			Password = Tokens.NewSecret(12)
		};

		// 1v1: each player is their own team (0 and 1); history, replay names and Discord posts group by team.
		foreach (var (q, team) in new[] { (a, 0), (b, 1) })
			match.Players.Add(new MatchPlayer
			{
				UserId = q.UserId,
				Team = team,
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
		var open = await db.Matches.Include(m => m.Players).ThenInclude(p => p.User)
			.Where(m => MatchStates.Active.Contains(m.Status)).ToListAsync();
		foreach (var m in open)
		{
			if (m.Status != MatchStatus.InProgress && m.CreatedAt < now.AddSeconds(-mm.MatchStartTimeoutSeconds))
			{
				Close(m, MatchStatus.Cancelled, "start_timeout");

				// Matched but never connected to the game server: a dodge. Players who did join are not penalised.
				foreach (var p in m.Players.Where(p => !p.Joined))
				{
					QueueRestrictions.ApplyDodge(p.User!, queueOptions, now);
					log.LogInformation("Match {Match}: {User} never joined (dodge #{Count})", m.Id, p.User!.DisplayName, p.User.DodgeCount);
				}
			}
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

		ratingService.ApplyResult(m, winner, loser, rw, rl);

		m.Status = MatchStatus.Completed;
		m.StatusReason = reason;
		m.EndedAt = DateTime.UtcNow;

		servers.StopLater(m.Id, TimeSpan.FromMinutes(15));
		log.LogInformation("Match {Match} completed: winner {Winner} ({Reason}), rated={Rated}", m.Id, winner.UserId, reason, m.Rated);
	}
}
