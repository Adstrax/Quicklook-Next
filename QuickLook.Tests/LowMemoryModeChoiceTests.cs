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

using QuickLookNext.Helpers;
using System.Linq;

namespace QuickLook.Tests;

/// <summary>
/// v5.6.4: the tray menu offers the whole low memory mode as one choice - off, or on with
/// a wait before it releases its memory, or on and never releasing. These tests keep the
/// menu, the two settings behind it and the code that reads them in agreement.
/// </summary>
internal class LowMemoryModeChoiceTests
{
    public void TheFirstChoiceIsOff()
    {
        Assert.False(LowMemoryMode.Choices[0].Enabled, "the menu starts with the off state");
    }

    public void TheDefaultWaitIsNinetySeconds()
    {
        Assert.Equal(90, LowMemoryMode.DefaultSeconds, "default wait");

        var defaultWait = LowMemoryMode.ChoiceFor(enabled: true, LowMemoryMode.DefaultSeconds);
        Assert.True(defaultWait.Enabled, "the default is an enabled state");
        Assert.Equal(90, defaultWait.Seconds, "and it waits 90 seconds");
    }

    public void NeverIsOfferedAndComesLast()
    {
        var last = LowMemoryMode.Choices[^1];

        Assert.True(last.Enabled, "'never' is a mode state, not the off state");
        Assert.Equal(0, last.Seconds, "with no wait at all");
    }

    public void EveryWaitIsDistinctAndGrows()
    {
        var waits = LowMemoryMode.Choices.Where(c => c.Enabled).Select(c => c.Seconds).ToArray();

        Assert.Equal(waits.Length, waits.Distinct().Count(), "no duplicate waits");
        Assert.Equal(0, waits[^1], "'never' is the last of them");

        // 90 s, 5 min, 15 min, 1 h - the order the menu shows them in.
        for (var i = 1; i < waits.Length - 1; i++)
            Assert.True(waits[i] > waits[i - 1], $"wait {i} is longer than the one before it");
    }

    public void EveryChoiceHasALabelAndATranslationKey()
    {
        foreach (var choice in LowMemoryMode.Choices)
        {
            Assert.True(!string.IsNullOrWhiteSpace(choice.Name), "a name");
            Assert.True(!string.IsNullOrWhiteSpace(choice.Key), "a translation key");
        }
    }

    public void TheModeFlagDecidesWhichStateIsShown()
    {
        // The mode is off: the menu must not look like a wait is in effect.
        Assert.False(LowMemoryMode.ChoiceFor(enabled: false, seconds: 300).Enabled, "off");

        Assert.Equal(300, LowMemoryMode.ChoiceFor(enabled: true, seconds: 300).Seconds, "a listed wait");
        Assert.Equal(0, LowMemoryMode.ChoiceFor(enabled: true, seconds: 0).Seconds, "'never'");
    }

    public void AWaitThatIsNotOfferedFallsBackToTheDefault()
    {
        // The menu only writes listed waits, but a hand-edited config can hold anything;
        // it must not leave the menu with nothing ticked.
        Assert.Equal(LowMemoryMode.DefaultSeconds,
            LowMemoryMode.ChoiceFor(enabled: true, seconds: 42).Seconds, "an unknown wait");
    }
}
