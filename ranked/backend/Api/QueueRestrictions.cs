using Ranked.Backend.Data;

namespace Ranked.Backend.Api;

// Who may (not) join the queue: bans and dodge cooldowns. Also sanitises map vetoes.
public static class QueueRestrictions
{
	// Returns a 409 result when the user may not queue right now, otherwise null.
	public static IResult? Check(User user, DateTime now)
	{
		if (user.BannedUntil is { } bannedUntil && bannedUntil > now)
			return Results.Json(new
			{
				error = "banned",
				until = bannedUntil.Year >= 9999 ? (DateTime?)null : DateTime.SpecifyKind(bannedUntil, DateTimeKind.Utc),
				reason = user.BanReason
			}, statusCode: 409);

		if (user.QueueBlockedUntil is { } blockedUntil && blockedUntil > now)
			return Results.Json(new
			{
				error = "queue_cooldown",
				until = DateTime.SpecifyKind(blockedUntil, DateTimeKind.Utc),
				seconds = (int)Math.Ceiling((blockedUntil - now).TotalSeconds),
				dodges = user.DodgeCount
			}, statusCode: 409);

		return null;
	}

	// The user left a match before it started or never joined its game server. Opponents are not affected.
	public static void ApplyDodge(User user, QueueOptions options, DateTime now)
	{
		if (user.LastDodgeAt == null || user.LastDodgeAt < now.AddHours(-options.DodgeResetHours))
			user.DodgeCount = 0;

		user.DodgeCount++;
		user.LastDodgeAt = now;

		var penalties = options.Penalties;
		if (penalties.Length == 0)
			return;

		var minutes = penalties[Math.Min(user.DodgeCount - 1, penalties.Length - 1)];
		if (minutes > 0)
			user.QueueBlockedUntil = now.AddMinutes(minutes);
	}

	// Keeps only maps from the pool, without duplicates, at most `max`. Null when nothing is vetoed.
	public static string? SanitizeVetoes(IEnumerable<string?>? vetoes, string[] pool, int max)
	{
		if (vetoes == null || max <= 0)
			return null;

		var kept = vetoes.OfType<string>().Where(pool.Contains).Distinct().Take(max).ToList();
		return kept.Count > 0 ? string.Join(',', kept) : null;
	}
}
