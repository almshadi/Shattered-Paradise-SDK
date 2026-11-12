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
	/// <summary>Main-menu button that opens the ranked panel and hides the main menu while it is open.</summary>
	public class RankedMenuButtonLogic : ChromeLogic
	{
		[ObjectCreator.UseCtor]
		public RankedMenuButtonLogic(Widget widget)
		{
			var button = (ButtonWidget)widget;
			button.OnClick = () => OpenPanel(widget);

			// One background check so outdated clients see they need an update before opening the panel.
			// Failures (server unreachable etc.) leave the normal label.
			var updateRequired = false;
			var normalText = button.GetText;
			button.GetText = () => updateRequired ? "Ranked (update)" : normalText();
			RankedApi.Get("/config", r =>
			{
				if (r.Ok && !r.Body.VersionOk())
					updateRequired = true;
			});
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
