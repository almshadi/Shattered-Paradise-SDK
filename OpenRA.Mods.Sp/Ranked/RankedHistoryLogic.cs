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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.Sp.Ranked
{
	public class RankedHistoryLogic : ChromeLogic
	{
		const int PageSize = 20;

		[FluentReference]
		const string Loading = "label-ranked-history-loading";

		[FluentReference]
		const string SignedOut = "label-ranked-history-signed-out";

		[FluentReference]
		const string Empty = "label-ranked-history-empty";

		[FluentReference("error")]
		const string LoadFailed = "label-ranked-history-load-failed";

		[FluentReference("a", "b")]
		const string Versus = "label-ranked-history-versus";

		[FluentReference]
		const string Victory = "label-ranked-history-victory";

		[FluentReference]
		const string Defeat = "label-ranked-history-defeat";

		[FluentReference]
		const string Draw = "label-ranked-history-draw";

		[FluentReference]
		const string Cancelled = "label-ranked-history-cancelled";

		[FluentReference]
		const string Voided = "label-ranked-history-void";

		[FluentReference]
		const string InProgress = "label-ranked-history-in-progress";

		[FluentReference]
		const string ReasonDefeat = "label-ranked-history-reason-defeat";

		[FluentReference]
		const string ReasonSurrender = "label-ranked-history-reason-surrender";

		[FluentReference]
		const string ReasonDisconnect = "label-ranked-history-reason-disconnect";

		[FluentReference]
		const string ReasonNoShow = "label-ranked-history-reason-no-show";

		[FluentReference]
		const string ReasonLeftLobby = "label-ranked-history-reason-left-lobby";

		[FluentReference]
		const string ReasonAllDisconnected = "label-ranked-history-reason-all-disconnected";

		[FluentReference("text")]
		const string ReasonAdmin = "label-ranked-history-reason-admin";

		[FluentReference]
		const string Placement = "label-ranked-history-placement";

		[FluentReference]
		const string Unrated = "label-ranked-history-unrated";

		[FluentReference("result", "reason")]
		const string ResultWithReason = "label-ranked-history-result-with-reason";

		[FluentReference]
		const string Downloading = "label-ranked-history-downloading";

		[FluentReference("file")]
		const string Saved = "label-ranked-history-saved";

		[FluentReference("error")]
		const string DownloadFailed = "label-ranked-history-download-failed";

		[FluentReference]
		const string NoReplay = "label-ranked-history-no-replay";

		static readonly Color WinColor = Color.LimeGreen;
		static readonly Color LossColor = Color.Tomato;
		static readonly Color NeutralColor = Color.DarkGray;

		readonly ScrollPanelWidget list;
		readonly ScrollItemWidget template;
		readonly Dictionary<string, FactionInfo> factions;

		string status = "";
		string info = "";
		Color infoColor = ChromeMetrics.Get<Color>("TextColor");
		bool loading;
		bool downloading;
		bool hasMore;
		bool disposed;
		string lastCreatedAt;
		Row selected;

		sealed class Row
		{
			public string Id;
			public bool HasReplay;
			public string Date;
			public string FileDate;
			public string Opponent;
			public string Detail;
		}

		[ObjectCreator.UseCtor]
		public RankedHistoryLogic(Widget widget, Action onExit)
		{
			var worldInfo = Game.ModData.DefaultRules.Actors[SystemActors.World];
			factions = new Dictionary<string, FactionInfo>();
			foreach (var f in worldInfo.TraitInfos<FactionInfo>())
				factions.TryAdd(f.InternalName, f);

			list = widget.Get<ScrollPanelWidget>("LIST");
			template = list.Get<ScrollItemWidget>("TEMPLATE");
			list.RemoveChildren();

			var statusLabel = widget.Get<LabelWidget>("STATUS");
			statusLabel.GetText = () => status;
			statusLabel.IsVisible = () => status.Length > 0;

			var infoLabel = widget.Get<LabelWidget>("INFO");
			infoLabel.GetText = () => info;
			infoLabel.GetColor = () => infoColor;

			var refresh = widget.Get<ButtonWidget>("REFRESH_BUTTON");
			refresh.IsDisabled = () => loading;
			refresh.OnClick = () => Load(false);

			var more = widget.Get<ButtonWidget>("LOAD_MORE_BUTTON");
			more.IsDisabled = () => loading || !hasMore;
			more.OnClick = () => Load(true);

			var download = widget.Get<ButtonWidget>("DOWNLOAD_BUTTON");
			download.IsDisabled = () => downloading || selected == null || !selected.HasReplay;
			download.OnClick = DownloadReplay;

			widget.Get<ButtonWidget>("BACK_BUTTON").OnClick = () =>
			{
				disposed = true;
				Ui.CloseWindow();
				onExit?.Invoke();
			};

			Load(false);
		}

		void Load(bool append)
		{
			if (RankedApi.Token == null)
			{
				list.RemoveChildren();
				selected = null;
				hasMore = false;
				status = FluentProvider.GetMessage(SignedOut);
				return;
			}

			var path = $"/matches/history?limit={PageSize}";
			if (append && lastCreatedAt != null)
				path += "&before=" + Uri.EscapeDataString(lastCreatedAt);

			loading = true;
			if (!append)
			{
				status = FluentProvider.GetMessage(Loading);
				info = "";
			}

			RankedApi.Get(path, r =>
			{
				loading = false;
				if (disposed)
					return;

				if (!append)
				{
					list.RemoveChildren();
					selected = null;
					lastCreatedAt = null;
				}

				if (!r.Ok)
				{
					hasMore = false;
					if (r.Status == 401)
						status = FluentProvider.GetMessage(SignedOut);
					else if (append)
						SetInfo(FluentProvider.GetMessage(LoadFailed, "error", r.Error), LossColor);
					else
						status = FluentProvider.GetMessage(LoadFailed, "error", r.Error);

					return;
				}

				var count = 0;
				if (r.Body.ValueKind == JsonValueKind.Array)
				{
					foreach (var match in r.Body.EnumerateArray())
					{
						try
						{
							AddRow(match);
						}
						catch (Exception e)
						{
							Log.Write("debug", $"[ranked] Could not show match {match.Str("id")}: {e.Message}");
						}

						lastCreatedAt = match.Str("createdAt") ?? lastCreatedAt;
						count++;
					}
				}

				hasMore = count >= PageSize && lastCreatedAt != null;
				if (!append)
				{
					status = count == 0 ? FluentProvider.GetMessage(Empty) : "";
					list.ScrollToTop();
				}
			});
		}

		void AddRow(JsonElement match)
		{
			var me = match.Obj("me");
			var opponents = match.Obj("opponents");
			var opponentList = opponents.ValueKind == JsonValueKind.Array ? opponents.EnumerateArray().ToList() : new List<JsonElement>();

			var when = ParseTime(match.Str("startedAt")) ?? ParseTime(match.Str("createdAt"));
			var local = when?.ToLocalTime();

			var row = new Row
			{
				Id = match.Str("id"),
				HasReplay = match.Bool("hasReplay"),
				Date = local?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-",
				FileDate = local?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown",
				Opponent = opponentList.Count > 0 ? string.Join(", ", opponentList.Select(o => o.Str("name") ?? "?")) : "-",
			};

			var map = MapTitle(match.Str("mapUid"));
			var myFaction = FactionName(me.Str("faction"));
			var theirFactions = opponentList.Count > 0
				? string.Join("/", opponentList.Select(o => FactionName(o.Str("faction"))).Distinct())
				: "?";
			var factionsText = FluentProvider.GetMessage(Versus, "a", myFaction, "b", theirFactions);

			var (result, color) = ResultText(match.Str("status"), me.Str("result"));
			var reason = ReasonText(match.Str("reason"));
			var resultTooltip = reason ?? "";
			row.Detail = reason != null ? FluentProvider.GetMessage(ResultWithReason, "result", result, "reason", reason) : result;

			var rating = RatingText(match, me);
			var duration = DurationText(match.Num("durationSeconds"));

			var item = ScrollItemWidget.Setup(template, () => selected == row, () =>
			{
				selected = row;
				SetInfo($"{row.Date}  -  {row.Opponent}  -  {map}  -  {row.Detail}",
					color == NeutralColor ? ChromeMetrics.Get<Color>("TextColor") : color);
			});

			item.Get<LabelWidget>("DATE").GetText = () => row.Date;
			SetFittedText(item.Get<LabelWidget>("OPPONENT"), row.Opponent);
			SetFittedText(item.Get<LabelWidget>("MAP"), map);
			SetFittedText(item.Get<LabelWidget>("FACTIONS"), factionsText);

			var resultLabel = item.Get<LabelWithTooltipWidget>("RESULT");
			var resultDisplay = reason != null ? result + "*" : result;
			resultLabel.GetText = () => resultDisplay;
			resultLabel.GetColor = () => color;
			resultLabel.GetTooltipText = reason != null ? () => resultTooltip : null;

			var ratingLabel = item.Get<LabelWidget>("RATING");
			ratingLabel.GetText = () => rating.Text;
			if (rating.Color.HasValue)
			{
				var c = rating.Color.Value;
				ratingLabel.GetColor = () => c;
			}

			item.Get<LabelWidget>("DURATION").GetText = () => duration;
			item.IsVisible = () => true;
			list.AddChild(item);
		}

		static void SetFittedText(LabelWidget label, string text)
		{
			var font = Game.Renderer.Fonts[label.Font];
			var fitted = WidgetUtils.TruncateText(text, label.Bounds.Width, font);
			label.GetText = () => fitted;
		}

		static DateTime? ParseTime(string iso)
		{
			if (string.IsNullOrEmpty(iso))
				return null;

			return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
				? DateTime.SpecifyKind(t, DateTimeKind.Utc)
				: null;
		}

		static string MapTitle(string uid)
		{
			if (string.IsNullOrEmpty(uid))
				return "-";

			try
			{
				var preview = Game.ModData.MapCache[uid];
				if (preview.Status == MapStatus.Available && !string.IsNullOrEmpty(preview.Title))
					return preview.Title;
			}
			catch (Exception e)
			{
				Log.Write("debug", $"[ranked] Map lookup failed for {uid}: {e.Message}");
			}

			return uid.Length > 8 ? uid[..8] : uid;
		}

		string FactionName(string id)
		{
			if (string.IsNullOrEmpty(id))
				return "?";

			return factions.TryGetValue(id, out var f) ? FluentProvider.GetMessage(f.Name) : id;
		}

		static (string Text, Color Color) ResultText(string status, string result)
		{
			switch (status)
			{
				case "Completed":
					switch (result)
					{
						case "Win": return (FluentProvider.GetMessage(Victory), WinColor);
						case "Loss": return (FluentProvider.GetMessage(Defeat), LossColor);
						default: return (FluentProvider.GetMessage(Draw), NeutralColor);
					}

				case "Cancelled": return (FluentProvider.GetMessage(Cancelled), NeutralColor);
				case "Void": return (FluentProvider.GetMessage(Voided), NeutralColor);
				default: return (FluentProvider.GetMessage(InProgress), NeutralColor);
			}
		}

		static string ReasonText(string reason)
		{
			if (string.IsNullOrEmpty(reason))
				return null;

			if (reason.StartsWith("left_lobby", StringComparison.Ordinal))
				return FluentProvider.GetMessage(ReasonLeftLobby);

			if (reason.StartsWith("admin", StringComparison.Ordinal))
			{
				var colon = reason.IndexOf(':');
				var text = colon >= 0 ? reason[(colon + 1)..].Trim() : "";
				return FluentProvider.GetMessage(ReasonAdmin, "text", text);
			}

			switch (reason)
			{
				case "defeat": return FluentProvider.GetMessage(ReasonDefeat);
				case "surrender": return FluentProvider.GetMessage(ReasonSurrender);
				case "opponent_disconnected": return FluentProvider.GetMessage(ReasonDisconnect);
				case "start_timeout": return FluentProvider.GetMessage(ReasonNoShow);
				case "all_players_disconnected": return FluentProvider.GetMessage(ReasonAllDisconnected);
				default: return reason.Replace('_', ' ');
			}
		}

		static (string Text, Color? Color) RatingText(JsonElement match, JsonElement me)
		{
			if (!match.Bool("rated"))
				return (FluentProvider.GetMessage(Unrated), NeutralColor);

			if (me.Bool("placement"))
				return (FluentProvider.GetMessage(Placement), NeutralColor);

			var before = me.Num("mmrBefore");
			var after = me.Num("mmrAfter");
			if (!before.HasValue || !after.HasValue)
				return ("-", null);

			var delta = (int)Math.Round(after.Value - before.Value);
			if (delta > 0)
				return ("+" + delta.ToString(CultureInfo.InvariantCulture), WinColor);

			if (delta < 0)
				return (delta.ToString(CultureInfo.InvariantCulture), LossColor);

			return ("0", null);
		}

		static string DurationText(double? seconds)
		{
			if (!seconds.HasValue || seconds.Value < 0)
				return "-";

			var total = (int)Math.Round(seconds.Value);
			return $"{total / 60}:{total % 60:00}";
		}

		void SetInfo(string text, Color color)
		{
			info = text;
			infoColor = color;
		}

		void DownloadReplay()
		{
			var row = selected;
			if (row == null)
				return;

			if (!row.HasReplay || string.IsNullOrEmpty(row.Id))
			{
				SetInfo(FluentProvider.GetMessage(NoReplay), NeutralColor);
				return;
			}

			downloading = true;
			SetInfo(FluentProvider.GetMessage(Downloading), ChromeMetrics.Get<Color>("TextColor"));
			var manifest = Game.ModData.Manifest;
			var dir = Path.Combine(Platform.SupportDir, "Replays", manifest.Id, manifest.Metadata.Version);

			RankedApi.Download($"/matches/{Uri.EscapeDataString(row.Id)}/replay", (r, data, serverName) =>
			{
				if (!r.Ok || data == null)
				{
					downloading = false;
					if (!disposed)
						SetInfo(FluentProvider.GetMessage(DownloadFailed, "error", r.Status == 404 ? FluentProvider.GetMessage(NoReplay) : r.Error), LossColor);

					return;
				}

				var fallback = $"{row.FileDate}_{row.Opponent}.orarep";
				Task.Run(() =>
				{
					string saved = null;
					string error = null;
					try
					{
						saved = SaveReplay(dir, serverName, fallback, data);
					}
					catch (Exception e)
					{
						error = e.Message;
						Log.Write("debug", $"[ranked] Could not save replay: {e}");
					}

					Game.RunAfterTick(() =>
					{
						downloading = false;
						if (disposed)
							return;

						if (saved != null)
							SetInfo(FluentProvider.GetMessage(Saved, "file", Path.GetFileName(saved)), WinColor);
						else
							SetInfo(FluentProvider.GetMessage(DownloadFailed, "error", error), LossColor);
					});
				});
			});
		}

		static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars()
			.Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

		static string SanitizeFileName(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return null;

			// Never trust a server-supplied path: keep only the last segment.
			name = name.Replace('\\', '/');
			var slash = name.LastIndexOf('/');
			if (slash >= 0)
				name = name[(slash + 1)..];

			var chars = name.Select(c => InvalidFileNameChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
			name = new string(chars).Trim().TrimEnd('.');
			if (name.Length == 0 || name.Trim('_', '.').Length == 0)
				return null;

			if (!name.EndsWith(".orarep", StringComparison.OrdinalIgnoreCase))
				name += ".orarep";

			return name;
		}

		static string SaveReplay(string dir, string serverName, string fallback, byte[] data)
		{
			var name = SanitizeFileName(serverName) ?? SanitizeFileName(fallback) ?? "ranked-replay.orarep";
			Directory.CreateDirectory(dir);

			var stem = Path.GetFileNameWithoutExtension(name);
			var path = Path.Combine(dir, name);
			for (var i = 2; File.Exists(path); i++)
				path = Path.Combine(dir, $"{stem} ({i}).orarep");

			// CreateNew: never overwrite, even if a file appeared after the existence check.
			using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
				stream.Write(data, 0, data.Length);

			return path;
		}

		protected override void Dispose(bool disposing)
		{
			disposed = true;
			base.Dispose(disposing);
		}
	}
}
