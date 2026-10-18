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

namespace QuickLook.Common.Helpers;

/// <summary>
/// Decides when the still thumbnail of a video may be removed.
///
/// <para>
/// The playback graph reports <c>MediaOpened</c> as soon as it can play, not
/// when its renderer has painted something. Removing the thumbnail at that
/// moment exposes an empty surface - the "thumbnail, a short black flash, then
/// the video" sequence users see. The first rendered frame is the earliest
/// reliable evidence that the surface has content, so the thumbnail is kept
/// until then. A bounded timeout covers renderers that never report a frame
/// (for example a graph that opens paused).
/// </para>
///
/// <para>
/// The frame notification arrives on the media player's own thread while the
/// reveal itself happens on the UI thread, so every transition is serialised
/// here instead of relying on field ordering at the call site.
/// </para>
/// </summary>
public sealed class VideoRevealGate
{
    /// <summary>
    /// How long the thumbnail may outlive <c>MediaOpened</c> when no frame is
    /// reported. Long enough for a slow first frame, short enough that a broken
    /// renderer cannot leave the thumbnail up for the whole preview.
    /// </summary>
    public const int DefaultFrameTimeoutMilliseconds = 800;

    private readonly object _sync = new();
    private readonly int _frameTimeoutMilliseconds;
    private bool _mediaOpened;
    private bool _hasVideo;
    private bool _frameSeen;
    private bool _revealed;
    private long _openedAtMilliseconds;

    public VideoRevealGate(int frameTimeoutMilliseconds = DefaultFrameTimeoutMilliseconds)
    {
        if (frameTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameTimeoutMilliseconds));

        _frameTimeoutMilliseconds = frameTimeoutMilliseconds;
    }

    /// <summary>True once the thumbnail has been released for this media.</summary>
    public bool IsRevealed
    {
        get
        {
            lock (_sync)
                return _revealed;
        }
    }

    /// <summary>The graph is open and the renderer has not shown a frame yet.</summary>
    public bool IsWaitingForFrame
    {
        get
        {
            lock (_sync)
                return _mediaOpened && _hasVideo && !_frameSeen && !_revealed;
        }
    }

    /// <summary>Returns to the pre-open state for the next file.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _mediaOpened = false;
            _hasVideo = false;
            _frameSeen = false;
            _revealed = false;
            _openedAtMilliseconds = 0;
        }
    }

    /// <summary>
    /// Records that the playback graph opened.
    /// </summary>
    /// <returns>
    /// True when no video frame can arrive (audio-only media), so the caller may
    /// reveal immediately instead of waiting for the timeout.
    /// </returns>
    public bool NotifyMediaOpened(bool hasVideo, long nowMilliseconds)
    {
        lock (_sync)
        {
            _mediaOpened = true;
            // A frame that arrived before the routed event was delivered is
            // still this graph's first frame. Counting it avoids keeping the
            // thumbnail up for the whole timeout on fast renderers.
            _hasVideo = hasVideo || _frameSeen;
            _openedAtMilliseconds = nowMilliseconds;
            return !_hasVideo || _frameSeen;
        }
    }

    /// <summary>
    /// Records a rendered frame.
    /// </summary>
    /// <returns>
    /// True only when this is the first frame of the current media and the
    /// surface is already allowed to be revealed. A frame that beats the
    /// routed <c>MediaOpened</c> callback is remembered, and
    /// <see cref="NotifyMediaOpened"/> then reveals straight away.
    /// </returns>
    public bool NotifyFrame()
    {
        lock (_sync)
        {
            if (_frameSeen || _revealed)
                return false;

            _frameSeen = true;
            _hasVideo = true;
            return _mediaOpened;
        }
    }

    /// <summary>
    /// True when the frame wait has run out. Only meaningful while the gate is
    /// still waiting, so a late timer tick cannot reopen a finished reveal.
    /// </summary>
    public bool IsFrameTimeoutElapsed(long nowMilliseconds)
    {
        lock (_sync)
        {
            return _mediaOpened &&
                   _hasVideo &&
                   !_frameSeen &&
                   !_revealed &&
                   nowMilliseconds - _openedAtMilliseconds >= _frameTimeoutMilliseconds;
        }
    }

    /// <summary>
    /// Claims the single reveal for the current media. Concurrent frame
    /// notifications, the timeout, and a failure path all race here; only the
    /// first caller gets to touch the visual tree.
    /// </summary>
    public bool TryTakeReveal()
    {
        lock (_sync)
        {
            if (_revealed)
                return false;

            _revealed = true;
            return true;
        }
    }
}
