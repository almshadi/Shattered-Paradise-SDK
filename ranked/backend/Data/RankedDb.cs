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

	public Rating? Rating { get; set; }
}

// Kept separate from User so seasons / per-queue ratings can be added later.
public class Rating
{
	public Guid UserId { get; set; }
	public double Mmr { get; set; }
	public int Games { get; set; }
	public int Wins { get; set; }
	public int Losses { get; set; }
	public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
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

	protected override void OnModelCreating(ModelBuilder b)
	{
		b.Entity<User>(e =>
		{
			e.HasIndex(u => u.GoogleSub).IsUnique();
			e.HasIndex(u => u.NicknameKey).IsUnique();
			e.Ignore(u => u.NeedsNickname);
			e.HasOne(u => u.Rating).WithOne().HasForeignKey<Rating>(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
		});

		b.Entity<Rating>().HasKey(r => r.UserId);

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
