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
using OpenRA.Mods.Common;

namespace OpenRA.Mods.Sp.Ranked
{
	/// <summary>
	/// Discord rich presence for the ranked queue. The engine's DiscordService only knows fixed states
	/// (shown as the "state" line), but the "details" line is free text, so searching is shown there
	/// on top of the "In menu" state. Both calls are no-ops when Discord is disabled or not running.
	/// </summary>
	public static class RankedPresence
	{
		const string SearchingText = "Searching for a ranked match";

		static bool searching;

		/// <summary>Call when the player enters the ranked queue.</summary>
		public static void Searching()
		{
			searching = true;
			Try(() =>
			{
				// No-op if the presence already is "In menu" (DiscordService ignores repeated states).
				DiscordService.UpdateStatus(DiscordState.InMenu);
				DiscordService.UpdateDetails(SearchingText);
			});
		}

		/// <summary>
		/// Call when the player leaves the queue without a match (cancel, error, panel closed).
		/// Not needed when a match is found: joining the lobby replaces the presence anyway.
		/// </summary>
		public static void Idle()
		{
			if (!searching)
				return;

			searching = false;
			Try(() => DiscordService.UpdateDetails(null));
		}

		static void Try(Action a)
		{
			try
			{
				a();
			}
			catch (Exception e)
			{
				Log.Write("debug", $"[ranked] Discord presence update failed: {e.Message}");
			}
		}
	}
}
