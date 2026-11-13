namespace Ranked.Backend.Notifications;

// Bound to the "Ranked:Discord" config section (env Ranked__Discord__*).
public class DiscordOptions
{
	// Channel webhook (Server settings → Integrations → Webhooks). Empty = no Discord posts.
	public string WebhookUrl { get; set; } = "";

	// Post "X is looking for a ranked match" when someone joins the queue.
	public bool PostQueueJoins { get; set; } = true;

	// Post a result embed when a match completes.
	public bool PostMatchResults { get; set; } = true;

	// At most one queue-join post per player within this many minutes.
	public int QueueJoinThrottleMinutes { get; set; } = 10;

	// Optional overrides for the webhook's display name / avatar.
	public string Username { get; set; } = "Shattered Paradise Ranked";
	public string AvatarUrl { get; set; } = "";

	// Discord OAuth app (optional "Sign in with Discord"). Redirect URI: <PublicBaseUrl>/auth/discord/callback.
	public string ClientId { get; set; } = "";
	public string ClientSecret { get; set; } = "";

	public bool WebhookConfigured => !string.IsNullOrWhiteSpace(WebhookUrl);
	public bool LoginConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public static class DiscordSetup
{
	// Binds DiscordOptions and, when a webhook URL is configured, replaces the default (null) notifier.
	// Must run after the default IRankedNotifier registration (the last registration wins).
	public static void AddDiscord(this WebApplicationBuilder builder)
	{
		var section = builder.Configuration.GetSection("Ranked:Discord");
		builder.Services.Configure<DiscordOptions>(section);

		if (!string.IsNullOrWhiteSpace(section["WebhookUrl"]))
			builder.Services.AddSingleton<IRankedNotifier, DiscordNotifier>();
	}
}
