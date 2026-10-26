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
/// v6.5.0: low-memory timing and startup preparation are separate decisions. Every normal launch
/// warms the preview stack; only the deliberate post-release cold-idle process skips it.
/// </summary>
internal class MemoryModeTests : SettingsFixture
{
    public override void Setup()
    {
        base.Setup();
        StartupWarmUp.SetColdIdle(false);
    }

    public override void Teardown()
    {
        StartupWarmUp.SetColdIdle(false);
        base.Teardown();
    }

    public void BothWarmUpsRunByDefault()
    {
        Assert.True(StartupWarmUp.IsLowMemoryMode, "timed idle release is on out of the box");
        Assert.True(StartupWarmUp.IsEnabled, "and the startup warm-ups run");
    }

    public void TheModeDoesNotChangeStartupWarmUp()
    {
        StartupWarmUp.Toggle();

        Assert.False(StartupWarmUp.IsLowMemoryMode, "the release policy is off");
        Assert.True(StartupWarmUp.IsEnabled, "but startup still warms");

        StartupWarmUp.Toggle();

        Assert.True(StartupWarmUp.IsLowMemoryMode, "the timed release is on again");
        Assert.True(StartupWarmUp.IsEnabled, "and startup still warms");
    }

    public void TheChoiceIsPersistedInTheSettingsStore()
    {
        StartupWarmUp.Set(true);

        Assert.True(SettingHelper.Get("LowMemoryMode", false, "QuickLookNext"),
            "stored in the app's own settings domain, so it survives a restart");
    }

    public void OnlyThePostReleaseProcessIsCold()
    {
        StartupWarmUp.SetColdIdle(true);
        Assert.False(StartupWarmUp.IsEnabled, "the release restart has no warm-up and no timer to repeat it");

        StartupWarmUp.SetColdIdle(false);
        Assert.True(StartupWarmUp.IsEnabled, "the next real preview returns to a warm session");
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
