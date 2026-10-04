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
/// v5.6.2: when the preview window should be warmed off-screen, and when that warm
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
/// The warm-up follows the session now. Nothing is warmed until the user has
/// actually previewed something (the idle footprint of the low memory mode is
/// unchanged), every window rebuilt while they keep previewing is warmed as usual,
/// and once they have stopped for <see cref="IdleTimeout"/> the parked window is
/// released so the memory goes back to the idle baseline.
/// </para>
///
/// <para>
/// The class holds no clock and touches no UI: the caller passes the time in, which
/// is what makes the rules testable without a window.
/// </para>
/// </summary>
internal sealed class PreviewSession
{
    /// <summary>
    /// How long the warm window is kept after the last preview. Long enough that
    /// flicking through a folder never hits a cold start, short enough that "I am
    /// done looking at files" is followed by the memory coming back.
    /// </summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

    private long _lastActivityMs;

    /// <summary>True once the user has previewed something and has not gone idle since.</summary>
    internal bool IsActive { get; private set; }

    /// <summary>A preview is about to be shown.</summary>
    internal void Opened(long nowMs)
    {
        IsActive = true;
        _lastActivityMs = nowMs;
    }

    /// <summary>The preview window closed - the user closed it, or the plugin failed.</summary>
    internal void Closed(long nowMs)
    {
        if (IsActive)
            _lastActivityMs = nowMs;
    }

    /// <summary>Should a freshly created preview window be warmed off-screen?</summary>
    internal bool ShouldWarm(bool lowMemoryMode) => !lowMemoryMode || IsActive;

    /// <summary>
    /// Is the warm window no longer worth its memory? Only ever true in the low
    /// memory mode, only inside a session, never while a preview is open (closing the
    /// window would close that preview), and only after the idle timeout.
    /// </summary>
    internal bool ShouldRelease(long nowMs, bool lowMemoryMode, bool previewOpen)
    {
        if (!lowMemoryMode || !IsActive || previewOpen)
            return false;

        return nowMs - _lastActivityMs >= (long)IdleTimeout.TotalMilliseconds;
    }

    /// <summary>Ends the session, so the next window is built cold.</summary>
    internal void End()
    {
        IsActive = false;
        _lastActivityMs = 0;
    }
}
