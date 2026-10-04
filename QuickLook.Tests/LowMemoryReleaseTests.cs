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
/// v5.6.3: the low memory mode releases its memory after a wait the user picks. These
/// tests keep the menu and the code that reads the setting in agreement - a choice the
/// menu offers has to be one the release actually honours.
/// </summary>
internal class LowMemoryReleaseTests
{
    public void TheDefaultIsNinetySeconds()
    {
        Assert.Equal(90, LowMemoryRelease.DefaultSeconds, "default wait");
        Assert.Equal(90, LowMemoryRelease.Options[0].Seconds, "and it is the first choice");
    }

    public void NeverIsOffered()
    {
        Assert.True(LowMemoryRelease.Options[^1].Seconds == 0, "the last choice is 'never'");
    }

    public void EveryChoiceIsDistinctAndInOrder()
    {
        var seconds = LowMemoryRelease.Options.Select(o => o.Seconds).ToArray();

        Assert.Equal(LowMemoryRelease.Options.Length, seconds.Distinct().Count(), "no duplicates");
        Assert.Equal(0, seconds[^1], "'never' comes last");

        // The waits grow: 90 s, 5 min, 15 min, 1 h - the order the menu shows them in.
        for (var i = 1; i < seconds.Length - 1; i++)
            Assert.True(seconds[i] > seconds[i - 1], $"choice {i} waits longer than the one before it");
    }

    public void EveryChoiceHasALabelAndATranslationKey()
    {
        foreach (var option in LowMemoryRelease.Options)
        {
            Assert.True(!string.IsNullOrWhiteSpace(option.Name), "a name");
            Assert.True(!string.IsNullOrWhiteSpace(option.Key), "a translation key");
        }
    }

    public void AValueThatIsNotOfferedFallsBackToTheDefault()
    {
        // The menu only writes listed values, but a hand-edited config can hold
        // anything; it must not leave the menu with no selection.
        Assert.Equal(LowMemoryRelease.Options[0].Seconds, LowMemoryRelease.OptionFor(42).Seconds,
            "an unknown value");
        Assert.Equal(300, LowMemoryRelease.OptionFor(300).Seconds, "a listed value");
        Assert.Equal(0, LowMemoryRelease.OptionFor(0).Seconds, "'never'");
    }
}
