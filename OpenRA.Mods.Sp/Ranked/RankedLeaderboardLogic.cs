#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Widgets;

namespace OpenRA.Mods.Sp.Ranked
{
	public class RankedLeaderboardLogic : ChromeLogic
	{
		[FluentReference]
		const string Title = "label-ranked-leaderboard-title";

		[FluentReference("season")]
		const string TitleSeason = "label-ranked-leaderboard-title-season";

		[FluentReference("name")]
		const string SeasonCurrent = "label-ranked-season-current";

		[FluentReference("name", "start", "end")]
		const string SeasonArchived = "label-ranked-season-archived";

		[FluentReference]
		const string SeasonDefault = "label-ranked-season-default";

		[FluentReference]
		const string Loading = "label-ranked-leaderboard-loading";

		[FluentReference("error")]
		const string LoadFailed = "label-ranked-leaderboard-load-failed";

		[FluentReference]
		const string SeasonNotFound = "label-ranked-leaderboard-season-not-found";

		[FluentReference]
		const string EmptyCurrent = "label-ranked-leaderboard-empty-current";

		[FluentReference]
		const string EmptyArchived = "label-ranked-leaderboard-empty-archived";

		[FluentReference("rank", "total", "rating", "wins", "losses")]
		const string MePlaced = "label-ranked-me-placed";

		[FluentReference("rating", "wins", "losses")]
		const string MeUnranked = "label-ranked-me-unranked";

		[FluentReference("left", "total")]
		const string MePlacement = "label-ranked-me-placement";

		[FluentReference]
		const string MeNotPlayed = "label-ranked-me-not-played";

		[FluentReference]
		const string MeNotPlaced = "label-ranked-me-not-placed";

		sealed class Season
		{
			public string Id;
			public string Name;
			public string StartedAt;
			public string EndedAt;
			public bool IsActive;
		}

		readonly ScrollPanelWidget list;
		readonly ScrollItemWidget template;
		readonly List<Season> seasons = new();
		Season selectedSeason;
		string title;
		string status = "";
		string me = "";
		bool showDeviation;
		bool loading;
		bool disposed;
		int requestId;

		[ObjectCreator.UseCtor]
		public RankedLeaderboardLogic(Widget widget, Action onExit)
		{
			list = widget.Get<ScrollPanelWidget>("LIST");
			template = list.Get<ScrollItemWidget>("TEMPLATE");
			list.RemoveChildren();

			title = FluentProvider.GetMessage(Title);
			widget.Get<LabelWidget>("TITLE").GetText = () => title;

			var statusLabel = widget.Get<LabelWidget>("STATUS");
			statusLabel.GetText = () => status;
			statusLabel.IsVisible = () => status.Length > 0;
			widget.Get<LabelWidget>("ME").GetText = () => me;

			widget.Get("HEADERS").Get<LabelWidget>("DEVIATION").IsVisible = () => showDeviation;

			var seasonDropdown = widget.Get<DropDownButtonWidget>("SEASON");
			seasonDropdown.IsVisible = () => seasons.Count > 0;
			seasonDropdown.GetText = () => selectedSeason != null ? SeasonLabel(selectedSeason) : FluentProvider.GetMessage(SeasonDefault);
			seasonDropdown.OnMouseDown = _ =>
			{
				ScrollItemWidget SetupItem(Season season, ScrollItemWidget itemTemplate)
				{
					var item = ScrollItemWidget.Setup(itemTemplate, () => selectedSeason == season, () =>
					{
						if (selectedSeason == season)
							return;

						selectedSeason = season;
						Load();
					});

					var label = SeasonLabel(season);
					item.Get<LabelWidget>("LABEL").GetText = () => label;
					return item;
				}

				seasonDropdown.ShowDropDown("LABEL_DROPDOWN_TEMPLATE", 250, seasons, SetupItem);
			};

			var refresh = widget.Get<ButtonWidget>("REFRESH_BUTTON");
			refresh.IsDisabled = () => loading;
			refresh.OnClick = Load;

			widget.Get<ButtonWidget>("BACK_BUTTON").OnClick = () =>
			{
				disposed = true;
				Ui.CloseWindow();
				onExit?.Invoke();
			};

			LoadSeasons();
			Load();
		}

		static string SeasonLabel(Season season)
		{
			if (season.IsActive)
				return FluentProvider.GetMessage(SeasonCurrent, "name", season.Name);

			return FluentProvider.GetMessage(SeasonArchived, "name", season.Name, "start", FormatDate(season.StartedAt), "end", FormatDate(season.EndedAt));
		}

		static string FormatDate(string iso)
		{
			if (string.IsNullOrEmpty(iso))
				return "?";

			return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
				? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
				: iso;
		}

		bool ViewingArchived => selectedSeason != null && !selectedSeason.IsActive;

		void LoadSeasons()
		{
			RankedApi.Get("/seasons", r =>
			{
				if (disposed || !r.Ok || r.Body.ValueKind != JsonValueKind.Array)
					return;

				seasons.Clear();
				foreach (var s in r.Body.EnumerateArray())
				{
					// Ids may be numbers or strings depending on the backend; keep the raw text for the query.
					var id = s.TryGetProperty("id", out var idElement)
						? idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : idElement.GetRawText()
						: null;

					if (string.IsNullOrEmpty(id))
						continue;

					seasons.Add(new Season
					{
						Id = id,
						Name = s.Str("name") ?? id,
						StartedAt = s.Str("startedAt"),
						EndedAt = s.Str("endedAt"),
						IsActive = s.Bool("isActive"),
					});
				}

				// The initial leaderboard request (no season parameter) already shows the current season,
				// so selecting it here doesn't require a reload.
				if (selectedSeason == null)
					selectedSeason = seasons.FirstOrDefault(s => s.IsActive);
			});
		}

		void Load()
		{
			var request = ++requestId;
			var season = selectedSeason;
			var archived = ViewingArchived;
			loading = true;
			status = FluentProvider.GetMessage(Loading);
			list.RemoveChildren();
			showDeviation = false;
			me = "";

			var path = "/leaderboard?limit=100";
			if (season != null)
				path += "&season=" + Uri.EscapeDataString(season.Id);

			RankedApi.Get(path, r =>
			{
				// Ignore responses superseded by a newer request (e.g. the season was changed meanwhile).
				if (disposed || request != requestId)
					return;

				loading = false;
				list.RemoveChildren();

				if (!r.Ok)
				{
					title = FluentProvider.GetMessage(Title);
					status = r.Status == 404 && r.Error == "season_not_found"
						? FluentProvider.GetMessage(SeasonNotFound)
						: FluentProvider.GetMessage(LoadFailed, "error", r.Error ?? "?");
					return;
				}

				var seasonName = r.Body.Obj("season").Str("name");
				title = seasonName != null
					? FluentProvider.GetMessage(TitleSeason, "season", seasonName)
					: FluentProvider.GetMessage(Title);

				var rows = r.Body.Obj("rows");
				var rowList = rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray().ToList() : new List<JsonElement>();

				// Rating confidence is only meaningful (and only sent) for the current season.
				showDeviation = !archived && rowList.Any(row => row.Num("deviation") != null);
				foreach (var row in rowList)
					AddRow(row);

				status = rowList.Count > 0 ? "" : FluentProvider.GetMessage(archived ? EmptyArchived : EmptyCurrent);
				list.ScrollToTop();

				me = FormatMe(r.Body.Obj("me"), (int)(r.Body.Num("total") ?? 0), archived);
			});
		}

		static string FormatMe(JsonElement mine, int total, bool archived)
		{
			if (mine.ValueKind != JsonValueKind.Object)
			{
				if (RankedApi.Token == null)
					return "";

				return FluentProvider.GetMessage(archived ? MeNotPlaced : MeNotPlayed);
			}

			var rating = $"{mine.Num("mmr") ?? 0:0}";
			var wins = $"{mine.Num("wins") ?? 0:0}";
			var losses = $"{mine.Num("losses") ?? 0:0}";

			if (mine.Bool("placed"))
			{
				var rank = mine.Num("rank");
				if (rank == null)
					return FluentProvider.GetMessage(MeUnranked, "rating", rating, "wins", wins, "losses", losses);

				return FluentProvider.GetMessage(MePlaced, "rank", $"{rank:0}", "total", $"{total}", "rating", rating, "wins", wins, "losses", losses);
			}

			if (archived)
				return FluentProvider.GetMessage(MeNotPlaced);

			var left = (int)(mine.Num("placementGamesLeft") ?? 0);
			var games = (int)(mine.Num("games") ?? 0);
			return FluentProvider.GetMessage(MePlacement, "left", $"{left}", "total", $"{games + left}");
		}

		void AddRow(JsonElement row)
		{
			var isYou = row.Bool("isYou");
			var item = ScrollItemWidget.Setup(template, () => isYou, () => { });

			var wins = row.Num("wins") ?? 0;
			var games = row.Num("games") ?? 0;
			var rank = $"{row.Num("rank"):0}";
			var name = row.Str("name") ?? "?";
			var rating = $"{row.Num("mmr"):0}";
			var deviationValue = row.Num("deviation");
			var deviation = deviationValue != null ? $"±{deviationValue:0}" : "";
			var winsText = $"{wins:0}";
			var losses = $"{row.Num("losses"):0}";
			var winrate = games > 0 ? $"{100 * wins / games:0}%" : "-";

			item.Get<LabelWidget>("RANK").GetText = () => rank;
			item.Get<LabelWidget>("NAME").GetText = () => name;
			item.Get<LabelWidget>("RATING").GetText = () => rating;
			var deviationLabel = item.Get<LabelWidget>("DEVIATION");
			deviationLabel.GetText = () => deviation;
			deviationLabel.IsVisible = () => showDeviation;
			item.Get<LabelWidget>("WINS").GetText = () => winsText;
			item.Get<LabelWidget>("LOSSES").GetText = () => losses;
			item.Get<LabelWidget>("WINRATE").GetText = () => winrate;
			item.IsVisible = () => true;
			list.AddChild(item);
		}

		protected override void Dispose(bool disposing)
		{
			disposed = true;
			base.Dispose(disposing);
		}
	}
}
