// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using QuickLook.Common.Helpers;
using System.Windows;

namespace QuickLookNext.Helpers;

/// <summary>
/// v5.3.0: one place that decides whether decorative motion is allowed.
///
/// <para>
/// Two sources have a say. Windows itself - the accessibility page's "Animation effects" switch,
/// which WPF reports as <see cref="SystemParameters.ClientAreaAnimation"/> - and the app's own
/// <c>ShowWindowTransition</c> option. Either one turning it off means no fades and no slides; the
/// surfaces simply appear. Nothing here gates progress feedback (the busy spinner is information,
/// not decoration).
/// </para>
/// </summary>
internal static class Motion
{
    /// <summary>
    /// Whether fade / slide transitions may run: the app follows the system's animation setting
    /// everywhere, and the preview window's own transitions additionally honour
    /// <c>ShowWindowTransition</c>.
    /// </summary>
    internal static bool IsEnabled => ShouldAnimate(SystemParameters.ClientAreaAnimation, true);

    /// <summary>Whether the preview window may animate when it opens or when content appears.</summary>
    internal static bool WindowTransitionsEnabled
        => ShouldAnimate(
            SystemParameters.ClientAreaAnimation,
            SettingHelper.Get("ShowWindowTransition", true, "QuickLookNext"));

    /// <summary>
    /// The decision itself, so it can be tested without a desktop: a transition needs the system
    /// to allow animation and the app's own option to be on.
    /// </summary>
    internal static bool ShouldAnimate(bool systemAnimations, bool appOption)
        => systemAnimations && appOption;
}
