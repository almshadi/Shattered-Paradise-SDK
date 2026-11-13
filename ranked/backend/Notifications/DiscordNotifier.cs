using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ranked.Backend.Data;

namespace Ranked.Backend.Notifications;

// Posts queue joins and match results to a Discord channel webhook.
// Every call returns immediately; the work runs in the background with its own DI scope and never throws.
public sealed class DiscordNotifier : IRankedNotifier
{
	const int ColorQueue = 0x3B82F6;
	const int ColorWin = 0x22C55E;
	const int ColorUnrated = 0x9CA3AF;

	static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

	readonly IServiceScopeFactory scopes;
	readonly IHttpClientFactory http;
	readonly IOptionsMonitor<DiscordOptions> discord;
	readonly IOptionsMonitor<RankedOptions> ranked;
	readonly ILogger<DiscordNotifier> log;
	readonly ConcurrentDictionary<Guid, DateTime> lastQueuePost = new();

	public DiscordNotifier(IServiceScopeFactory scopes, IHttpClientFactory http, IOptionsMonitor<DiscordOptions> discord,
		IOptionsMonitor<RankedOptions> ranked, ILogger<DiscordNotifier> log)
	{
		this.scopes = scopes;
		this.http = http;
		this.discord = discord;
		this.ranked = ranked;
		this.log = log;
	}

	public void PlayerQueued(Guid userId, int queueSize)
	{
		try
		{
			var o = discord.CurrentValue;
			if (!o.WebhookConfigured || !o.PostQueueJoins || !TryClaimQueueSlot(userId, o))
				return;

			Run("queue join", async () =>
			{
				using var scope = scopes.CreateScope();
				var db = scope.ServiceProvider.GetRequiredService<RankedDb>();
				var user = await db.Users.AsNoTracking().Include(u => u.Rating).FirstOrDefaultAsync(u => u.Id == userId);
				if (user == null)
					return;

				var searching = Math.Max(queueSize, 1);
				var embed = new Dictionary<string, object?>
				{
					["title"] = "Looking for a ranked match",
					["description"] = $"**{Md(user.DisplayName)}** is looking for a ranked match ({searching} searching)",
					["color"] = ColorQueue,
					["timestamp"] = DateTime.UtcNow.ToString("o"),
					["footer"] = new { text = "Open Shattered Paradise → Ranked → Find match to play them" }
				};

				if (user.Rating is { } r && r.Games > 0)
					embed["fields"] = new[] { new { name = "Rating", value = $"{Math.Round(r.Mmr):0} ({r.Wins}W / {r.Losses}L)", inline = true } };

				AddSiteLink(embed);
				await Post(o, embed);
			});
		}
		catch (Exception e)
		{
			log.LogWarning(e, "Discord queue notification failed");
		}
	}

	public void MatchCompleted(Guid matchId)
	{
		try
		{
			var o = discord.CurrentValue;
			if (!o.WebhookConfigured || !o.PostMatchResults)
				return;

			Run("match result", async () =>
			{
				using var scope = scopes.CreateScope();
				var db = scope.ServiceProvider.GetRequiredService<RankedDb>();
				var m = await db.Matches.AsNoTracking().Include(x => x.Players).ThenInclude(p => p.User)
					.FirstOrDefaultAsync(x => x.Id == matchId);

				if (m == null || m.Status != MatchStatus.Completed)
					return;

				var embed = BuildResultEmbed(m);
				if (embed == null)
					return;

				AddSiteLink(embed);
				await Post(o, embed);
			});
		}
		catch (Exception e)
		{
			log.LogWarning(e, "Discord match notification failed");
		}
	}

	// "Alice defeated Bob on <map> · +16 / −16 · GDI vs Nod"
	internal static Dictionary<string, object?>? BuildResultEmbed(Match m)
	{
		var winners = m.Players.Where(p => p.Result == MatchResult.Win).OrderBy(p => p.Team).ToList();
		var losers = m.Players.Where(p => p.Result == MatchResult.Loss).OrderBy(p => p.Team).ToList();
		if (winners.Count == 0 || losers.Count == 0)
			return null;

		static string Names(IEnumerable<MatchPlayer> ps) => string.Join(" & ", ps.Select(p => $"**{Md(p.User?.DisplayName ?? "?")}**"));
		static string Factions(IEnumerable<MatchPlayer> ps) => string.Join(" & ", ps.Select(p => FactionName(p.Faction)));

		var map = string.IsNullOrEmpty(m.MapUid) ? "an unknown map" : $"`{m.MapUid}`";
		var summary = new List<string>();

		var ratingChanged = m.Rated && m.Players.All(p => p.MmrAfter != null);
		if (ratingChanged)
		{
			var w = winners.Average(p => p.MmrAfter!.Value - p.MmrBefore);
			var l = losers.Average(p => p.MmrAfter!.Value - p.MmrBefore);
			summary.Add($"{Delta(w)} / {Delta(l)}");
		}
		else
			summary.Add("unrated");

		summary.Add($"{Factions(winners)} vs {Factions(losers)}");

		var fields = new List<object>();
		if (ratingChanged)
		{
			foreach (var p in winners.Concat(losers))
			{
				fields.Add(new
				{
					name = p.User?.DisplayName ?? "?",
					value = $"{Math.Round(p.MmrAfter!.Value):0} ({Delta(p.MmrAfter!.Value - p.MmrBefore)})",
					inline = true
				});
			}
		}

		if (m.StartedAt != null && m.EndedAt != null)
		{
			var d = m.EndedAt.Value - m.StartedAt.Value;
			fields.Add(new { name = "Duration", value = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"), inline = true });
		}

		var embed = new Dictionary<string, object?>
		{
			["title"] = m.TeamSize > 1 ? $"Ranked {m.QueueType} result" : "Ranked match result",
			["description"] = $"{Names(winners)} defeated {Names(losers)} on {map}\n{string.Join(" · ", summary)}",
			["color"] = ratingChanged ? ColorWin : ColorUnrated,
			["timestamp"] = (m.EndedAt ?? DateTime.UtcNow).ToString("o"),
			["footer"] = new { text = string.IsNullOrEmpty(m.StatusReason) ? "Shattered Paradise Ranked" : $"Ended by {m.StatusReason}" }
		};

		if (fields.Count > 0)
			embed["fields"] = fields;

		return embed;
	}

	internal static string FactionName(string? internalName) => (internalName ?? "").ToLowerInvariant() switch
	{
		"gdi" => "GDI",
		"nod" => "Nod",
		"mut" => "Mutants",
		"cab" => "CABAL",
		"scr" => "Scrin",
		"" or "random" => "Random",
		var other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other)
	};

	static string Delta(double d)
	{
		var r = (int)Math.Round(d);
		return r >= 0 ? $"+{r}" : $"−{-r}";
	}

	// Escape Discord markdown in player names (nicknames allow '_' and '.').
	static string Md(string s) => s.Replace("\\", "\\\\").Replace("_", "\\_").Replace("*", "\\*").Replace("~", "\\~").Replace("`", "\\`");

	bool TryClaimQueueSlot(Guid userId, DiscordOptions o)
	{
		var now = DateTime.UtcNow;
		var window = TimeSpan.FromMinutes(Math.Max(0, o.QueueJoinThrottleMinutes));

		// Keep the map small: forget entries older than the window.
		if (lastQueuePost.Count > 1000)
			foreach (var kv in lastQueuePost)
				if (now - kv.Value > window)
					lastQueuePost.TryRemove(kv.Key, out _);

		while (true)
		{
			if (lastQueuePost.TryGetValue(userId, out var last))
			{
				if (now - last < window)
					return false;

				if (lastQueuePost.TryUpdate(userId, now, last))
					return true;
			}
			else if (lastQueuePost.TryAdd(userId, now))
				return true;
		}
	}

	void AddSiteLink(Dictionary<string, object?> embed)
	{
		var baseUrl = ranked.CurrentValue.PublicBaseUrl;
		if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
			embed["url"] = baseUrl.TrimEnd('/') + "/";
	}

	async Task Post(DiscordOptions o, Dictionary<string, object?> embed)
	{
		var payload = new Dictionary<string, object?>
		{
			["embeds"] = new[] { embed },
			["allowed_mentions"] = new { parse = Array.Empty<string>() }
		};

		if (!string.IsNullOrWhiteSpace(o.Username))
			payload["username"] = o.Username;

		if (!string.IsNullOrWhiteSpace(o.AvatarUrl))
			payload["avatar_url"] = o.AvatarUrl;

		var json = JsonSerializer.Serialize(payload, JsonOptions);
		var client = http.CreateClient();
		client.Timeout = TimeSpan.FromSeconds(10);

		// One retry: covers a stale pooled connection and Discord's short 429 rate-limit waits.
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				// Buffered body with Content-Length (no chunked transfer encoding).
				using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
				using var res = await client.PostAsync(o.WebhookUrl, content);
				if (res.IsSuccessStatusCode)
					return;

				var body = await res.Content.ReadAsStringAsync();
				if ((int)res.StatusCode == 429 && attempt == 1)
				{
					var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
					if (wait <= TimeSpan.FromSeconds(10))
					{
						await Task.Delay(wait);
						continue;
					}
				}

				log.LogWarning("Discord webhook returned {Status}: {Body}", (int)res.StatusCode, body.Length > 300 ? body[..300] : body);
				return;
			}
			catch (HttpRequestException) when (attempt == 1)
			{
				await Task.Delay(TimeSpan.FromSeconds(1));
			}
		}
	}

	void Run(string what, Func<Task> work)
	{
		_ = Task.Run(async () =>
		{
			try
			{
				await work();
			}
			catch (Exception e)
			{
				log.LogWarning(e, "Discord {What} notification failed", what);
			}
		});
	}
}
