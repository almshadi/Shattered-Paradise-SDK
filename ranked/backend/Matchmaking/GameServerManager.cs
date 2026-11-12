using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Data;
using Ranked.Backend.Replays;

namespace Ranked.Backend.Matchmaking;

// Starts one dedicated OpenRA server process per match and keeps track of it.
public class GameServerManager : IDisposable
{
	readonly RankedOptions options;
	readonly ILogger<GameServerManager> log;
	readonly string sdkDir;
	readonly ReplayStore replays;
	readonly IServiceScopeFactory scopes;
	readonly ConcurrentDictionary<Guid, (Process Process, int Port)> running = new();

	public GameServerManager(IOptions<RankedOptions> options, IHostEnvironment env, ILogger<GameServerManager> log,
		ReplayStore replays, IServiceScopeFactory scopes)
	{
		this.options = options.Value;
		this.log = log;
		this.replays = replays;
		this.scopes = scopes;
		sdkDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, this.options.GameServers.SdkDir));
	}

	public bool IsRunning(Guid matchId) => running.TryGetValue(matchId, out var s) && !s.Process.HasExited;

	public int? AllocatePort()
	{
		var used = running.Values.Where(s => !s.Process.HasExited).Select(s => s.Port).ToHashSet();
		for (var p = options.GameServers.PortRangeStart; p <= options.GameServers.PortRangeEnd; p++)
			if (!used.Contains(p))
				return p;

		return null;
	}

	public void Start(Guid matchId, int port, string password, string mapUid)
	{
		var gs = options.GameServers;
		var engineDir = Path.Combine(sdkDir, "engine");
		var serverDll = Path.Combine(engineDir, "bin", "OpenRA.Server.dll");
		if (!File.Exists(serverDll))
			throw new FileNotFoundException("OpenRA server not built. Run `make` in the SDK directory.", serverDll);

		var supportDir = SupportDirFor(matchId);
		Directory.CreateDirectory(supportDir);

		var psi = new ProcessStartInfo(gs.DotnetPath)
		{
			WorkingDirectory = engineDir,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		psi.ArgumentList.Add(serverDll);
		foreach (var arg in new[]
		{
			"Game.Mod=sp",
			$"Engine.EngineDir={engineDir}", // relative ".." in launch-dedicated.sh is resolved from engine/bin, i.e. engine/
			$"Engine.SupportDir={supportDir}",
			$"Server.Name=SP Ranked {matchId.ToString("N")[..8]}",
			$"Server.ListenPort={port}",
			$"Server.Password={password}",
			$"Server.Map={mapUid}",
			"Server.AdvertiseOnline=False",
			"Server.EnableSingleplayer=False",
			"Server.QueryMapRepository=False",
			"Server.EnableVoteKick=False",
			"Server.ShareAnonymizedIPs=False",
			"Server.EnableLintChecks=False",
			"Server.EnableSyncReports=True",
			"Server.RecordReplays=True",
			"Server.EnableGeoIP=False",
		})
			psi.ArgumentList.Add(arg);

		psi.Environment["MOD_SEARCH_PATHS"] = $"{Path.Combine(sdkDir, "mods")},./mods";
		psi.Environment["SP_RANKED_MATCH_ID"] = matchId.ToString();
		psi.Environment["SP_RANKED_BACKEND_URL"] = gs.BackendUrlForServers;
		psi.Environment["SP_RANKED_SERVER_SECRET"] = options.ServerSecret;

		var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
		var logFile = new StreamWriter(Path.Combine(supportDir, "server-stdout.log")) { AutoFlush = true };
		var logClosed = false;
		void WriteLog(string line)
		{
			lock (logFile)
				if (!logClosed)
					logFile.WriteLine(line);
		}

		process.OutputDataReceived += (_, e) => { if (e.Data != null) WriteLog(e.Data); };
		process.ErrorDataReceived += (_, e) => { if (e.Data != null) WriteLog("ERR " + e.Data); };
		process.Exited += (_, _) =>
		{
			try
			{
				log.LogInformation("Game server for match {Match} exited with code {Code}", matchId, process.ExitCode);

				// Let the async stdout/stderr readers drain before closing the log (and deleting its folder).
				process.WaitForExit(5000);
				lock (logFile)
				{
					logClosed = true;
					logFile.Dispose();
				}
			}
			catch (Exception e)
			{
				log.LogWarning(e, "Error while handling exit of game server for match {Match}", matchId);
			}

			_ = Task.Run(() => CaptureReplayAndCleanUp(matchId, supportDir));
		};

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		running[matchId] = (process, port);
		log.LogInformation("Started game server for match {Match} on port {Port} (pid {Pid})", matchId, port, process.Id);
	}

	string SupportDirFor(Guid matchId) => Path.Combine(sdkDir, "ranked", ".server-support", matchId.ToString("N"));

	// After the server process is gone: move its replay (if a game was played) into the replay store, link it to the
	// match and delete the per-match support dir. Never throws.
	async Task CaptureReplayAndCleanUp(Guid matchId, string supportDir)
	{
		try
		{
			var replay = ReplayStore.FindNewestReplay(supportDir);
			if (replay != null)
			{
				var fileName = replays.Import(matchId, replay);
				log.LogInformation("Stored replay of match {Match} as {File}", matchId, fileName);

				using var scope = scopes.CreateScope();
				var db = scope.ServiceProvider.GetRequiredService<RankedDb>();
				var updated = await db.Matches.Where(m => m.Id == matchId)
					.ExecuteUpdateAsync(s => s.SetProperty(m => m.ReplayFile, fileName));
				if (updated == 0)
					log.LogWarning("Replay of match {Match} stored, but the match no longer exists in the database", matchId);
			}
		}
		catch (Exception e)
		{
			log.LogWarning(e, "Could not capture the replay of match {Match}", matchId);
		}

		try
		{
			if (Directory.Exists(supportDir))
				Directory.Delete(supportDir, recursive: true);
		}
		catch (Exception e)
		{
			log.LogWarning(e, "Could not delete the support dir {Dir} of match {Match}", supportDir, matchId);
		}
	}

	public void Stop(Guid matchId)
	{
		if (!running.TryRemove(matchId, out var s))
			return;

		try
		{
			if (!s.Process.HasExited)
				s.Process.Kill(entireProcessTree: true);
		}
		catch (Exception e)
		{
			log.LogWarning(e, "Failed to stop game server for match {Match}", matchId);
		}
	}

	// Safety net: the server normally exits by itself once players leave after the result.
	public void StopLater(Guid matchId, TimeSpan delay) =>
		_ = Task.Delay(delay).ContinueWith(_ => Stop(matchId));

	public void Dispose()
	{
		foreach (var id in running.Keys.ToList())
			Stop(id);
	}
}
