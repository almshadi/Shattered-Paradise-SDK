using Microsoft.Extensions.Options;
using Ranked.Backend.Data;

// Namespace is "Ratings" (not "Rating") so it does not shadow the Data.Rating entity inside Ranked.Backend.*.
namespace Ranked.Backend.Ratings;

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

// Applies the result of a finished 1v1 game to both players' ratings and match rows.
public class RatingService
{
	readonly RankedOptions options;

	public RatingService(IOptions<RankedOptions> options)
	{
		this.options = options.Value;
	}

	// Rating values only change for rated matches (Matchmaking:RatingUpdates); games/wins/losses always count.
	public void ApplyResult(Match m, MatchPlayer winner, MatchPlayer loser, Rating rw, Rating rl)
	{
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
	}
}
