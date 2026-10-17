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

using QuickLook.Common.Plugin;
using QuickLookNext.Helpers;

namespace QuickLook.Tests;

public sealed class PreviewQuiesceGateTests
{
    public void APreviewIsQuiescedOnlyOnce()
    {
        var gate = new PreviewQuiesceGate();
        var preview = new QuiescablePreview();

        Assert.True(gate.TryQuiesce(preview), "the first request should stop the old preview");
        Assert.False(gate.TryQuiesce(preview), "the second lifecycle path should be a no-op");
        Assert.Equal(1, preview.QuiesceCount, "the old preview must not be touched twice");
    }

    public void APluginWithoutTheCapabilityIsIgnored()
    {
        var gate = new PreviewQuiesceGate();

        Assert.False(gate.TryQuiesce(new object()), "ordinary plugins keep the old lifecycle");
    }

    public void ANextPreviewCanBeQuiescedAfterReset()
    {
        var gate = new PreviewQuiesceGate();
        var first = new QuiescablePreview();
        var second = new QuiescablePreview();

        gate.TryQuiesce(first);
        gate.Reset();

        Assert.True(gate.TryQuiesce(second), "a new preview needs its own quiesce call");
        Assert.Equal(1, second.QuiesceCount, "the new preview should stop exactly once");
    }

    private sealed class QuiescablePreview : IPreviewQuiesce
    {
        public int QuiesceCount { get; private set; }

        public void Quiesce()
        {
            QuiesceCount++;
        }
    }
}
