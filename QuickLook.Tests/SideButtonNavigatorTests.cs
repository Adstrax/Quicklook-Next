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
/// v6.0.0: side-button presses must not race each other from the same previewed file.
/// </summary>
internal class SideButtonNavigatorTests
{
    public void ARapidBurstBecomesOneNetStep()
    {
        var navigator = new SideButtonNavigator();

        Assert.True(navigator.Enqueue(1, "a.jpg", out var start), "the first press starts the drain");
        Assert.True(start, "and asks for a worker");
        Assert.True(navigator.Enqueue(1, "a.jpg", out start), "the second press is accepted");
        Assert.False(start, "but joins the running drain");
        Assert.True(navigator.Enqueue(-1, "a.jpg", out start), "a press back is accepted too");

        Assert.True(navigator.TryTake(out var path, out var delta), "the burst is ready");
        Assert.Equal("a.jpg", path, "it starts from the preview that was open");
        Assert.Equal(1, delta, "two forward and one back is one net step forward");
        Assert.False(navigator.TryTake(out _, out _), "the burst is finished");
    }

    public void TheNextStepStartsWhereThePreviousOneLanded()
    {
        var navigator = new SideButtonNavigator();

        navigator.Enqueue(1, "a.jpg", out _);
        Assert.True(navigator.TryTake(out _, out var delta), "the first step is ready");
        Assert.Equal(1, delta, "one forward");
        navigator.Advance("b.jpg");

        navigator.Enqueue(1, "a.jpg", out var start);
        Assert.False(start, "the running drain takes the new press");
        Assert.True(navigator.TryTake(out var path, out delta), "the next step is ready");
        Assert.Equal("b.jpg", path, "from the item Explorer selected, not the old preview");
        Assert.Equal(1, delta, "one more forward");
    }

    public void ANetZeroBurstDoesNothingAndTheNextPressStartsAgain()
    {
        var navigator = new SideButtonNavigator();

        navigator.Enqueue(1, "a.jpg", out _);
        navigator.Enqueue(-1, "a.jpg", out _);
        Assert.False(navigator.TryTake(out _, out _), "nothing moved");

        Assert.True(navigator.Enqueue(1, "a.jpg", out var start), "the next press is accepted");
        Assert.True(start, "and starts a fresh drain");
    }

    public void AnEmptyAnchorIsIgnored()
    {
        var navigator = new SideButtonNavigator();

        Assert.False(navigator.Enqueue(1, string.Empty, out var start),
            "there is no preview to step from");
        Assert.False(start, "so no worker is started");
    }
}
