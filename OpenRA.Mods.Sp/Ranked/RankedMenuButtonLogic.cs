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
using OpenRA.Mods.Common.Widgets;
using OpenRA.Widgets;

namespace OpenRA.Mods.Sp.Ranked
{
	/// <summary>
	/// Main-menu button that opens the ranked panel and hides the main menu while it is open.
	/// Its label shows "Ranked (update)" for outdated clients, otherwise how many players are searching.
	/// </summary>
	public class RankedMenuButtonLogic : ChromeLogic
	{
		// Queue activity is refreshed only while the button is actually on screen: ChromeLogic.Tick runs
		// only for visible widgets attached to the UI tree, so polling stops in-game, while the ranked
		// panel hides the menu, and once the main menu is removed.
		const int StatsPollIntervalMs = 30000;

		readonly ButtonWidget button;
		readonly Func<string> normalText;

		bool updateRequired;
		int searching;
		bool statsRequestInFlight;
		bool statsUnavailable;
		long nextStatsPoll;

		string cachedLabelSource;
		string cachedLabel;

		[ObjectCreator.UseCtor]
		public RankedMenuButtonLogic(Widget widget)
		{
			button = (ButtonWidget)widget;
			button.OnClick = () => OpenPanel(widget);

			// One background check so outdated clients see they need an update before opening the panel.
			// Failures (server unreachable etc.) leave the normal label.
			normalText = button.GetText;
			button.GetText = GetLabel;
			RankedApi.Get("/config", r =>
			{
				if (r.Ok && !r.Body.VersionOk())
					updateRequired = true;
			});
		}

		public override void Tick()
		{
			if (statsUnavailable || statsRequestInFlight || Game.RunTime < nextStatsPoll)
				return;

			statsRequestInFlight = true;
			RankedApi.Get("/queue/stats", r =>
			{
				statsRequestInFlight = false;
				nextStatsPoll = Game.RunTime + StatsPollIntervalMs;

				// Older backends don't have the endpoint: stop asking.
				if (r.Status == 404)
				{
					statsUnavailable = true;
					searching = 0;
					return;
				}

				// Unreachable or bad data: show the plain label rather than a stale count.
				var count = r.Ok ? r.Body.Num("searching") : null;
				searching = count.HasValue && count.Value > 0 ? (int)Math.Min(count.Value, 9999) : 0;
			});
		}

		string GetLabel()
		{
			if (updateRequired)
				return "Ranked (update)";

			var text = normalText();
			if (searching <= 0)
				return text;

			var source = text + "|" + searching;
			if (source == cachedLabelSource)
				return cachedLabel;

			// The button is narrow; fall back to a shorter form if the full one doesn't fit.
			var label = $"{text} · {searching} searching";
			if (!Fits(label))
				label = $"{text} · {searching}";

			cachedLabelSource = source;
			cachedLabel = label;
			return label;
		}

		bool Fits(string text)
		{
			try
			{
				if (Game.Renderer == null || !Game.Renderer.Fonts.TryGetValue(button.Font, out var font))
					return true;

				return font.Measure(text).X <= button.Bounds.Width - 8;
			}
			catch (Exception)
			{
				return true;
			}
		}

		static void OpenPanel(Widget button)
		{
			var mainMenu = FindAncestor(button, "MAINMENU");
			var menus = FindAncestor(button, "MENUS");
			var previousVisibility = menus?.IsVisible;
			if (menus != null)
				menus.IsVisible = () => false;

			void Restore()
			{
				if (menus != null && previousVisibility != null)
					menus.IsVisible = previousVisibility;
			}

			void Open()
			{
				Game.OpenWindow("RANKED_PANEL", new WidgetArgs
				{
					{ "onExit", (Action)Restore },

					// Same as MainMenuLogic.RemoveShellmapUI: drop the menu once the game starts.
					{ "onStart", (Action)(() => mainMenu?.Parent?.RemoveChild(mainMenu)) },
					{ "reopen", (Action)Open }
				});
			}

			Open();
		}

		static Widget FindAncestor(Widget w, string id)
		{
			for (var p = w; p != null; p = p.Parent)
				if (p.Id == id)
					return p;

			return null;
		}
	}
}
