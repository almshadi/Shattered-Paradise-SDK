using Microsoft.EntityFrameworkCore;
using Ranked.Backend;
using Ranked.Backend.Api;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;
using Ranked.Backend.Matchmaking;
using Ranked.Backend.Ratings;

// Load secrets from ranked/.env (KEY=VALUE lines) for local development. Real env vars win.
foreach (var envFile in new[] { ".env", "../.env" })
{
	if (!File.Exists(envFile))
		continue;

	foreach (var line in File.ReadAllLines(envFile))
	{
		var trimmed = line.Trim();
		if (trimmed.Length == 0 || trimmed.StartsWith('#') || !trimmed.Contains('='))
			continue;

		var key = trimmed[..trimmed.IndexOf('=')].Trim();
		var value = trimmed[(trimmed.IndexOf('=') + 1)..].Trim().Trim('"');
		if (Environment.GetEnvironmentVariable(key) == null)
			Environment.SetEnvironmentVariable(key, value);
	}
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RankedOptions>(builder.Configuration.GetSection("Ranked"));
builder.Services.Configure<Ranked.Backend.Admin.AdminOptions>(builder.Configuration.GetSection("Ranked:Admin"));
builder.Services.AddDbContext<RankedDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<MatchmakingLock>();
builder.Services.AddSingleton<ModVersionReader>();
builder.Services.AddSingleton<GameServerManager>();
builder.Services.AddSingleton<MapPicker>();
builder.Services.AddSingleton<RatingService>();
builder.Services.AddSingleton<Ranked.Backend.Ratings.ISeasonService, Ranked.Backend.Ratings.NoSeasonService>();
builder.Services.AddSingleton<Ranked.Backend.Notifications.IRankedNotifier, Ranked.Backend.Notifications.NullRankedNotifier>();
builder.Services.AddSingleton<Matchmaker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Matchmaker>());

var app = builder.Build();

var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RankedOptions>>().Value;
if (string.IsNullOrEmpty(options.ServerSecret))
	throw new InvalidOperationException("Ranked:ServerSecret must be set (env Ranked__ServerSecret).");

if (options.Auth.DevLogin)
	app.Logger.LogWarning("DEV LOGIN IS ENABLED - anyone can log in as any test user. Do not use on a public server.");

var versions = app.Services.GetRequiredService<ModVersionReader>();
var requiredVersion = versions.Required;
if (string.IsNullOrEmpty(requiredVersion))
	app.Logger.LogWarning("No required client version could be determined - clients of any version will be accepted.");
else
	app.Logger.LogInformation("Required client version: {Version} (from {Source})", requiredVersion, versions.Source);

// Apply EF migrations on startup (baselines databases that were created by the old EnsureCreated prototype).
using (var scope = app.Services.CreateScope())
{
	var db = scope.ServiceProvider.GetRequiredService<RankedDb>();
	for (var attempt = 1; ; attempt++)
	{
		try
		{
			DatabaseMigrator.Migrate(db, app.Logger);
			break;
		}
		catch (Exception) when (attempt < 10)
		{
			app.Logger.LogInformation("Waiting for database...");
			Thread.Sleep(2000);
		}
	}
}

app.MapGet("/", () => Results.Text("Shattered Paradise ranked backend"));
AuthEndpoints.Map(app);
AccountEndpoints.Map(app);
QueueEndpoints.Map(app);
MatchEndpoints.Map(app);
LeaderboardEndpoints.Map(app);
ConfigEndpoints.Map(app);
ServerEndpoints.Map(app);
AdminEndpoints.Map(app);

app.Run();
