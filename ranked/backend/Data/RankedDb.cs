using Microsoft.EntityFrameworkCore;

namespace Ranked.Backend.Data;

public class User
{
	public Guid Id { get; set; } = Guid.NewGuid();

	// Google's stable account id ("sub" claim). Null for dev-login users.
	public string? GoogleSub { get; set; }
	public string? Email { get; set; }

	// Public nickname chosen by the player. The real name from Google is never stored.
	public string DisplayName { get; set; } = "";

	// Lower-cased nickname for case-insensitive uniqueness. Null until a nickname is chosen.
	public string? NicknameKey { get; set; }
	public bool NeedsNickname => NicknameKey == null;
	public bool IsDevUser { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

	// Moderation (admin feature).
	public bool IsAdmin { get; set; }
	public DateTime? BannedUntil { get; set; }
	public string? BanReason { get; set; }

	// Queue dodging penalties.
	public int DodgeCount { get; set; }
	public DateTime? LastDodgeAt { get; set; }
	public DateTime? QueueBlockedUntil { get; set; }

	// Linked Discord account id (unique when set).
	public string? DiscordId { get; set; }

	public Rating? Rating { get; set; }
}

// Current-season rating of a user. Kept separate from User so seasons / per-queue ratings can be added later.
public class Rating
{
	public Guid UserId { get; set; }

	// Displayed rating (Glicko rating once Glicko-2 replaces Elo).
	public double Mmr { get; set; }

	// Glicko-2 rating deviation and volatility (in Glicko-1 scale for the deviation).
	public double Deviation { get; set; } = 350;
	public double Volatility { get; set; } = 0.06;

	// Season this rating belongs to. Null = no season system active yet.
	public int? SeasonId { get; set; }
	public int Games { get; set; }
	public int Wins { get; set; }
	public int Losses { get; set; }
	public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Season
{
	public int Id { get; set; }
	public string Name { get; set; } = "";
	public DateTime StartedAt { get; set; } = DateTime.UtcNow;
	public DateTime? EndedAt { get; set; }
	public bool IsActive { get; set; }
}

// Frozen final standing of a user at the end of a season.
public class SeasonStanding
{
	public int SeasonId { get; set; }
	public Guid UserId { get; set; }
	public int Rank { get; set; }
	public double Mmr { get; set; }
	public int Games { get; set; }
	public int Wins { get; set; }
	public int Losses { get; set; }
}

// Audit log of admin actions. Deliberately no foreign keys, so entries survive user / match deletion.
public class AdminAction
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid AdminUserId { get; set; }
	public string Action { get; set; } = "";
	public Guid? TargetUserId { get; set; }
	public Guid? TargetMatchId { get; set; }
	public string? Details { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Opaque bearer token issued by us after login. Only the SHA-256 hash is stored.
public class AuthSession
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public Guid UserId { get; set; }
	public string TokenHash { get; set; } = "";
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime ExpiresAt { get; set; }
	public User? User { get; set; }
}

public enum LoginStatus { Pending, Complete, Failed }

// A browser login started by the game client. The client polls it until Google redirects back.
public class LoginAttempt
{
	public Guid Id { get; set; } = Guid.NewGuid();

	// Random secret only the polling client knows, so nobody else can collect the token.
	public string PollSecretHash { get; set; } = "";
	public LoginStatus Status { get; set; }
	public string? Error { get; set; }
	public Guid? UserId { get; set; }

	// The issued session token, handed out exactly once to the poller, then cleared.
	public string? IssuedToken { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime ExpiresAt { get; set; }
}

public class QueueEntry
{
	public Guid UserId { get; set; }
	public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
	public double MmrSnapshot { get; set; }

	// Requested faction internal name ("Random", "gdi", ...) and color as RRGGBB.
	public string Faction { get; set; } = "Random";
	public string? Color { get; set; }
	public DateTime LastPollAt { get; set; } = DateTime.UtcNow;

	// Queue the player is searching in ("1v1", "2v2", ...).
	public string QueueType { get; set; } = "1v1";

	// Comma-separated map UIDs the player vetoed. Null = no vetoes.
	public string? MapVetoes { get; set; }
	public User? User { get; set; }
}

public enum MatchStatus
{
	Starting,          // game server process being launched
	WaitingForPlayers, // server up, players joining / readying
	InProgress,
	Completed,
	Cancelled,         // someone never showed up / server failed before start
	Void               // desync, crash or no clear result after start
}

public class Match
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public MatchStatus Status { get; set; }
	public string MatchmakingMode { get; set; } = "";
	public bool Rated { get; set; }
	public string MapUid { get; set; } = "";
	public string Host { get; set; } = "";
	public int Port { get; set; }
	public string Password { get; set; } = "";
	public string? StatusReason { get; set; }
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime? StartedAt { get; set; }
	public DateTime? EndedAt { get; set; }
	public string QueueType { get; set; } = "1v1";
	public int TeamSize { get; set; } = 1;

	// File name of the stored replay, if one was uploaded.
	public string? ReplayFile { get; set; }
	public int? SeasonId { get; set; }
	public List<MatchPlayer> Players { get; set; } = new();
}

public enum MatchResult { None, Win, Loss }

public class MatchPlayer
{
	public Guid MatchId { get; set; }
	public Guid UserId { get; set; }

	// Per-player secret the game client sends to the game server to prove who it is.
	public string Ticket { get; set; } = "";
	public bool Joined { get; set; }

	// Faction/color chosen in the ranked panel before queueing. Hidden from the opponent until the game starts.
	public string? Faction { get; set; }
	public string? Color { get; set; }
	public MatchResult Result { get; set; }
	public double MmrBefore { get; set; }
	public double? MmrAfter { get; set; }

	// Team number within the match (0-based). Always 0/1 in 1v1.
	public int Team { get; set; }
	public double? DeviationBefore { get; set; }
	public double? DeviationAfter { get; set; }

	// Whether this game was one of the player's placement games.
	public bool Placement { get; set; }
	public User? User { get; set; }
	public Match? Match { get; set; }
}

public class RankedDb : DbContext
{
	public RankedDb(DbContextOptions<RankedDb> options)
		: base(options) { }

	public DbSet<User> Users => Set<User>();
	public DbSet<Rating> Ratings => Set<Rating>();
	public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
	public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();
	public DbSet<QueueEntry> Queue => Set<QueueEntry>();
	public DbSet<Match> Matches => Set<Match>();
	public DbSet<MatchPlayer> MatchPlayers => Set<MatchPlayer>();
	public DbSet<Season> Seasons => Set<Season>();
	public DbSet<SeasonStanding> SeasonStandings => Set<SeasonStanding>();
	public DbSet<AdminAction> AdminActions => Set<AdminAction>();

	protected override void OnModelCreating(ModelBuilder b)
	{
		b.Entity<User>(e =>
		{
			e.HasIndex(u => u.GoogleSub).IsUnique();
			e.HasIndex(u => u.NicknameKey).IsUnique();
			e.HasIndex(u => u.DiscordId).IsUnique();
			e.Ignore(u => u.NeedsNickname);
			e.HasOne(u => u.Rating).WithOne().HasForeignKey<Rating>(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
		});

		b.Entity<Rating>(e =>
		{
			e.HasKey(r => r.UserId);
			e.HasOne<Season>().WithMany().HasForeignKey(r => r.SeasonId).OnDelete(DeleteBehavior.SetNull);
		});

		b.Entity<SeasonStanding>(e =>
		{
			e.HasKey(s => new { s.SeasonId, s.UserId });
			e.HasOne<Season>().WithMany().HasForeignKey(s => s.SeasonId).OnDelete(DeleteBehavior.Cascade);
			e.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
		});

		b.Entity<AdminAction>(e =>
		{
			e.HasIndex(a => a.CreatedAt);
			e.HasIndex(a => a.TargetUserId);
		});

		b.Entity<AuthSession>(e =>
		{
			e.HasIndex(s => s.TokenHash).IsUnique();
			e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
		});

		b.Entity<QueueEntry>(e =>
		{
			e.HasKey(q => q.UserId);
			e.HasOne(q => q.User).WithMany().HasForeignKey(q => q.UserId).OnDelete(DeleteBehavior.Cascade);
		});

		b.Entity<Match>(e =>
		{
			e.Property(m => m.Status).HasConversion<string>();
			e.HasIndex(m => m.Status);
			e.HasOne<Season>().WithMany().HasForeignKey(m => m.SeasonId).OnDelete(DeleteBehavior.SetNull);
		});

		b.Entity<MatchPlayer>(e =>
		{
			e.HasKey(p => new { p.MatchId, p.UserId });
			e.Property(p => p.Result).HasConversion<string>();
			e.HasOne(p => p.Match).WithMany(m => m.Players).HasForeignKey(p => p.MatchId).OnDelete(DeleteBehavior.Cascade);
			e.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
		});

		b.Entity<LoginAttempt>().Property(l => l.Status).HasConversion<string>();
	}
}
