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

using OpenRA.Widgets;

namespace OpenRA.Mods.Sp.Ranked
{
	/// <summary>
	/// Some NVIDIA drivers report harmless GL_INVALID_ENUM debug messages, which the engine's GL debug callback turns
	/// into a fatal crash. Turn the callback off by default (takes effect from the next start; the main menu is shown
	/// long before the crash usually happens). Applies to every launcher, not only play.cmd.
	/// </summary>
	public class SpGraphicsDefaultsLogic : ChromeLogic
	{
		[ObjectCreator.UseCtor]
		public SpGraphicsDefaultsLogic(Widget widget)
		{
			var graphics = Game.Settings.Graphics;
			if (graphics.DisableGLDebugMessageCallback)
				return;

			graphics.DisableGLDebugMessageCallback = true;
			Game.Settings.Save();
		}
	}
}
