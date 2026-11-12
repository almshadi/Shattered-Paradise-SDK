using Microsoft.Extensions.Options;
using Ranked.Backend.Data;

namespace Ranked.Backend.Matchmaking;

// Chooses the map for a new match: uniformly random from the configured pool minus every player's vetoes.
public class MapPicker
{
	readonly RankedOptions options;

	public MapPicker(IOptions<RankedOptions> options)
	{
		this.options = options.Value;
	}

	// Returns the map UID for a match between these queue entries, or "" when the pool is empty.
	// If the vetoes together remove every map, the full pool is used instead.
	public string PickMap(IReadOnlyList<QueueEntry> players)
	{
		var pool = options.GameServers.MapPool;
		if (pool.Length == 0)
			return "";

		var vetoed = players
			.SelectMany(p => (p.MapVetoes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			.ToHashSet();

		var candidates = pool.Where(m => !vetoed.Contains(m)).ToArray();
		if (candidates.Length == 0)
			candidates = pool;

		return candidates[Random.Shared.Next(candidates.Length)];
	}
}
