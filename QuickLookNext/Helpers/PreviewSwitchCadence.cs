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

using System;

namespace QuickLookNext.Helpers;

/// <summary>
/// v6.0.0: gives a burst of selection changes one short settle window before the next preview
/// starts its plugin match and Prepare.
/// <para>
/// The first switch after a pause stays immediate. Later switches within
/// <see cref="BurstWindowMs"/> are delayed only until that window has passed; the preview request
/// version then discards every request except the newest. This prevents holding or tapping an
/// arrow key quickly from decoding every file the selection passed through.
/// </para>
/// </summary>
internal sealed class PreviewSwitchCadence
{
    internal const int BurstWindowMs = 60;

    private long _lastRequestMs = long.MinValue;

    /// <summary>
    /// Records this selection request and returns how long to wait before loading it. A switch
    /// after a pause returns zero, so normal navigation does not pay the burst delay.
    /// </summary>
    internal int GetDelayMs(long nowMs)
    {
        var previous = _lastRequestMs;
        _lastRequestMs = nowMs;

        if (previous == long.MinValue)
            return 0;

        var elapsed = Math.Max(0, nowMs - previous);
        return elapsed >= BurstWindowMs ? 0 : BurstWindowMs - (int)elapsed;
    }

    /// <summary>The next preview starts a new navigation session without a settle delay.</summary>
    internal void Reset() => _lastRequestMs = long.MinValue;
}
