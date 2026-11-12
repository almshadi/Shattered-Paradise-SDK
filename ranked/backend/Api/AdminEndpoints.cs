using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ranked.Backend.Admin;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;
using Ranked.Backend.Matchmaking;
using Ranked.Backend.Ratings;

namespace Ranked.Backend.Api;

// Moderation / maintenance API for admins (Authorization: Bearer <session token>).
// Everything except GET /admin/me requires an admin (see AdminAuth); every mutating call writes an AdminAction row.
public static class AdminEndpoints
{
	public const double MinMmr = 0, MaxMmr = 5000, MinDeviation = 30, MaxDeviation = 350;
	const int MaxReasonLength = 500, MaxSeasonNameLength = 64;
	const int MaxBanMinutes = 10 * 365 * 24 * 60;

	// "Permanent" ban.
	public static readonly DateTime PermanentBan = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

	public static void Map(WebApplication app)
	{
		var root = app.MapGroup("/admin");

		// Lets the client / web page decide whether to show admin tools. 200 for any signed-in user.
		root.MapGet("/me", async (HttpContext ctx, RankedDb db) =>
		{
			var user = await CurrentUser.GetAsync(ctx, db);
			if (user == null)
				return Results.Unauthorized();

			await db.SaveChangesAsync();
			return Results.Ok(new { isAdmin = AdminAuth.IsAdmin(user, AdminAuth.Options(ctx)), name = user.DisplayName });
		});

		var g = root.MapGroup("").AddEndpointFilter(AdminAuth.Filter);

		// ---- Users ----

		g.MapGet("/users", async (HttpContext ctx, RankedDb db, string? search, int? limit) =>
		{
			var q = db.Users.Include(u => u.Rating).AsQueryable();
			var s = search?.Trim();
			if (!string.IsNullOrEmpty(s))
			{
				if (Guid.TryParse(s, out var id))
					q = q.Where(u => u.Id == id);
				else
				{
					var lower = s.ToLowerInvariant();
					q = q.Where(u => u.DisplayName.ToLower().Contains(lower)
						|| (u.Email != null && u.Email.ToLower().Contains(lower))
						|| u.DiscordId == s);
				}
			}

			var users = await q.OrderByDescending(u => u.LastSeenAt).Take(Clamp(limit, 50, 200)).ToListAsync();
			await db.SaveChangesAsync(); // LastSeenAt of the admin
			var o = AdminAuth.Options(ctx);
			return Results.Ok(users.Select(u => AdminUserDto(u, o)));
		});

		g.MapPost("/users/{id:guid}/ban", async (Guid id, BanRequest req, HttpContext ctx, RankedDb db, MatchmakingLock gate) =>
		{
			var admin = AdminAuth.Admin(ctx);
			if (req.Minutes is { } m && (m < 1 || m > MaxBanMinutes))
				return Bad("invalid_minutes");
			if (InvalidReason(req.Reason))
				return Bad("invalid_reason");
			if (id == admin.Id)
				return Bad("cannot_ban_self");

			await gate.Gate.WaitAsync();
			try
			{
				await using var tx = await db.Database.BeginTransactionAsync();
				var user = await db.Users.Include(u => u.Rating).FirstOrDefaultAsync(u => u.Id == id);
				if (user == null)
					return NotFound();

				var until = req.Minutes is { } minutes ? DateTime.UtcNow.AddMinutes(minutes) : PermanentBan;
				user.BannedUntil = until;
				user.BanReason = Reason(req.Reason);
				var removedFromQueue = await db.Queue.Where(q => q.UserId == id).ExecuteDeleteAsync();

				Audit(db, admin, "ban", targetUser: id, details: new
				{
					minutes = req.Minutes,
					permanent = req.Minutes == null,
					bannedUntil = until,
					reason = user.BanReason,
					removedFromQueue = removedFromQueue > 0
				});
				await db.SaveChangesAsync();
				await tx.CommitAsync();
				return Results.Ok(AdminUserDto(user, AdminAuth.Options(ctx)));
			}
			finally
			{
				gate.Gate.Release();
			}
		});

		g.MapPost("/users/{id:guid}/unban", async (Guid id, HttpContext ctx, RankedDb db) =>
		{
			var admin = AdminAuth.Admin(ctx);
			var user = await db.Users.Include(u => u.Rating).FirstOrDefaultAsync(u => u.Id == id);
			if (user == null)
				return NotFound();

			Audit(db, admin, "unban", targetUser: id, details: new { previousBannedUntil = user.BannedUntil, previousReason = user.BanReason });
			user.BannedUntil = null;
			user.BanReason = null;
			await db.SaveChangesAsync();
			return Results.Ok(AdminUserDto(user, AdminAuth.Options(ctx)));
		});

		g.MapPost("/users/{id:guid}/rating", async (Guid id, RatingAdjustRequest req, HttpContext ctx, RankedDb db, MatchmakingLock gate) =>
		{
			var admin = AdminAuth.Admin(ctx);
			if (req.Mmr is not { } mmr || double.IsNaN(mmr) || mmr < MinMmr || mmr > MaxMmr)
				return Bad("invalid_mmr");
			if (req.Deviation is { } d && (double.IsNaN(d) || d < MinDeviation || d > MaxDeviation))
				return Bad("invalid_deviation");
			if (InvalidReason(req.Reason))
				return Bad("invalid_reason");

			// Ratings are also written by match results, which run under the matchmaking lock.
			await gate.Gate.WaitAsync();
			try
			{
				var user = await db.Users.Include(u => u.Rating).FirstOrDefaultAsync(u => u.Id == id);
				if (user == null)
					return NotFound();

				if (user.Rating == null)
				{
					user.Rating = new Rating { UserId = user.Id };
					db.Ratings.Add(user.Rating);
				}

				var r = user.Rating;
				var old = new { mmr = r.Mmr, deviation = r.Deviation };
				r.Mmr = Math.Round(mmr, 1);
				if (req.Deviation is { } dev)
					r.Deviation = Math.Round(dev, 1);
				r.UpdatedAt = DateTime.UtcNow;

				Audit(db, admin, "rating", targetUser: id, details: new
				{
					from = old,
					to = new { mmr = r.Mmr, deviation = r.Deviation },
					reason = Reason(req.Reason)
				});
				await db.SaveChangesAsync();
				return Results.Ok(AdminUserDto(user, AdminAuth.Options(ctx)));
			}
			finally
			{
				gate.Gate.Release();
			}
		});

		g.MapPost("/users/{id:guid}/admin", async (Guid id, SetAdminRequest req, HttpContext ctx, RankedDb db) =>
		{
			var admin = AdminAuth.Admin(ctx);
			if (req.IsAdmin is not { } makeAdmin)
				return Bad("invalid_is_admin");
			if (id == admin.Id && !makeAdmin)
				return Bad("cannot_demote_self");

			var user = await db.Users.Include(u => u.Rating).FirstOrDefaultAsync(u => u.Id == id);
			if (user == null)
				return NotFound();

			var o = AdminAuth.Options(ctx);
			Audit(db, admin, "admin", targetUser: id, details: new
			{
				from = user.IsAdmin,
				to = makeAdmin,
				configAdmin = AdminAuth.IsConfigAdmin(user, o)
			});
			user.IsAdmin = makeAdmin;
			await db.SaveChangesAsync();
			return Results.Ok(AdminUserDto(user, o));
		});

		// ---- Matches ----

		// status: any MatchStatus name (case-insensitive) or "active". userId: optional filter.
		g.MapGet("/matches", async (RankedDb db, string? status, Guid? userId, int? limit) =>
		{
			var q = db.Matches.Include(m => m.Players).ThenInclude(p => p.User).AsQueryable();
			if (!string.IsNullOrWhiteSpace(status))
			{
				if (status.Trim().Equals("active", StringComparison.OrdinalIgnoreCase))
					q = q.Where(m => MatchStates.Active.Contains(m.Status));
				else if (Enum.TryParse<MatchStatus>(status.Trim(), true, out var st) && Enum.IsDefined(st))
					q = q.Where(m => m.Status == st);
				else
					return Bad("invalid_status");
			}

			if (userId is { } uid)
				q = q.Where(m => m.Players.Any(p => p.UserId == uid));

			var matches = await q.OrderByDescending(m => m.CreatedAt).Take(Clamp(limit, 50, 200)).AsSplitQuery().ToListAsync();
			await db.SaveChangesAsync();
			return Results.Ok(matches.Select(AdminMatchDto));
		});

		// Active match: closed as Void (server stopped, nothing rated).
		// Completed match: its rating effects are reverted per player, then it is marked Void.
		g.MapPost("/matches/{id:guid}/void", async (Guid id, VoidRequest req, HttpContext ctx, RankedDb db, MatchmakingLock gate, Matchmaker mm) =>
		{
			var admin = AdminAuth.Admin(ctx);
			if (InvalidReason(req.Reason))
				return Bad("invalid_reason");

			var statusReason = "admin:" + Reason(req.Reason);
			await gate.Gate.WaitAsync();
			try
			{
				var m = await db.Matches.Include(x => x.Players).ThenInclude(p => p.User).FirstOrDefaultAsync(x => x.Id == id);
				if (m == null)
					return NotFound();

				var previousStatus = m.Status;
				var reverted = new List<object>();
				if (MatchStates.Active.Contains(m.Status))
					mm.Close(m, MatchStatus.Void, statusReason);
				else if (m.Status == MatchStatus.Completed)
				{
					var ids = m.Players.Select(p => p.UserId).ToList();
					var ratings = await db.Ratings.Where(r => ids.Contains(r.UserId)).ToDictionaryAsync(r => r.UserId);
					foreach (var p in m.Players)
					{
						if (!ratings.TryGetValue(p.UserId, out var r))
							continue;

						// A season reset happened since: the match belongs to an archived season, current rating stays.
						if (r.SeasonId != m.SeasonId)
						{
							reverted.Add(new { userId = p.UserId, skipped = "different_season" });
							continue;
						}

						var mmrDelta = (p.MmrAfter ?? p.MmrBefore) - p.MmrBefore;
						var before = new { mmr = r.Mmr, deviation = r.Deviation, games = r.Games, wins = r.Wins, losses = r.Losses };
						r.Mmr = Math.Round(r.Mmr - mmrDelta, 1);
						if (p.DeviationBefore is { } dev)
							r.Deviation = dev;
						if (p.Result != MatchResult.None)
							r.Games = Math.Max(0, r.Games - 1);
						if (p.Result == MatchResult.Win)
							r.Wins = Math.Max(0, r.Wins - 1);
						else if (p.Result == MatchResult.Loss)
							r.Losses = Math.Max(0, r.Losses - 1);
						r.UpdatedAt = DateTime.UtcNow;

						reverted.Add(new
						{
							userId = p.UserId,
							mmrDelta = -mmrDelta,
							before,
							after = new { mmr = r.Mmr, deviation = r.Deviation, games = r.Games, wins = r.Wins, losses = r.Losses }
						});
					}

					m.Status = MatchStatus.Void;
					m.StatusReason = statusReason;
				}
				else
					return Results.Json(new { error = "not_voidable", status = m.Status.ToString() }, statusCode: 409);

				Audit(db, admin, "void_match", targetMatch: m.Id, details: new
				{
					previousStatus = previousStatus.ToString(),
					reason = Reason(req.Reason),
					reverted
				});
				await db.SaveChangesAsync();
				return Results.Ok(AdminMatchDto(m));
			}
			finally
			{
				gate.Gate.Release();
			}
		});

		// ---- Seasons ----

		g.MapGet("/seasons", async (RankedDb db) =>
			Results.Ok(await db.Seasons.OrderByDescending(s => s.StartedAt).Select(s => SeasonDto(s)).ToListAsync()));

		g.MapPost("/seasons/start", async (SeasonStartRequest req, HttpContext ctx, RankedDb db, MatchmakingLock gate, ISeasonService seasons) =>
		{
			var admin = AdminAuth.Admin(ctx);
			var name = (req.Name ?? "").Trim();
			if (name.Length < 1 || name.Length > MaxSeasonNameLength)
				return Bad("invalid_name");

			return await SeasonChange(db, gate, async () =>
			{
				var previous = await seasons.GetActiveAsync(db);
				var s = await seasons.StartNewAsync(db, name);
				await db.SaveChangesAsync(); // assigns the season id
				Audit(db, admin, "season_start", details: new { seasonId = s.Id, name = s.Name, endedSeasonId = previous?.Id });
				await db.SaveChangesAsync();
				return Results.Ok(SeasonDto(s));
			});
		});

		g.MapPost("/seasons/end", async (HttpContext ctx, RankedDb db, MatchmakingLock gate, ISeasonService seasons) =>
		{
			var admin = AdminAuth.Admin(ctx);
			return await SeasonChange(db, gate, async () =>
			{
				var active = await seasons.GetActiveAsync(db);
				await seasons.EndActiveAsync(db);
				if (active == null)
					return Results.Json(new { error = "no_active_season" }, statusCode: 409);

				Audit(db, admin, "season_end", details: new { seasonId = active.Id, name = active.Name });
				await db.SaveChangesAsync();
				return Results.Ok(SeasonDto(active));
			});
		});

		// ---- Audit log ----

		g.MapGet("/actions", async (RankedDb db, int? limit) =>
		{
			var actions = await db.AdminActions.OrderByDescending(a => a.CreatedAt).Take(Clamp(limit, 100, 500)).ToListAsync();
			var userIds = actions.Select(a => a.AdminUserId)
				.Concat(actions.Where(a => a.TargetUserId != null).Select(a => a.TargetUserId!.Value))
				.Distinct().ToList();
			var names = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
			await db.SaveChangesAsync();
			return Results.Ok(actions.Select(a => new
			{
				id = a.Id,
				createdAt = a.CreatedAt,
				action = a.Action,
				adminUserId = a.AdminUserId,
				adminName = names.GetValueOrDefault(a.AdminUserId),
				targetUserId = a.TargetUserId,
				targetName = a.TargetUserId is { } t ? names.GetValueOrDefault(t) : null,
				targetMatchId = a.TargetMatchId,
				details = a.Details
			}));
		});
	}

	// Seasons touch every rating: run under the matchmaking lock in one transaction.
	// NotSupportedException (NoSeasonService placeholder) -> 501.
	static async Task<IResult> SeasonChange(RankedDb db, MatchmakingLock gate, Func<Task<IResult>> action)
	{
		await gate.Gate.WaitAsync();
		try
		{
			await using var tx = await db.Database.BeginTransactionAsync();
			var result = await action();
			await tx.CommitAsync();
			return result;
		}
		catch (NotSupportedException e)
		{
			return Results.Json(new { error = "not_implemented", message = e.Message }, statusCode: 501);
		}
		finally
		{
			gate.Gate.Release();
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	static void Audit(RankedDb db, User admin, string action, Guid? targetUser = null, Guid? targetMatch = null, object? details = null) =>
		db.AdminActions.Add(new AdminAction
		{
			AdminUserId = admin.Id,
			Action = action,
			TargetUserId = targetUser,
			TargetMatchId = targetMatch,
			Details = details == null ? null : JsonSerializer.Serialize(details, JsonOptions)
		});

	static object SeasonDto(Season s) =>
		new { id = s.Id, name = s.Name, startedAt = s.StartedAt, endedAt = s.EndedAt, isActive = s.IsActive };

	static object AdminUserDto(User u, AdminOptions o) => new
	{
		id = u.Id,
		name = u.DisplayName,
		email = u.Email,
		isDevUser = u.IsDevUser,
		isAdmin = AdminAuth.IsAdmin(u, o),
		mmr = u.Rating?.Mmr,
		deviation = u.Rating?.Deviation,
		games = u.Rating?.Games ?? 0,
		wins = u.Rating?.Wins ?? 0,
		losses = u.Rating?.Losses ?? 0,
		bannedUntil = u.BannedUntil,
		banReason = u.BanReason,
		createdAt = u.CreatedAt,
		lastSeenAt = u.LastSeenAt
	};

	static object AdminMatchDto(Match m) => new
	{
		id = m.Id,
		status = m.Status.ToString(),
		reason = m.StatusReason,
		rated = m.Rated,
		createdAt = m.CreatedAt,
		endedAt = m.EndedAt,
		mapUid = m.MapUid,
		players = m.Players.OrderBy(p => p.Team).Select(p => new
		{
			userId = p.UserId,
			name = p.User?.DisplayName,
			result = p.Result.ToString(),
			mmrBefore = p.MmrBefore,
			mmrAfter = p.MmrAfter
		})
	};

	static int Clamp(int? limit, int fallback, int max) => Math.Clamp(limit ?? fallback, 1, max);
	static bool InvalidReason(string? reason) => reason != null && reason.Trim().Length > MaxReasonLength;
	static string Reason(string? reason) => (reason ?? "").Trim();
	static IResult Bad(string error) => Results.Json(new { error }, statusCode: 400);
	static IResult NotFound() => Results.Json(new { error = "not_found" }, statusCode: 404);
}

public record BanRequest(int? Minutes, string? Reason);
public record RatingAdjustRequest(double? Mmr, double? Deviation, string? Reason);
public record SetAdminRequest(bool? IsAdmin);
public record VoidRequest(string? Reason);
public record SeasonStartRequest(string? Name);
