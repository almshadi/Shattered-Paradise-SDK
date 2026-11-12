namespace Ranked.Backend.Ratings;

// Bound to the "Ranked:Rating" config section (env Ranked__Rating__*).
public class RatingOptions
{
	public const int DefaultPlacementGames = 5;

	// Glicko-2 system constant: how much the volatility may change per game (0.3 - 1.2; lower = steadier).
	public double Tau { get; set; } = 0.5;

	// Starting values for new players (the rating itself comes from Matchmaking:InitialRating).
	public double InitialDeviation { get; set; } = 350;
	public double InitialVolatility { get; set; } = 0.06;

	// Inactivity: the deviation grows by one Glicko-2 rating period's worth for every this many days without a rated game.
	public double InactivityPeriodDays { get; set; } = 7;

	// A player appears on the leaderboard once they played this many games in the current season.
	public int PlacementGames { get; set; } = DefaultPlacementGames;

	// Season soft reset: Mmr = 1500 + (Mmr - 1500) * SoftResetFactor, Deviation = max(Deviation, SoftResetMinDeviation).
	public double SoftResetFactor { get; set; } = 0.5;
	public double SoftResetMinDeviation { get; set; } = 200;
}

public static class Placement
{
	public static int GamesLeft(Data.Rating? r, int placementGames) => Math.Max(0, placementGames - (r?.Games ?? 0));
	public static bool IsPlaced(Data.Rating? r, int placementGames) => r != null && r.Games >= placementGames;
}
