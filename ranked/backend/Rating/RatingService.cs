using Microsoft.Extensions.Options;
using Ranked.Backend.Data;

// Namespace is "Ratings" (not "Rating") so it does not shadow the Data.Rating entity inside Ranked.Backend.*.
namespace Ranked.Backend.Ratings;

// Applies the result of a finished 1v1 game to both players' ratings and match rows.
// Glicko-2 (see Glicko2.cs), one game = one rating period.
public class RatingService
{
	readonly RatingOptions options;
	readonly IServiceScopeFactory scopes;

	public RatingService(IOptions<RatingOptions> options, IServiceScopeFactory scopes)
	{
		this.options = options.Value;
		this.scopes = scopes;
	}

	// Rating values only change for rated matches (Matchmaking:RatingUpdates); games/wins/losses always count.
	public void ApplyResult(Match m, MatchPlayer winner, MatchPlayer loser, Rating rw, Rating rl)
	{
		var now = DateTime.UtcNow;
		var seasonId = ActiveSeasonId();
		m.SeasonId = seasonId;

		winner.MmrBefore = rw.Mmr;
		loser.MmrBefore = rl.Mmr;
		winner.DeviationBefore = rw.Deviation;
		loser.DeviationBefore = rl.Deviation;
		winner.Placement = !Placement.IsPlaced(rw, options.PlacementGames);
		loser.Placement = !Placement.IsPlaced(rl, options.PlacementGames);

		if (m.Rated)
		{
			// Inactivity first: uncertainty grows with the time since each player's last rated game.
			var w = Inflate(rw, now);
			var l = Inflate(rl, now);
			var newW = Glicko2.Rate(w, new GlickoGame(l.Rating, l.Deviation, 1), options.Tau);
			var newL = Glicko2.Rate(l, new GlickoGame(w.Rating, w.Deviation, 0), options.Tau);
			Store(rw, newW, now);
			Store(rl, newL, now);
		}

		winner.MmrAfter = rw.Mmr;
		loser.MmrAfter = rl.Mmr;
		winner.DeviationAfter = rw.Deviation;
		loser.DeviationAfter = rl.Deviation;

		// Unrated (test) matches leave the player's record alone: no games, wins/losses or placement progress.
		if (m.Rated)
		{
			rw.Games++; rw.Wins++; rw.SeasonId = seasonId;
			rl.Games++; rl.Losses++; rl.SeasonId = seasonId;
		}
	}

	GlickoRating Inflate(Rating r, DateTime now)
	{
		var days = (now - r.UpdatedAt).TotalDays;
		return new GlickoRating(r.Mmr, Glicko2.InflateDeviation(r.Deviation, r.Volatility, days, options.InactivityPeriodDays), r.Volatility);
	}

	static void Store(Rating r, GlickoRating g, DateTime now)
	{
		r.Mmr = Math.Round(g.Rating, 1);
		r.Deviation = Math.Round(g.Deviation, 2);
		r.Volatility = g.Volatility;
		r.UpdatedAt = now;
	}

	// ApplyResult is synchronous (called by the Matchmaker), so the active season is read via a short-lived context.
	int? ActiveSeasonId()
	{
		using var scope = scopes.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<RankedDb>();
		return db.Seasons.Where(s => s.IsActive).OrderByDescending(s => s.Id).Select(s => (int?)s.Id).FirstOrDefault();
	}
}
