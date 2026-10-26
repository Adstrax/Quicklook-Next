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

using System;

namespace QuickLookNext.Helpers;

/// <summary>
/// v6.5.0: when the preview window should be warmed off-screen, and when that warm
/// window should be handed back.
///
/// <para>
/// The preview window is destroyed and rebuilt on every close, and the first
/// <c>Show()</c> of a window costs about 200 ms (HWND creation, layout, the DWM/WCA
/// backdrop, the WPF render stack). The startup warm-up exists to pay that while
/// nobody is waiting - and because the rebuild happens on every close, the normal
/// mode pays it again after every preview. The low memory mode used to skip the
/// warm-up outright, which is what made <em>every</em> preview pay it: measured on
/// one machine, 325 ms per preview instead of 120 ms, on every preview after the
/// first, with no sign of settling down.
/// </para>
///
/// <para>
/// A normal launch starts the session immediately, including the startup warm-up.
/// The user's wait starts at that moment and is restarted whenever a preview opens or
/// closes. When it expires the process restarts cold; the next real preview starts a new
/// warm session. A session can therefore be released even if the user never previewed a
/// file after launch.
/// </para>
///
/// <para>
/// The class holds no clock and touches no UI: the caller passes the time in, which
/// is what makes the rules testable without a window.
/// </para>
/// </summary>
internal sealed class PreviewSession
{
    private long _lastActivityMs;

    /// <summary>
    /// Raised whenever opening, closing or ending a session changes the instant
    /// from which the idle wait is measured. The host uses this as the single
    /// rearm point for its release timer, so closing a preview cannot leave an
    /// already-fired timer stopped.
    /// </summary>
    internal event EventHandler ActivityChanged;

    /// <summary>True while this process owns a warm preview session.</summary>
    internal bool IsActive { get; private set; }

    /// <summary>Starts a warm session at launch, without waiting for the first preview.</summary>
    internal void Started(long nowMs)
    {
        IsActive = true;
        _lastActivityMs = nowMs;
        NotifyActivityChanged();
    }

    /// <summary>A preview is about to be shown.</summary>
    internal void Opened(long nowMs)
    {
        IsActive = true;
        _lastActivityMs = nowMs;
        NotifyActivityChanged();
    }

    /// <summary>The preview window closed - the user closed it, or the plugin failed.</summary>
    internal void Closed(long nowMs)
    {
        if (IsActive)
        {
            _lastActivityMs = nowMs;
            NotifyActivityChanged();
        }
    }

    /// <summary>Should a freshly created preview window be warmed off-screen?</summary>
    internal bool ShouldWarm(bool lowMemoryMode) => !lowMemoryMode || IsActive;

    /// <summary>
    /// Is the warm window no longer worth its memory? Only ever true in the low
    /// memory mode, only inside a session, never while a preview is open (closing the
    /// window would close that preview), and only after the idle timeout. A startup
    /// session counts even when no preview has been opened.
    /// </summary>
    internal bool ShouldRelease(long nowMs, bool lowMemoryMode, bool previewOpen, long timeoutMs)
    {
        if (!lowMemoryMode || !IsActive || previewOpen)
            return false;

        return nowMs - _lastActivityMs >= timeoutMs;
    }

    /// <summary>Time since the last session activity, for low-memory diagnostics.</summary>
    internal long IdleFor(long nowMs) =>
        IsActive ? Math.Max(0, nowMs - _lastActivityMs) : 0;

    /// <summary>Ends the session, so the next window is built cold.</summary>
    internal void End()
    {
        IsActive = false;
        _lastActivityMs = 0;
        NotifyActivityChanged();
    }

    private void NotifyActivityChanged() =>
        ActivityChanged?.Invoke(this, EventArgs.Empty);
}
