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
/// Low-memory decisions run in fixed windows of the user's chosen length. A window
/// records only whether it saw at least one preview; repeated previews do not re-arm the
/// timer or store another timestamp. When the window expires, activity keeps the process
/// warm for one more complete window, while an entirely inactive window releases it.
/// The next real preview starts a fresh warm session.
/// </para>
///
/// <para>
/// The class holds no clock and touches no UI: the host owns the timer and calls
/// <see cref="CompleteWindow"/> when a fixed window expires. This makes the rules
/// testable without waiting or creating a window.
/// </para>
/// </summary>
internal sealed class PreviewSession
{
    private bool _windowRunning;
    private bool _previewSeenInWindow;

    /// <summary>
    /// Raised when a new fixed window begins or the session ends. The host uses this
    /// as the single point that starts the release timer. Previews inside a running
    /// window deliberately do not raise it, because they must not move the deadline.
    /// </summary>
    internal event EventHandler ActivityChanged;

    /// <summary>True while this process owns a warm preview session.</summary>
    internal bool IsActive { get; private set; }

    /// <summary>Whether any preview was opened during the current fixed window.</summary>
    internal bool HasPreviewInWindow => _previewSeenInWindow;

    /// <summary>Starts a warm session at launch, without waiting for the first preview.</summary>
    internal void Started()
    {
        IsActive = true;
        _windowRunning = true;
        _previewSeenInWindow = false;
        NotifyActivityChanged();
    }

    /// <summary>
    /// Records that a preview is about to be shown. Only the first preview after a
    /// stopped window starts a new deadline; further previews only set the boolean.
    /// </summary>
    internal void Opened()
    {
        var startsWindow = !IsActive || !_windowRunning;
        IsActive = true;
        _windowRunning = true;
        _previewSeenInWindow = true;
        if (startsWindow)
            NotifyActivityChanged();
    }

    /// <summary>Should a freshly created preview window be warmed off-screen?</summary>
    internal bool ShouldWarm(bool lowMemoryMode) => !lowMemoryMode || IsActive;

    internal enum WindowResult
    {
        Inactive,
        KeepWarm,
        Release
    }

    /// <summary>
    /// Completes the current fixed window. Any preview during it keeps the process
    /// warm for a full new window. An open preview also counts as activity and can
    /// never be closed by an idle release.
    /// </summary>
    internal WindowResult CompleteWindow(bool lowMemoryMode, bool previewOpen)
    {
        if (!lowMemoryMode || !IsActive)
        {
            _windowRunning = false;
            _previewSeenInWindow = false;
            return WindowResult.Inactive;
        }

        if (previewOpen)
            _previewSeenInWindow = true;

        if (_previewSeenInWindow)
        {
            _previewSeenInWindow = false;
            return WindowResult.KeepWarm;
        }

        _windowRunning = false;
        return WindowResult.Release;
    }

    /// <summary>
    /// A due release can be blocked by a panel or dialog. Keep the session alive
    /// and retry later. A preview opening at that point starts a new full window.
    /// </summary>
    internal bool ShouldRetryBlockedRelease(bool lowMemoryMode, bool previewOpen)
        => lowMemoryMode && IsActive && !_windowRunning && !previewOpen;

    /// <summary>Ends the session, so the next window is built cold.</summary>
    internal void End()
    {
        IsActive = false;
        _windowRunning = false;
        _previewSeenInWindow = false;
        NotifyActivityChanged();
    }

    private void NotifyActivityChanged() =>
        ActivityChanged?.Invoke(this, EventArgs.Empty);
}
