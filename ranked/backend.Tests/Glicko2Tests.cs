using Ranked.Backend.Ratings;
using Xunit;

namespace Ranked.Backend.Tests;

public class Glicko2Tests
{
	// Worked example from Glickman, "Example of the Glicko-2 system" (tau = 0.5).
	[Fact]
	public void ReproducesGlickmanPaperExample()
	{
		var player = new GlickoRating(1500, 200, 0.06);
		var games = new[]
		{
			new GlickoGame(1400, 30, 1),
			new GlickoGame(1550, 100, 0),
			new GlickoGame(1700, 300, 0)
		};

		var r = Glicko2.Rate(player, games, 0.5);

		Assert.Equal(1464.06, r.Rating, 0.01);
		Assert.Equal(151.52, r.Deviation, 0.01);
		Assert.Equal(0.05999, r.Volatility, 0.00001);
	}

	[Fact]
	public void NewPlayersMoveALotAndDeviationShrinks()
	{
		var a = new GlickoRating(1500, 350, 0.06);
		var b = new GlickoRating(1500, 350, 0.06);

		var winner = Glicko2.Rate(a, new GlickoGame(b.Rating, b.Deviation, 1), 0.5);
		var loser = Glicko2.Rate(b, new GlickoGame(a.Rating, a.Deviation, 0), 0.5);

		Assert.True(winner.Rating > 1600, $"winner {winner.Rating}");
		Assert.True(loser.Rating < 1400, $"loser {loser.Rating}");
		Assert.Equal(winner.Rating - 1500, 1500 - loser.Rating, 0.001);
		Assert.True(winner.Deviation < 350 && loser.Deviation < 350);
	}

	[Fact]
	public void InactivityGrowsDeviationUpToCap()
	{
		Assert.Equal(50, Glicko2.InflateDeviation(50, 0.06, 0), 0.0001);
		var fourWeeks = Glicko2.InflateDeviation(50, 0.06, 28);
		Assert.True(fourWeeks > 50 && fourWeeks < 60, $"{fourWeeks}");
		Assert.Equal(350, Glicko2.InflateDeviation(300, 0.06, 365 * 50), 0.0001);
	}
}
