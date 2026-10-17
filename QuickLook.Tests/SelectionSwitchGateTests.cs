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
/// v6.0.0: only the newest selection read may move the preview, and the selection monitor must
/// not repeat a switch the mouse already requested.
/// </summary>
internal class SelectionSwitchGateTests
{
    public void AnOlderReadCannotReplaceANewerSelection()
    {
        var gate = new SelectionSwitchGate();
        gate.Reset("a.jpg");

        var older = gate.BeginRead();
        var newer = gate.BeginRead();

        Assert.True(gate.ShouldDispatch(newer, "b.jpg", "a.jpg", 1_000), "the newer read wins");
        Assert.False(gate.ShouldDispatch(older, "c.jpg", "b.jpg", 1_001),
            "a read that started earlier cannot drag the preview back");
    }

    public void ThePathAlreadyOnScreenIsNotRequestedAgain()
    {
        var gate = new SelectionSwitchGate();
        gate.Reset("a.jpg");

        Assert.False(gate.ShouldDispatch(gate.BeginRead(), "a.jpg", "a.jpg", 1_000),
            "the current selection is not a switch");
    }

    public void ASideButtonTargetIsNotRepeatedByTheSelectionMonitor()
    {
        var gate = new SelectionSwitchGate();
        gate.Reset("a.jpg");
        gate.MarkExternalSwitch("b.jpg", 1_000);

        Assert.False(gate.ShouldDispatch(gate.BeginRead(), "b.jpg", "a.jpg", 1_001),
            "the direct side-button switch is still on its way to the UI thread");
    }

    public void AnOlderReadIsInvalidatedByTheSideButtonSwitch()
    {
        var gate = new SelectionSwitchGate();
        gate.Reset("a.jpg");
        var readBeforeThePress = gate.BeginRead();

        gate.MarkExternalSwitch("b.jpg", 1_000);

        Assert.False(gate.ShouldDispatch(readBeforeThePress, "a.jpg", "b.jpg", 1_001),
            "a read of the old selection must not undo the side-button move");
    }

    public void ALaterSelectionIsStillFollowed()
    {
        var gate = new SelectionSwitchGate();
        gate.Reset("a.jpg");
        gate.MarkExternalSwitch("b.jpg", 1_000, holdMs: 100);

        Assert.True(gate.ShouldDispatch(gate.BeginRead(), "c.jpg", "b.jpg", 1_101),
            "a real selection change after the echo window is dispatched");
    }

    public void ARapidArrowBurstDispatchesOnlyItsFinalSelection()
    {
        var gate = new SelectionSwitchGate();
        gate.Reset("a.jpg");

        var first = gate.BeginRead();
        var second = gate.BeginRead();
        var third = gate.BeginRead();

        Assert.True(gate.ShouldDispatch(third, "d.jpg", "a.jpg", 1_000),
            "the final selection is shown");
        Assert.False(gate.ShouldDispatch(second, "c.jpg", "d.jpg", 1_001),
            "the middle read is stale");
        Assert.False(gate.ShouldDispatch(first, "b.jpg", "d.jpg", 1_002),
            "the first read is stale too");
    }

    public void ClosingThePreviewRetiresEveryPendingRead()
    {
        var gate = new SelectionSwitchGate();
        gate.Reset("a.jpg");
        var pending = gate.BeginRead();

        gate.InvalidateReads();

        Assert.False(gate.ShouldDispatch(pending, "b.jpg", "a.jpg", 1_000),
            "a selection read cannot reopen or move a closed preview");
    }
}
