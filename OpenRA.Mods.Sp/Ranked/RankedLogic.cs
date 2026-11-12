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
using System.Linq;
using System.Text.Json;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Network;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.Sp.Ranked
{
	public class RankedLogic : ChromeLogic
	{
		enum State { Checking, LoggedOut, WaitingForBrowser, NeedsNickname, Idle, Queued, MatchFound, Joining, UpdateRequired }

		// Remembered for the rest of the session.
		static string selectedFaction = "Random";
		static Color? selectedColor;

		const int QueuePollMs = 2000;
		const int LoginPollMs = 2000;

		readonly Action onExit;
		readonly Action onStart;
		readonly Action reopen;
		readonly TextFieldWidget devNameField;
		readonly TextFieldWidget nicknameField;
		readonly Dictionary<string, FactionInfo> factions;
		bool renaming;

		State state = State.Checking;
		string status = "Connecting to ranked server...";
		string detail = "";
		string info = "";
		bool devLoginAvailable;
		bool requestInFlight;
		bool pollInFlight;
		long nextPollAt;
		bool disposed;

		string userId;
		string userName;
		string loginId;
		string pollSecret;
		string browserUrl;
		string requiredVersion;
		string yourVersion;
		string downloadUrl;

		[ObjectCreator.UseCtor]
		public RankedLogic(Widget widget, ModData modData, WorldRenderer worldRenderer, Action onExit, Action onStart, Action reopen)
		{
			this.onExit = onExit;
			this.onStart = onStart;
			this.reopen = reopen;

			var worldInfo = modData.DefaultRules.Actors[SystemActors.World];
			factions = worldInfo.TraitInfos<FactionInfo>().Where(f => f.Selectable).ToDictionary(f => f.InternalName);
			if (!factions.ContainsKey(selectedFaction))
				selectedFaction = factions.Keys.FirstOrDefault() ?? "Random";

			selectedColor ??= Game.Settings.Player.Color;
			SetupPicks(widget, worldInfo, worldRenderer);

			nicknameField = widget.Get<TextFieldWidget>("NICKNAME");
			nicknameField.IsVisible = () => state == State.NeedsNickname;
			widget.Get<LabelWidget>("NICKNAME_LABEL").IsVisible = nicknameField.IsVisible;

			var leaderboard = widget.Get<ButtonWidget>("LEADERBOARD_BUTTON");
			leaderboard.IsVisible = () => state is State.Idle or State.LoggedOut or State.UpdateRequired;
			leaderboard.OnClick = () => Ui.OpenWindow("RANKED_LEADERBOARD_PANEL", new WidgetArgs { { "onExit", (Action)(() => { }) } });

			var rename = widget.Get<ButtonWidget>("RENAME_BUTTON");
			rename.IsVisible = () => state == State.Idle;
			rename.IsDisabled = () => requestInFlight;
			rename.OnClick = () =>
			{
				renaming = true;
				nicknameField.Text = userName ?? "";
				state = State.NeedsNickname;
				status = "Change nickname";
				detail = "3-16 characters: letters, digits, _ - .";
			};

			widget.Get<LabelWidget>("STATUS").GetText = () => status;
			widget.Get<LabelWidget>("DETAIL").GetText = () => detail;
			widget.Get<LabelWidget>("INFO").GetText = () => info;
			var versionText = "Your version: " + RankedApi.ClientVersion;
			widget.Get<LabelWidget>("VERSION").GetText = () => versionText;

			devNameField = widget.Get<TextFieldWidget>("DEV_NAME");
			devNameField.IsVisible = () => state == State.LoggedOut && devLoginAvailable;
			devNameField.Text = Game.Settings.Player.Name;
			widget.Get<LabelWidget>("DEV_NAME_LABEL").IsVisible = devNameField.IsVisible;

			var primary = widget.Get<ButtonWidget>("PRIMARY_BUTTON");
			primary.GetText = () => state switch
			{
				State.LoggedOut => "Sign in with Google",
				State.NeedsNickname => "Save nickname",
				State.WaitingForBrowser => "Open browser again",
				State.Idle => "Find match",
				State.Queued => "Cancel search",
				State.UpdateRequired => "Download update",
				_ => "Please wait..."
			};
			primary.IsDisabled = () => requestInFlight || state is State.Checking or State.MatchFound or State.Joining;
			primary.OnClick = OnPrimary;

			var secondary = widget.Get<ButtonWidget>("SECONDARY_BUTTON");
			secondary.GetText = () => state switch
			{
				State.LoggedOut => "Dev login",
				State.WaitingForBrowser => "Cancel sign-in",
				State.NeedsNickname when renaming => "Cancel",
				_ => "Sign out"
			};
			secondary.IsVisible = () => (state == State.LoggedOut && devLoginAvailable) || state is State.WaitingForBrowser or State.Idle or State.NeedsNickname
				|| (state == State.UpdateRequired && RankedApi.Token != null);
			secondary.IsDisabled = () => requestInFlight;
			secondary.OnClick = OnSecondary;

			var back = widget.Get<ButtonWidget>("BACK_BUTTON");
			back.OnClick = () =>
			{
				if (state == State.Queued)
					RankedApi.Delete("/queue", _ => { });

				Close();
				onExit();
			};

			RankedApi.LoadToken();
			RankedApi.Get("/config", r =>
			{
				if (!r.Ok)
				{
					SetLoggedOut("Ranked server unavailable", r.Error + $"\n{RankedApi.BaseUrl}");
					return;
				}

				devLoginAvailable = r.Body.Bool("devLogin");
				var mode = r.Body.Str("mode");
				var rated = r.Body.Bool("ratingUpdates");
				info = $"Queue: {mode?.ToUpperInvariant()}  |  Ratings: {(rated ? "ON" : "OFF (test mode)")}";

				if (!r.Body.VersionOk())
				{
					SetUpdateRequired(r.Body.Str("requiredClientVersion"), RankedApi.ClientVersion, r.Body.Str("clientDownloadUrl"));

					// Still show who is signed in; LoadProfile won't leave the UpdateRequired state.
					if (RankedApi.Token != null)
						LoadProfile();

					return;
				}

				if (RankedApi.Token == null)
					SetLoggedOut("Not signed in", "Sign in to play ranked matches.");
				else
					LoadProfile();
			});
		}

		void Close()
		{
			disposed = true;
			Ui.CloseWindow();
		}

		void SetLoggedOut(string s, string d)
		{
			state = State.LoggedOut;
			status = s;
			detail = d;
		}

		void SetUpdateRequired(string required, string yours, string url)
		{
			if (state is State.Queued or State.MatchFound)
				RankedApi.Delete("/queue", _ => { });

			state = State.UpdateRequired;
			requiredVersion = string.IsNullOrEmpty(required) ? "a different version" : required;
			yourVersion = string.IsNullOrEmpty(yours) ? RankedApi.ClientVersion : yours;
			downloadUrl = string.IsNullOrEmpty(url) ? RankedApi.DefaultDownloadUrl : url;
			UpdateRequiredText();
		}

		// Server sends 409 { error: "version_mismatch", required, yours, downloadUrl }.
		void SetUpdateRequired(ApiResult r) =>
			SetUpdateRequired(r.Body.Str("required"), r.Body.Str("yours"), r.Body.Str("downloadUrl"));

		void UpdateRequiredText()
		{
			status = "Update required";
			detail = $"This ranked server runs {requiredVersion}.\nYou have {yourVersion}. Download the new version to play ranked.";
			if (RankedApi.Token != null && userName != null)
				detail += $"\nSigned in as {userName}";
		}

		void LoadProfile(string extra = null)
		{
			RankedApi.Get("/me", r =>
			{
				if (disposed)
					return;

				// Outdated client: only pick up the name, never leave this state.
				if (state == State.UpdateRequired)
				{
					if (r.Status == 401)
					{
						RankedApi.SaveToken(null);
						userName = null;
					}
					else if (r.Ok)
					{
						userId = r.Body.Str("id");
						userName = r.Body.Str("name");
					}

					UpdateRequiredText();
					return;
				}

				if (r.Status == 401)
				{
					RankedApi.SaveToken(null);
					SetLoggedOut("Not signed in", "Your session expired. Please sign in again.");
					return;
				}

				if (!r.Ok)
				{
					SetLoggedOut("Ranked server unavailable", r.Error);
					return;
				}

				userId = r.Body.Str("id");
				userName = r.Body.Str("name");
				renaming = false;
				if (r.Body.Bool("needsNickname"))
				{
					state = State.NeedsNickname;
					status = "Choose your nickname";
					detail = "This is the only name other players will see.\n3-16 characters: letters, digits, _ - .";
					nicknameField.Text = "";
					return;
				}

				var mmr = r.Body.Num("mmr") ?? 0;
				var wins = r.Body.Num("wins") ?? 0;
				var losses = r.Body.Num("losses") ?? 0;
				state = State.Idle;
				status = $"Signed in as {userName}";
				detail = $"Rating {mmr:0}   Wins {wins:0}   Losses {losses:0}" + (extra != null ? "\n" + extra : "");

				// A match may already be waiting (e.g. after a crash or reopening the panel).
				PollQueue();
				if (extra == null)
					ShowLastResult();
			});
		}

		void ShowLastResult()
		{
			RankedApi.Get("/matches/recent", r =>
			{
				if (disposed || !r.Ok || r.Body.ValueKind != JsonValueKind.Array || state != State.Idle)
					return;

				var last = r.Body.EnumerateArray().FirstOrDefault();
				if (last.ValueKind != JsonValueKind.Object)
					return;

				var result = last.Str("result");
				var matchStatus = last.Str("status");
				var before = last.Num("mmrBefore");
				var after = last.Num("mmrAfter");
				var text = matchStatus switch
				{
					"Completed" when after != null && before != null =>
						$"Last match: {(result == "Win" ? "Victory" : "Defeat")} ({after - before:+0;-0;0} rating)",
					"Completed" => $"Last match: {result}",
					"Cancelled" when last.Bool("leftByMe") => "You left the last match before it started.",
					"Cancelled" when last.Str("reason")?.StartsWith("left_lobby", StringComparison.Ordinal) == true =>
						"Last match was cancelled: your opponent left the lobby.",
					"Cancelled" => "Last match was cancelled.",
					"Void" => "Last match was voided (no result).",
					_ => null
				};

				if (text != null)
					detail += "\n" + text;
			});
		}

		void OnPrimary()
		{
			switch (state)
			{
				case State.LoggedOut: StartGoogleLogin(); break;
				case State.NeedsNickname: SaveNickname(); break;
				case State.WaitingForBrowser: RankedApi.OpenInBrowser(browserUrl); break;
				case State.Idle: JoinQueue(); break;
				case State.Queued: LeaveQueue(); break;
				case State.UpdateRequired: RankedApi.OpenInBrowser(downloadUrl ?? RankedApi.DefaultDownloadUrl); break;
			}
		}

		void OnSecondary()
		{
			switch (state)
			{
				case State.LoggedOut:
					requestInFlight = true;
					RankedApi.Post("/auth/dev-login", new { name = devNameField.Text }, r =>
					{
						requestInFlight = false;
						if (!r.Ok)
						{
							detail = "Dev login failed: " + r.Error;
							return;
						}

						RankedApi.SaveToken(r.Body.Str("token"));
						LoadProfile();
					});
					break;

				case State.NeedsNickname when renaming:
					LoadProfile();
					break;

				case State.WaitingForBrowser:
					loginId = null;
					SetLoggedOut("Not signed in", "Sign-in cancelled.");
					break;

				case State.UpdateRequired:
					RankedApi.Post("/auth/logout", null, _ => { });
					RankedApi.SaveToken(null);
					userName = null;
					UpdateRequiredText();
					break;

				default:
					RankedApi.Post("/auth/logout", null, _ => { });
					RankedApi.SaveToken(null);
					SetLoggedOut("Not signed in", "Signed out.");
					break;
			}
		}

		void SaveNickname()
		{
			requestInFlight = true;
			RankedApi.Put("/me/nickname", new { nickname = nicknameField.Text.Trim() }, r =>
			{
				requestInFlight = false;
				if (!r.Ok)
				{
					detail = r.Error switch
					{
						"nickname_taken" => "That nickname is already taken.",
						"invalid_nickname" => "Use 3-16 characters: letters, digits, _ - .",
						_ => "Could not save nickname: " + r.Error
					};
					return;
				}

				LoadProfile();
			});
		}

		void SetupPicks(Widget widget, ActorInfo worldInfo, WorldRenderer worldRenderer)
		{
			bool PicksVisible() => state == State.Idle;
			widget.Get<LabelWidget>("FACTION_LABEL").IsVisible = PicksVisible;
			widget.Get<LabelWidget>("COLOR_LABEL").IsVisible = PicksVisible;

			var factionDropdown = widget.Get<DropDownButtonWidget>("FACTION");
			factionDropdown.IsVisible = PicksVisible;
			factionDropdown.IsDisabled = () => requestInFlight;
			factionDropdown.Get<ImageWidget>("FACTIONFLAG").GetImageCollection = () => "flags";
			factionDropdown.Get<ImageWidget>("FACTIONFLAG").GetImageName = () => selectedFaction;
			factionDropdown.Get<LabelWidget>("FACTIONNAME").GetText = () =>
				factions.TryGetValue(selectedFaction, out var f) ? FluentProvider.GetMessage(f.Name) : selectedFaction;

			factionDropdown.OnMouseDown = _ =>
			{
				ScrollItemWidget SetupItem(string id, ScrollItemWidget template)
				{
					var item = ScrollItemWidget.Setup(template, () => selectedFaction == id, () => selectedFaction = id);
					var name = FluentProvider.GetMessage(factions[id].Name);
					item.Get<LabelWidget>("LABEL").GetText = () => name;
					var flag = item.Get<ImageWidget>("FLAG");
					flag.GetImageCollection = () => "flags";
					flag.GetImageName = () => id;
					return item;
				}

				factionDropdown.ShowDropDown("FACTION_DROPDOWN_TEMPLATE", 180, factions.Keys, SetupItem);
			};

			var colorManager = worldInfo.TraitInfo<IColorPickerManagerInfo>();
			var colorDropdown = widget.Get<DropDownButtonWidget>("COLOR");
			colorDropdown.IsVisible = PicksVisible;
			colorDropdown.IsDisabled = () => requestInFlight;
			colorDropdown.Get<ColorBlockWidget>("COLORBLOCK").GetColor = () => selectedColor ?? Color.White;
			colorDropdown.OnMouseDown = _ => colorManager.ShowColorDropDown(colorDropdown, selectedColor ?? Color.White,
				selectedFaction == "Random" ? null : selectedFaction, worldRenderer, c => selectedColor = c);
		}

		void StartGoogleLogin()
		{
			requestInFlight = true;
			RankedApi.Post("/auth/login", null, r =>
			{
				requestInFlight = false;
				if (!r.Ok)
				{
					detail = "Could not start sign-in: " + r.Error;
					return;
				}

				if (!r.Body.Bool("googleConfigured"))
				{
					detail = "Google sign-in is not configured on the ranked server yet." + (devLoginAvailable ? " Use Dev login." : "");
					return;
				}

				loginId = r.Body.Str("loginId");
				pollSecret = r.Body.Str("pollSecret");
				browserUrl = r.Body.Str("browserUrl");
				state = State.WaitingForBrowser;
				status = "Continue in your browser";
				detail = "Sign in with Google in the browser window that just opened.\nThis screen updates automatically.";
				nextPollAt = Game.RunTime + LoginPollMs;
				RankedApi.OpenInBrowser(browserUrl);
			});
		}

		void PollLogin()
		{
			var id = loginId;
			pollInFlight = true;
			RankedApi.Post($"/auth/login/{id}/poll", new { pollSecret }, r =>
			{
				pollInFlight = false;
				if (disposed || state != State.WaitingForBrowser || id != loginId)
					return;

				var s = r.Ok ? r.Body.Str("status") : null;
				switch (s)
				{
					case "complete":
						RankedApi.SaveToken(r.Body.Str("token"));
						LoadProfile();
						break;
					case "failed":
						SetLoggedOut("Sign-in failed", r.Body.Str("error") ?? "");
						break;
					case "expired":
					case null when r.Status == 404:
						SetLoggedOut("Sign-in expired", "Please try again.");
						break;
				}
			});
		}

		void JoinQueue()
		{
			requestInFlight = true;
			var color = selectedColor ?? Game.Settings.Player.Color;
			RankedApi.Post("/queue", new { faction = selectedFaction, color = $"{color.R:X2}{color.G:X2}{color.B:X2}" }, r =>
			{
				requestInFlight = false;
				if (r.Error == "version_mismatch")
				{
					SetUpdateRequired(r);
					return;
				}

				if (!r.Ok)
				{
					detail = r.Error switch
					{
						"already_in_match" => "You are already in a match.",
						"nickname_required" => "Choose a nickname first.",
						_ => "Could not join queue: " + r.Error
					};

					PollQueue();
					return;
				}

				state = State.Queued;
				status = "Searching for an opponent...";
				detail = "";
				nextPollAt = Game.RunTime;
			});
		}

		void LeaveQueue()
		{
			requestInFlight = true;
			RankedApi.Delete("/queue", r =>
			{
				requestInFlight = false;
				LoadProfile("Search cancelled.");
			});
		}

		void PollQueue()
		{
			pollInFlight = true;
			RankedApi.Get("/queue/status", r =>
			{
				pollInFlight = false;
				if (disposed || state is State.Joining or State.LoggedOut or State.UpdateRequired)
					return;

				// Stops polling: Tick only polls while Queued / MatchFound.
				if (r.Error == "version_mismatch")
				{
					SetUpdateRequired(r);
					return;
				}

				if (!r.Ok)
				{
					detail = "Connection problem: " + r.Error;
					return;
				}

				switch (r.Body.Str("state"))
				{
					case "queued":
						state = State.Queued;
						status = "Searching for an opponent...";
						var wait = (int)(r.Body.Num("waitSeconds") ?? 0);
						detail = $"Time in queue {wait / 60}:{wait % 60:00}   Players searching: {r.Body.Num("queueSize") ?? 0:0}";
						break;

					case "match_starting":
						state = State.MatchFound;
						status = "Match found!";
						detail = $"vs {r.Body.Obj("match").Obj("opponent").Str("name")}. Starting game server...";
						break;

					case "match_ready":
						JoinMatch(r.Body.Obj("match"));
						break;

					case "idle":
						if (state is State.Queued or State.MatchFound)
							LoadProfile("Removed from the queue (match cancelled or timed out).");
						break;
				}
			});
		}

		void JoinMatch(JsonElement match)
		{
			state = State.Joining;
			var opponent = match.Obj("opponent");
			status = "Match found!";
			detail = $"vs {opponent.Str("name")} (rating {opponent.Num("mmr") ?? 0:0}). Joining game server...";

			var host = match.Str("host");
			var port = (int)(match.Num("port") ?? 0);
			var password = match.Str("password");
			var authCommand = $"ranked-auth {userId} {match.Str("ticket")}";
			var matchId = match.Str("id");

			// Leaving the lobby (or failing to connect) means "not interested": cancel the match, don't rejoin.
			void LeaveMatch()
			{
				Game.Disconnect();
				RankedApi.Post($"/matches/{matchId}/leave", null, _ => reopen());
			}

			// Short pause so the player can read who they are facing.
			Game.RunAfterDelay(1500, () =>
			{
				if (disposed)
					return;

				var orderManager = Game.JoinServer(new ConnectionTarget(host, port), password);
				Ui.OpenWindow("CONNECTING_PANEL", new WidgetArgs
				{
					{ "endpoint", new ConnectionTarget(host, port) },
					{
						"onConnect", (Action)(() =>
						{
							SendWhenJoined(orderManager, authCommand, 0);
							Close();
							Game.OpenWindow("SERVER_LOBBY", new WidgetArgs
							{
								{ "onExit", (Action)(() => LeaveMatch()) },
								{ "onStart", onStart },
								{ "skirmishMode", false }
							});
						})
					},
					{ "onAbort", (Action)(() => LeaveMatch()) },
					{ "onRetry", (Action<string>)(_ => LeaveMatch()) }
				});
			});
		}

		// "Connected" fires before the server handshake completes; commands sent earlier get the connection rejected.
		// Our client appears in the lobby info only after the handshake, so wait for that.
		static void SendWhenJoined(OrderManager orderManager, string command, int attempt)
		{
			if (orderManager.LocalClient != null)
			{
				orderManager.IssueOrder(Order.Command(command));
				return;
			}

			if (attempt < 150)
				Game.RunAfterDelay(100, () => SendWhenJoined(orderManager, command, attempt + 1));
			else
				Log.Write("debug", "[ranked] Gave up sending ranked auth: lobby never synced.");
		}

		public override void Tick()
		{
			if (disposed || requestInFlight || pollInFlight || Game.RunTime < nextPollAt)
				return;

			if (state == State.Queued || state == State.MatchFound)
			{
				nextPollAt = Game.RunTime + QueuePollMs;
				PollQueue();
			}
			else if (state == State.WaitingForBrowser && loginId != null)
			{
				nextPollAt = Game.RunTime + LoginPollMs;
				PollLogin();
			}
		}

		protected override void Dispose(bool disposing)
		{
			disposed = true;
			base.Dispose(disposing);
		}
	}
}
