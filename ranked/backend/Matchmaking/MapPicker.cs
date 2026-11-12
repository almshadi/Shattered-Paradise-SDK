using Microsoft.Extensions.Options;
using Ranked.Backend.Data;

namespace Ranked.Backend.Matchmaking;

// Chooses the map for a new match. Currently: uniformly random from the configured pool.
public class MapPicker
{
	readonly RankedOptions options;

	public MapPicker(IOptions<RankedOptions> options)
	{
		this.options = options.Value;
	}

	// Returns the map UID for a match between these queue entries, or "" when the pool is empty.
	public string PickMap(IReadOnlyList<QueueEntry> players)
	{
		var pool = options.GameServers.MapPool;
		return pool.Length > 0 ? pool[Random.Shared.Next(pool.Length)] : "";
	}
}
