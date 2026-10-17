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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.

using QuickLookNext.Helpers;

namespace QuickLook.Tests;

/// <summary>
/// v6.0.0: a quick arrow-key burst gets one settle window, while an isolated switch stays immediate.
/// </summary>
internal class PreviewSwitchCadenceTests
{
    public void TheFirstSwitchIsImmediate()
    {
        var cadence = new PreviewSwitchCadence();

        Assert.Equal(0, cadence.GetDelayMs(10_000), "opening should not wait");
    }

    public void ARequestInsideTheBurstWaitsForTheRestOfTheWindow()
    {
        var cadence = new PreviewSwitchCadence();
        cadence.GetDelayMs(10_000);

        Assert.Equal(35, cadence.GetDelayMs(10_025),
            "a request 25 ms later waits only the remaining 35 ms");
    }

    public void ARequestAfterAPauseIsImmediateAgain()
    {
        var cadence = new PreviewSwitchCadence();
        cadence.GetDelayMs(10_000);

        Assert.Equal(0, cadence.GetDelayMs(10_060),
            "the end of the burst is not delayed");
    }

    public void TheWindowExtendsWithEachRapidRequest()
    {
        var cadence = new PreviewSwitchCadence();
        cadence.GetDelayMs(10_000);
        cadence.GetDelayMs(10_030);

        Assert.Equal(30, cadence.GetDelayMs(10_060),
            "the latest request still gets a short settling time");
    }

    public void ClosingThePreviewStartsAFreshNavigation()
    {
        var cadence = new PreviewSwitchCadence();
        cadence.GetDelayMs(10_000);
        cadence.GetDelayMs(10_020);

        cadence.Reset();

        Assert.Equal(0, cadence.GetDelayMs(10_021),
            "the next preview opens immediately");
    }
}
