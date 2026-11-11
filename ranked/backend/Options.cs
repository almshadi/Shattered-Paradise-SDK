namespace Ranked.Backend;

public class RankedOptions
{
	// Externally reachable base URL of this backend (used for the Google redirect URI).
	public string PublicBaseUrl { get; set; } = "http://localhost:8090";

	// Shared secret between backend and the game servers it spawns.
	public string ServerSecret { get; set; } = "";

	// Clients must report exactly this version (empty = accept any). Prevents mismatched builds being matched.
	public string RequiredClientVersion { get; set; } = "";

	public AuthOptions Auth { get; set; } = new();
	public MatchmakingOptions Matchmaking { get; set; } = new();
	public GameServerOptions GameServers { get; set; } = new();
}

public class AuthOptions
{
	// Allows logging in as a named test user without Google. NEVER enable on a public server.
	public bool DevLogin { get; set; }
	public string GoogleClientId { get; set; } = "";
	public string GoogleClientSecret { get; set; } = "";
	public int SessionDays { get; set; } = 30;
}

public class MatchmakingOptions
{
	// "fifo": pair the two longest-waiting players, ignoring rating.
	// "mmr":  pair players whose rating difference fits a search window that widens over time.
	public string Mode { get; set; } = "fifo";

	// When false, results are still recorded but ratings never change (test games).
	public bool RatingUpdates { get; set; } = true;

	public double InitialRating { get; set; } = 1500;
	public double EloKFactor { get; set; } = 32;
	public double MmrInitialWindow { get; set; } = 100;
	public double MmrWindowGrowthPerSecond { get; set; } = 5;

	// Queue entries whose client stopped polling are dropped after this many seconds.
	public int QueueStaleSeconds { get; set; } = 30;

	// Match is cancelled if the game hasn't started within this time.
	public int MatchStartTimeoutSeconds { get; set; } = 300;

	// Upper bound for a running game; afterwards the server is killed and the match voided.
	public int MatchMaxDurationMinutes { get; set; } = 180;
}

public class GameServerOptions
{
	// Host/IP clients use to reach spawned game servers.
	public string PublicHost { get; set; } = "127.0.0.1";
	public int PortRangeStart { get; set; } = 12340;
	public int PortRangeEnd { get; set; } = 12360;

	// Root of the Shattered Paradise SDK checkout (contains engine/ and mods/), relative to the backend's content root.
	public string SdkDir { get; set; } = "../..";
	public string DotnetPath { get; set; } = "dotnet";

	// Backend URL as seen by the game server processes (usually local).
	public string BackendUrlForServers { get; set; } = "http://127.0.0.1:8090";

	// Map UIDs to pick from at random.
	public string[] MapPool { get; set; } = Array.Empty<string>();
}
