using Microsoft.Extensions.Options;

namespace Ranked.Backend;

// The client version players must run: the mod version of the SDK checkout the game servers are launched from
// (OpenRA rejects clients whose mod version differs from the server anyway). Ranked:RequiredClientVersion overrides it.
public class ModVersionReader
{
	readonly RankedOptions options;
	readonly ILogger<ModVersionReader> log;
	readonly string modYaml;
	readonly object sync = new();
	DateTime cachedWriteTime;
	string cached = "";
	bool warned;

	public ModVersionReader(IOptions<RankedOptions> options, IHostEnvironment env, ILogger<ModVersionReader> log)
	{
		this.options = options.Value;
		this.log = log;
		var sdkDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, this.options.GameServers.SdkDir));
		modYaml = Path.Combine(sdkDir, "mods", "sp", "mod.yaml");
	}

	public string Required
	{
		get
		{
			if (!string.IsNullOrEmpty(options.RequiredClientVersion))
				return options.RequiredClientVersion;

			lock (sync)
			{
				try
				{
					var writeTime = File.GetLastWriteTimeUtc(modYaml);
					if (writeTime != cachedWriteTime)
					{
						cached = ReadVersion(modYaml);
						cachedWriteTime = writeTime;
						if (cached.Length == 0)
							WarnOnce($"No Metadata.Version found in {modYaml}; accepting any client version.");
						else
							warned = false;
					}
				}
				catch (Exception e)
				{
					cached = "";
					cachedWriteTime = default;
					WarnOnce($"Could not read {modYaml} ({e.Message}); accepting any client version.");
				}

				return cached;
			}
		}
	}

	public string Source => !string.IsNullOrEmpty(options.RequiredClientVersion) ? "Ranked:RequiredClientVersion" : modYaml;

	void WarnOnce(string message)
	{
		if (warned)
			return;

		warned = true;
		log.LogWarning("{Message}", message);
	}

	static string ReadVersion(string path)
	{
		var inMetadata = false;
		foreach (var raw in File.ReadLines(path))
		{
			var line = raw.TrimEnd('\r');
			if (line.Length == 0 || line.TrimStart().StartsWith('#'))
				continue;

			if (!char.IsWhiteSpace(line[0]))
			{
				inMetadata = line.TrimEnd() == "Metadata:";
				continue;
			}

			if (!inMetadata)
				continue;

			var trimmed = line.Trim();
			if (trimmed.StartsWith("Version:"))
				return trimmed["Version:".Length..].Trim();
		}

		return "";
	}
}

public static class ClientVersion
{
	public const string Header = "X-Client-Version";

	public static string Of(HttpContext ctx) => ctx.Request.Headers[Header].ToString();

	public static bool IsOk(string required, string yours) => string.IsNullOrEmpty(required) || yours == required;

	// Returns the 409 version_mismatch result, or null when the caller's build is acceptable.
	public static IResult? Check(HttpContext ctx, ModVersionReader versions, RankedOptions options)
	{
		var required = versions.Required;
		var yours = Of(ctx);
		if (IsOk(required, yours))
			return null;

		return Results.Json(new { error = "version_mismatch", required, yours, downloadUrl = options.ClientDownloadUrl }, statusCode: 409);
	}
}
