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
using QuickLookNext.Helpers;
using System;

namespace QuickLook.Tests;

/// <summary>
/// v5.2.0: the low memory mode is the one switch that decides whether the two startup warm-ups
/// (the off-screen preview window and the per-family preparation) run. Both ask the same helper,
/// so they cannot drift apart - and an idle tray process drops from ~169 MB to ~61 MB private.
/// </summary>
internal class MemoryModeTests : SettingsFixture
{
    public void BothWarmUpsRunByDefault()
    {
        Assert.False(StartupWarmUp.IsLowMemoryMode, "the mode is off out of the box");
        Assert.True(StartupWarmUp.IsEnabled, "so the warm-ups run");
    }

    public void TheModeTurnsTheWarmUpsOffAndBackOn()
    {
        StartupWarmUp.Toggle();

        Assert.True(StartupWarmUp.IsLowMemoryMode, "the mode is on");
        Assert.False(StartupWarmUp.IsEnabled, "which means no warm-up at start");

        StartupWarmUp.Toggle();

        Assert.False(StartupWarmUp.IsLowMemoryMode, "the mode is off again");
        Assert.True(StartupWarmUp.IsEnabled, "and the warm-ups are back");
    }

    public void TheChoiceIsPersistedInTheSettingsStore()
    {
        StartupWarmUp.Toggle();

        Assert.True(SettingHelper.Get("LowMemoryMode", false, "QuickLookNext"),
            "stored in the app's own settings domain, so it survives a restart");
    }

    /// <summary>
    /// v5.2.0: the WebView2 host drops its parked controls when the display scaling changes - a
    /// control created for the old scale must never be handed to the next preview (#1956). The
    /// signal has to fire on a real change only, or every preview would throw its warm control away.
    /// </summary>
    public void AScaleChangeIsAnnouncedOnlyWhenItReallyChanges()
    {
        var announced = 0;
        Action handler = () => announced++;

        DisplayScale.Changed += handler;
        try
        {
            var before = announced;
            var scale = DisplayScale.Current + 0.123d;

            DisplayScale.Notify(scale);
            Assert.Equal(before + 1, announced, "a new scale is announced");

            DisplayScale.Notify(scale);
            Assert.Equal(before + 1, announced, "the same scale again is not a change");

            DisplayScale.Notify(scale + 0.5d);
            Assert.Equal(before + 2, announced, "and the next real change is");

            DisplayScale.Notify(0d);
            Assert.Equal(before + 2, announced, "a nonsense scale is ignored");
        }
        finally
        {
            DisplayScale.Changed -= handler;
        }
    }
}
