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
/// v6.0.0: only the newest preview may post its plugin View call.
/// </summary>
internal class PreviewShowGateTests
{
    public void AnOlderShowCannotRenderAfterANewerOne()
    {
        var gate = new PreviewShowGate();

        var older = gate.Begin();
        var newer = gate.Begin();

        Assert.True(gate.IsCurrent(newer), "the newest show wins");
        Assert.False(gate.IsCurrent(older), "the queued older View must be dropped");
    }

    public void InvalidatingAClosedPreviewRejectsEveryQueuedShow()
    {
        var gate = new PreviewShowGate();
        var show = gate.Begin();

        gate.Invalidate();

        Assert.False(gate.IsCurrent(show), "a recycled window must not run the old View");
    }

    public void TheNextShowAfterAnInvalidationIsCurrent()
    {
        var gate = new PreviewShowGate();
        var oldShow = gate.Begin();
        gate.Invalidate();

        var newShow = gate.Begin();

        Assert.True(gate.IsCurrent(newShow), "the new preview can render");
        Assert.False(gate.IsCurrent(oldShow), "and the invalidated one stays retired");
    }
}
