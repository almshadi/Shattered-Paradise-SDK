// Namespace is "Ratings" (not "Rating") so it does not shadow the Data.Rating entity inside Ranked.Backend.*.
namespace Ranked.Backend.Ratings;

// Rating in the public Glicko scale (rating ~1500, deviation up to 350) plus Glicko-2 volatility.
public readonly record struct GlickoRating(double Rating, double Deviation, double Volatility);

// One game against an opponent: score 1 = win, 0 = loss, 0.5 = draw.
public readonly record struct GlickoGame(double OpponentRating, double OpponentDeviation, double Score);

// Glicko-2 as described in Mark Glickman, "Example of the Glicko-2 system" (2012).
public static class Glicko2
{
	public const double Scale = 173.7178;
	public const double BaseRating = 1500;
	public const double MaxDeviation = 350;
	const double ConvergenceTolerance = 0.000001;

	// Inactivity: before a rating period, the deviation grows with the time since the player's last rated game.
	// phi* = sqrt(phi^2 + sigma^2 * periods), periods = days / periodDays. Capped at MaxDeviation.
	public static double InflateDeviation(double deviation, double volatility, double days, double periodDays = 7)
	{
		if (days <= 0 || periodDays <= 0)
			return Math.Min(deviation, MaxDeviation);

		var phi = deviation / Scale;
		var inflated = Math.Sqrt(phi * phi + volatility * volatility * (days / periodDays)) * Scale;
		return Math.Min(inflated, MaxDeviation);
	}

	// One rating period containing a single game.
	public static GlickoRating Rate(GlickoRating player, GlickoGame game, double tau) =>
		Rate(player, new[] { game }, tau);

	// One rating period containing any number of games (an empty period only grows the deviation).
	public static GlickoRating Rate(GlickoRating player, IReadOnlyCollection<GlickoGame> games, double tau)
	{
		// Step 2: convert to the Glicko-2 scale.
		var mu = (player.Rating - BaseRating) / Scale;
		var phi = player.Deviation / Scale;
		var sigma = player.Volatility;

		if (games.Count == 0)
		{
			var idle = Math.Min(Math.Sqrt(phi * phi + sigma * sigma) * Scale, MaxDeviation);
			return player with { Deviation = idle };
		}

		// Step 3 + 4: estimated variance v and improvement delta.
		double vInv = 0, deltaSum = 0;
		foreach (var g in games)
		{
			var muJ = (g.OpponentRating - BaseRating) / Scale;
			var phiJ = g.OpponentDeviation / Scale;
			var gPhi = G(phiJ);
			var e = E(mu, muJ, gPhi);
			vInv += gPhi * gPhi * e * (1 - e);
			deltaSum += gPhi * (g.Score - e);
		}

		var v = 1 / vInv;
		var delta = v * deltaSum;

		// Step 5: new volatility (Illinois algorithm).
		var newSigma = NewVolatility(phi, sigma, v, delta, tau);

		// Step 6 + 7: new deviation and rating.
		var phiStar = Math.Sqrt(phi * phi + newSigma * newSigma);
		var newPhi = 1 / Math.Sqrt(1 / (phiStar * phiStar) + 1 / v);
		var newMu = mu + newPhi * newPhi * deltaSum;

		// Step 8: back to the Glicko scale.
		return new GlickoRating(newMu * Scale + BaseRating, Math.Min(newPhi * Scale, MaxDeviation), newSigma);
	}

	static double G(double phi) => 1 / Math.Sqrt(1 + 3 * phi * phi / (Math.PI * Math.PI));

	static double E(double mu, double muJ, double gPhiJ) => 1 / (1 + Math.Exp(-gPhiJ * (mu - muJ)));

	static double NewVolatility(double phi, double sigma, double v, double delta, double tau)
	{
		var a = Math.Log(sigma * sigma);
		var phi2 = phi * phi;
		var delta2 = delta * delta;

		double F(double x)
		{
			var ex = Math.Exp(x);
			var d = phi2 + v + ex;
			return ex * (delta2 - phi2 - v - ex) / (2 * d * d) - (x - a) / (tau * tau);
		}

		var A = a;
		double B;
		if (delta2 > phi2 + v)
			B = Math.Log(delta2 - phi2 - v);
		else
		{
			var k = 1;
			while (F(a - k * tau) < 0)
				k++;
			B = a - k * tau;
		}

		var fA = F(A);
		var fB = F(B);
		for (var i = 0; i < 1000 && Math.Abs(B - A) > ConvergenceTolerance; i++)
		{
			var C = A + (A - B) * fA / (fB - fA);
			var fC = F(C);
			if (fC * fB <= 0)
			{
				A = B;
				fA = fB;
			}
			else
				fA /= 2;

			B = C;
			fB = fC;
		}

		return Math.Exp(A / 2);
	}
}
