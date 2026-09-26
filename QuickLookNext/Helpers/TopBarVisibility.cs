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

namespace QuickLookNext.Helpers;

/// <summary>
/// v5.4.0: when the preview's top bar may be hidden again.
///
/// <para>
/// The bar used to be hidden by the "show" storyboard's completion callback, while a 100 ms poll
/// re-showed it the moment the pointer was inside the top zone. With the pointer parked on the
/// bar the two together looped: show, wait a second, hide, show again - a visible pulse, which
/// only became obvious once the bar had a scrim behind it. The decision is now one rule, kept
/// here so it can be tested without a desktop.
/// </para>
/// </summary>
internal static class TopBarVisibility
{
    /// <summary>How long the bar stays up after the pointer leaves before it fades out.</summary>
    internal const int HideAfterSeconds = 1;

    /// <summary>
    /// Whether the bar should be hidden right now: only when the plugin allows auto-hiding, when
    /// it is currently shown, and when the pointer is not resting on it.
    /// </summary>
    internal static bool ShouldHide(bool pluginAllowsAutoHide, bool barIsShown, bool pointerInTopZone)
        => pluginAllowsAutoHide && barIsShown && !pointerInTopZone;
}
