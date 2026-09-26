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

using System.Windows;
using QuickLookNext.Helpers;

namespace QuickLook.Tests;

/// <summary>
/// v5.3.0: decorative motion (the caption bar's fade, the first content fade, the window's show
/// transition) runs only when both Windows and the app allow it - the accessibility setting is
/// what matters most, and an app that ignores it is a bug for anyone who needs it off.
/// </summary>
internal class MotionTests
{
    public void MotionNeedsBothTheSystemAndTheAppToAllowIt()
    {
        Assert.True(Motion.ShouldAnimate(systemAnimations: true, appOption: true),
            "both allow it");
        Assert.False(Motion.ShouldAnimate(systemAnimations: true, appOption: false),
            "the app option alone can turn it off");
        Assert.False(Motion.ShouldAnimate(systemAnimations: false, appOption: true),
            "and so can the system's accessibility setting");
        Assert.False(Motion.ShouldAnimate(systemAnimations: false, appOption: false),
            "both off");
    }

    public void TheLiveFlagsFollowTheSystemSetting()
    {
        // Not a claim about a fixed value - it documents the wiring: IsEnabled is the system's
        // setting on its own, and the window flag narrows it further with the app option.
        Assert.Equal(SystemParameters.ClientAreaAnimation, Motion.IsEnabled,
            "IsEnabled follows the system's animation setting");
        Assert.True(!Motion.WindowTransitionsEnabled || Motion.IsEnabled,
            "window transitions can never be on while motion is off");
    }

    /// <summary>
    /// v5.4.0: the top bar may only be hidden once the pointer has left it - otherwise the reveal
    /// poll and the hide fight each other and the bar pulses while the cursor rests on it.
    /// </summary>
    public void TheTopBarIsOnlyHiddenOnceThePointerHasLeftIt()
    {
        Assert.False(TopBarVisibility.ShouldHide(pluginAllowsAutoHide: true, barIsShown: true, pointerInTopZone: true),
            "a pointer on the bar keeps it up");
        Assert.True(TopBarVisibility.ShouldHide(true, true, pointerInTopZone: false),
            "and leaving the bar lets it go");
        Assert.False(TopBarVisibility.ShouldHide(pluginAllowsAutoHide: false, barIsShown: true, pointerInTopZone: false),
            "a plugin that wants the bar visible keeps it visible");
        Assert.False(TopBarVisibility.ShouldHide(true, barIsShown: false, pointerInTopZone: false),
            "an already hidden bar is not hidden again");
    }
}
