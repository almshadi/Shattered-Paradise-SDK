namespace Ranked.Backend;

// Queue rules (section "Ranked:Queue"): dodge penalties and map vetoes.
public class QueueOptions
{
	public static readonly int[] DefaultDodgePenaltyMinutes = { 2, 5, 15 };

	// Queue cooldown in minutes for the 1st, 2nd, 3rd, ... dodge; the last value repeats. Empty = no penalty.
	// Nullable so a configured list replaces the defaults instead of being appended to them.
	public int[]? DodgePenaltyMinutes { get; set; }

	// The dodge counter resets when the previous dodge is older than this.
	public double DodgeResetHours { get; set; } = 24;

	// How many maps of the pool a player may veto.
	public int MaxMapVetoes { get; set; } = 2;

	public int[] Penalties => DodgePenaltyMinutes ?? DefaultDodgePenaltyMinutes;
}
