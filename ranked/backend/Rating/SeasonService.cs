using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Data;

namespace Ranked.Backend.Ratings;

// Ranked seasons: archiving final standings, ending a season and the soft rating reset when a new one starts.
// Unlike the interface's general rule, StartNewAsync / EndActiveAsync save the context themselves (inside a transaction
// unless the caller already opened one), because the new season's id is needed to stamp Rating.SeasonId.
public sealed class SeasonService : ISeasonService
{
	readonly RatingOptions options;

	public SeasonService(IOptions<RatingOptions> options)
	{
		this.options = options.Value;
	}

	public Task<Season?> GetActiveAsync(RankedDb db) =>
		db.Seasons.Where(s => s.IsActive).OrderByDescending(s => s.Id).FirstOrDefaultAsync();

	public async Task<Season> StartNewAsync(RankedDb db, string name)
	{
		name = (name ?? "").Trim();
		if (name.Length == 0)
			throw new ArgumentException("Season name must not be empty.", nameof(name));

		var tx = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync() : null;
		try
		{
			await ArchiveAndEndActive(db);

			var season = new Season { Name = name, StartedAt = DateTime.UtcNow, IsActive = true };
			db.Seasons.Add(season);
			await db.SaveChangesAsync();

			// Soft reset: ratings move halfway back to 1500, uncertainty is raised so placements matter again,
			// and the season record starts from zero (everyone plays placement games again).
			var ratings = await db.Ratings.ToListAsync();
			foreach (var r in ratings)
			{
				r.Mmr = Math.Round(Glicko2.BaseRating + (r.Mmr - Glicko2.BaseRating) * options.SoftResetFactor, 1);
				r.Deviation = Math.Min(Math.Max(r.Deviation, options.SoftResetMinDeviation), Glicko2.MaxDeviation);
				r.Games = 0;
				r.Wins = 0;
				r.Losses = 0;
				r.SeasonId = season.Id;
			}

			await db.SaveChangesAsync();
			if (tx != null)
				await tx.CommitAsync();

			return season;
		}
		finally
		{
			if (tx != null)
				await tx.DisposeAsync();
		}
	}

	public async Task EndActiveAsync(RankedDb db)
	{
		var tx = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync() : null;
		try
		{
			await ArchiveAndEndActive(db);
			await db.SaveChangesAsync();
			if (tx != null)
				await tx.CommitAsync();
		}
		finally
		{
			if (tx != null)
				await tx.DisposeAsync();
		}
	}

	// Freezes the leaderboard of every active season (normally exactly one) into SeasonStandings and ends it.
	async Task ArchiveAndEndActive(RankedDb db)
	{
		var active = await db.Seasons.Where(s => s.IsActive).ToListAsync();
		if (active.Count == 0)
			return;

		var now = DateTime.UtcNow;
		var placementGames = options.PlacementGames;
		var standings = await db.Users
			.Where(u => u.NicknameKey != null && u.Rating != null && u.Rating.Games >= placementGames
				&& (u.BannedUntil == null || u.BannedUntil <= now))
			.OrderByDescending(u => u.Rating!.Mmr).ThenByDescending(u => u.Rating!.Wins).ThenBy(u => u.Rating!.Games)
			.Select(u => new { u.Id, u.Rating!.Mmr, u.Rating.Games, u.Rating.Wins, u.Rating.Losses })
			.ToListAsync();

		// Standings belong to the newest active season; any stray older active seasons are just closed.
		var current = active.OrderByDescending(s => s.Id).First();
		var existing = await db.SeasonStandings.Where(s => s.SeasonId == current.Id).ToListAsync();
		db.SeasonStandings.RemoveRange(existing);
		db.SeasonStandings.AddRange(standings.Select((s, i) => new SeasonStanding
		{
			SeasonId = current.Id,
			UserId = s.Id,
			Rank = i + 1,
			Mmr = s.Mmr,
			Games = s.Games,
			Wins = s.Wins,
			Losses = s.Losses
		}));

		foreach (var s in active)
		{
			s.IsActive = false;
			s.EndedAt = now;
		}
	}
}
