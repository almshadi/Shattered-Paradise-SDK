using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Data;

namespace Ranked.Backend.Replays;

public class ReplayOptions
{
	// Where captured replays are kept. Relative paths are resolved from the backend's content root.
	// Default: ranked/.replays next to the backend folder. Production: /app/replays (docker volume).
	public string Directory { get; set; } = "../.replays";

	// Replays older than this are deleted (and Match.ReplayFile cleared). 0 = keep forever.
	public int RetentionDays { get; set; } = 30;
}

// Stores the .orarep files the dedicated game servers record, one file per match ("<matchId:N>.orarep").
public class ReplayStore
{
	readonly ReplayOptions options;
	readonly ILogger<ReplayStore> log;

	public string RootDirectory { get; }

	public ReplayStore(IOptions<ReplayOptions> options, IHostEnvironment env, ILogger<ReplayStore> log)
	{
		this.options = options.Value;
		this.log = log;
		RootDirectory = Path.GetFullPath(Path.Combine(env.ContentRootPath, this.options.Directory));
	}

	// Newest .orarep anywhere below a game server's support dir (the engine writes to
	// <SupportDir>/Replays/<mod id>/<mod version>/<mod id>-Server-<timestamp>.orarep), or null.
	public static string? FindNewestReplay(string supportDir)
	{
		if (!System.IO.Directory.Exists(supportDir))
			return null;

		return new DirectoryInfo(supportDir).EnumerateFiles("*.orarep", SearchOption.AllDirectories)
			.OrderByDescending(f => f.LastWriteTimeUtc)
			.FirstOrDefault()?.FullName;
	}

	// Moves a replay into the store and returns its file name (relative to the store).
	public string Import(Guid matchId, string sourceFile)
	{
		System.IO.Directory.CreateDirectory(RootDirectory);
		var fileName = matchId.ToString("N") + ".orarep";
		var target = Path.Combine(RootDirectory, fileName);

		// The game server may only just have released the file; retry briefly.
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				// Copy + delete instead of File.Move: the store is usually on a different volume in production.
				File.Copy(sourceFile, target, overwrite: true);
				File.Delete(sourceFile);
				return fileName;
			}
			catch (IOException) when (attempt < 5)
			{
				Thread.Sleep(500);
			}
		}
	}

	// Absolute path of a stored replay, or null if the name is invalid or the file is gone.
	public string? PathFor(string? fileName)
	{
		if (string.IsNullOrEmpty(fileName) || fileName != Path.GetFileName(fileName))
			return null;

		var path = Path.Combine(RootDirectory, fileName);
		return File.Exists(path) ? path : null;
	}

	// Deletes replays older than RetentionDays and clears Match.ReplayFile for them. Returns the number deleted.
	public async Task<int> CleanupAsync(RankedDb db, CancellationToken ct = default)
	{
		if (options.RetentionDays <= 0 || !System.IO.Directory.Exists(RootDirectory))
			return 0;

		var cutoff = DateTime.UtcNow.AddDays(-options.RetentionDays);
		var deleted = new List<string>();
		foreach (var file in new DirectoryInfo(RootDirectory).EnumerateFiles("*.orarep"))
		{
			if (file.LastWriteTimeUtc >= cutoff)
				continue;

			try
			{
				file.Delete();
				deleted.Add(file.Name);
			}
			catch (Exception e)
			{
				log.LogWarning(e, "Could not delete old replay {File}", file.FullName);
			}
		}

		if (deleted.Count > 0)
		{
			var cleared = await db.Matches.Where(m => m.ReplayFile != null && deleted.Contains(m.ReplayFile))
				.ExecuteUpdateAsync(s => s.SetProperty(m => m.ReplayFile, (string?)null), ct);
			log.LogInformation("Replay retention: deleted {Count} replay(s) older than {Days} days, cleared {Cleared} match reference(s)",
				deleted.Count, options.RetentionDays, cleared);
		}

		return deleted.Count;
	}
}

// Runs the replay retention cleanup shortly after startup and then once a day.
public class ReplayCleanupService : BackgroundService
{
	readonly ReplayStore store;
	readonly IServiceScopeFactory scopes;
	readonly ILogger<ReplayCleanupService> log;

	public ReplayCleanupService(ReplayStore store, IServiceScopeFactory scopes, ILogger<ReplayCleanupService> log)
	{
		this.store = store;
		this.scopes = scopes;
		this.log = log;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					using var scope = scopes.CreateScope();
					await store.CleanupAsync(scope.ServiceProvider.GetRequiredService<RankedDb>(), stoppingToken);
				}
				catch (Exception e) when (e is not OperationCanceledException)
				{
					log.LogWarning(e, "Replay retention cleanup failed");
				}

				await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
			}
		}
		catch (OperationCanceledException) { }
	}
}

public static class ReplayServiceExtensions
{
	// Options (Ranked:Replays), the store and the daily retention cleanup.
	public static IServiceCollection AddReplays(this IServiceCollection services, IConfiguration config)
	{
		services.Configure<ReplayOptions>(config.GetSection("Ranked:Replays"));
		services.AddSingleton<ReplayStore>();
		services.AddHostedService<ReplayCleanupService>();
		return services;
	}
}
