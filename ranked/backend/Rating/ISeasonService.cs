using Ranked.Backend.Data;

namespace Ranked.Backend.Ratings;

// Ranked seasons. The active season (if any) is the one with IsActive = true.
// Callers own the DbContext. StartNewAsync / EndActiveAsync save it themselves (transactionally), see SeasonService.
public interface ISeasonService
{
	Task<Season?> GetActiveAsync(RankedDb db);

	// Archives standings of the current active season (if any) into SeasonStandings, ends it,
	// applies the soft rating reset and starts a new active season with the given name.
	Task<Season> StartNewAsync(RankedDb db, string name);

	// Archives standings and ends the active season without starting a new one.
	Task EndActiveAsync(RankedDb db);
}

// Placeholder until the real implementation lands: no seasons.
public sealed class NoSeasonService : ISeasonService
{
	public Task<Season?> GetActiveAsync(RankedDb db) => Task.FromResult<Season?>(null);
	public Task<Season> StartNewAsync(RankedDb db, string name) => throw new NotSupportedException("Seasons are not implemented yet.");
	public Task EndActiveAsync(RankedDb db) => throw new NotSupportedException("Seasons are not implemented yet.");
}
