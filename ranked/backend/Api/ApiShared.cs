using Microsoft.EntityFrameworkCore;
using Ranked.Backend.Data;
using Ranked.Backend.Matchmaking;

namespace Ranked.Backend.Api;

// Endpoints used by the game client (Authorization: Bearer <session token>) are split per feature:
// AccountEndpoints, QueueEndpoints, MatchEndpoints, LeaderboardEndpoints, ConfigEndpoints.
// Helpers and request records shared between them live here.
public static class ApiShared
{
	public static Task<Match?> ActiveMatchFor(RankedDb db, Guid userId) =>
		db.Matches.Include(m => m.Players).ThenInclude(p => p.User)
			.Where(m => MatchStates.Active.Contains(m.Status) && m.Players.Any(p => p.UserId == userId))
			.OrderByDescending(m => m.CreatedAt)
			.FirstOrDefaultAsync();

	public static object UserDto(User u) => new
	{
		id = u.Id,
		name = u.DisplayName,
		needsNickname = u.NeedsNickname,
		isDevUser = u.IsDevUser,
		mmr = u.Rating?.Mmr,
		games = u.Rating?.Games,
		wins = u.Rating?.Wins,
		losses = u.Rating?.Losses
	};
}

public record NicknameRequest(string? Nickname);
public record QueueRequest(string? Faction, string? Color);
