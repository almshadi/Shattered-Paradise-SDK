using System.Text.Json;
using Xunit;

namespace Ranked.Backend.Tests;

public class MapTitleTests
{
	[Fact]
	public void MapTitleFallsBackToNull()
	{
		var o = new GameServerOptions { MapTitles = new() { ["abc"] = " Alpine Assault ", ["blank"] = " " } };
		Assert.Equal("Alpine Assault", o.MapTitle("abc"));
		Assert.Null(o.MapTitle("blank"));
		Assert.Null(o.MapTitle("unknown"));
		Assert.Null(o.MapTitle(null));
		Assert.Null(o.MapTitle(""));
	}

	// Every map of the shipped pool has a title (the web pages and Discord posts show it).
	[Fact]
	public void AppSettingsTitleEveryPoolMap()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir != null && !File.Exists(Path.Combine(dir.FullName, "backend", "appsettings.json")))
			dir = dir.Parent;

		Assert.NotNull(dir);
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "backend", "appsettings.json")));
		var gs = doc.RootElement.GetProperty("Ranked").GetProperty("GameServers");
		var pool = gs.GetProperty("MapPool").EnumerateArray().Select(e => e.GetString()!).ToList();
		var titles = gs.GetProperty("MapTitles").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());

		Assert.NotEmpty(pool);
		foreach (var uid in pool)
			Assert.False(string.IsNullOrWhiteSpace(titles.GetValueOrDefault(uid)), $"No title for map {uid}");
	}
}
