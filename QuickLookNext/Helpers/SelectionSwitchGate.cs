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
using System.Threading;

namespace QuickLookNext.Helpers;

/// <summary>
/// v6.0.0: decides whether a selection read may request a preview switch.
/// <para>
/// The shell worker answers selection reads asynchronously. An older read finishing after a newer
/// one must not drag the preview backwards, and a side-button target already being switched to must
/// not be requested a second time by the selection monitor.
/// </para>
/// </summary>
internal sealed class SelectionSwitchGate
{
    private readonly object _sync = new();
    private int _readVersion;
    private string _lastPath = string.Empty;
    private string _externalPath = string.Empty;
    private long _externalUntil;

    /// <summary>Starts from the selection that is already present, which is not a switch.</summary>
    internal void Reset(string path)
    {
        lock (_sync)
        {
            Interlocked.Increment(ref _readVersion);
            _lastPath = path ?? string.Empty;
            _externalPath = string.Empty;
            _externalUntil = 0;
        }
    }

    /// <summary>Starts a selection read; only the newest started read may dispatch.</summary>
    internal int BeginRead() => Interlocked.Increment(ref _readVersion);

    /// <summary>Makes every read started before a stop return without a switch.</summary>
    internal void InvalidateReads() => Interlocked.Increment(ref _readVersion);

    /// <summary>
    /// Records a target the mouse is switching to directly. Until the direct switch has had time
    /// to reach the UI thread, the selection event for the same target is only an echo.
    /// </summary>
    internal void MarkExternalSwitch(string path, long nowMs, long holdMs = 1_500)
    {
        if (string.IsNullOrEmpty(path))
            return;

        lock (_sync)
        {
            Interlocked.Increment(ref _readVersion);
            _lastPath = path;
            _externalPath = path;
            _externalUntil = nowMs + Math.Max(0, holdMs);
        }
    }

    /// <summary>
    /// Whether this read is current, is not the selection already on screen, and is not the echo
    /// of a switch the mouse has already requested.
    /// </summary>
    internal bool ShouldDispatch(int readVersion, string path, string previewedPath, long nowMs)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        lock (_sync)
        {
            if (readVersion != Volatile.Read(ref _readVersion) || path == _lastPath)
                return false;

            _lastPath = path;

            if (path == previewedPath)
                return false;

            return path != _externalPath || nowMs > _externalUntil;
        }
    }
}
