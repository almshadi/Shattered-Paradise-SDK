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
using System.Text.Json;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Widgets;

namespace OpenRA.Mods.Sp.Ranked
{
	public class RankedLeaderboardLogic : ChromeLogic
	{
		readonly ScrollPanelWidget list;
		readonly ScrollItemWidget template;
		string status = "";
		string me = "";
		bool loading;
		bool disposed;

		[ObjectCreator.UseCtor]
		public RankedLeaderboardLogic(Widget widget, Action onExit)
		{
			list = widget.Get<ScrollPanelWidget>("LIST");
			template = list.Get<ScrollItemWidget>("TEMPLATE");
			list.RemoveChildren();

			var statusLabel = widget.Get<LabelWidget>("STATUS");
			statusLabel.GetText = () => status;
			statusLabel.IsVisible = () => status.Length > 0;
			widget.Get<LabelWidget>("ME").GetText = () => me;

			var refresh = widget.Get<ButtonWidget>("REFRESH_BUTTON");
			refresh.IsDisabled = () => loading;
			refresh.OnClick = Load;

			widget.Get<ButtonWidget>("BACK_BUTTON").OnClick = () =>
			{
				disposed = true;
				Ui.CloseWindow();
				onExit?.Invoke();
			};

			Load();
		}

		void Load()
		{
			loading = true;
			status = "Loading...";
			RankedApi.Get("/leaderboard?limit=100", r =>
			{
				loading = false;
				if (disposed)
					return;

				list.RemoveChildren();
				if (!r.Ok)
				{
					status = "Could not load leaderboard: " + r.Error;
					return;
				}

				var rows = r.Body.Obj("rows");
				var count = 0;
				foreach (var row in rows.EnumerateArray())
				{
					AddRow(row);
					count++;
				}

				status = count == 0 ? "No ranked games played yet." : "";
				list.ScrollToTop();

				var mine = r.Body.Obj("me");
				var total = (int)(r.Body.Num("total") ?? 0);
				me = mine.ValueKind == JsonValueKind.Object
					? $"You: #{mine.Num("rank"):0} of {total}  -  Rating {mine.Num("mmr"):0}  ({mine.Num("wins"):0}W / {mine.Num("losses"):0}L)"
					: RankedApi.Token != null ? "Play a ranked match to appear on the leaderboard." : "";
			});
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
			var winsText = $"{wins:0}";
			var losses = $"{row.Num("losses"):0}";
			var winrate = games > 0 ? $"{100 * wins / games:0}%" : "-";

			item.Get<LabelWidget>("RANK").GetText = () => rank;
			item.Get<LabelWidget>("NAME").GetText = () => name;
			item.Get<LabelWidget>("RATING").GetText = () => rating;
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
