// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Common.Helpers;

namespace QuickLook.Tests;

/// <summary>
/// v6.1.2: a video preview used to remove its thumbnail a fixed 250 ms after
/// MediaOpened, which exposed the renderer's empty surface whenever the first
/// frame took longer - the "thumbnail, black flash, video" sequence. The gate
/// keeps the thumbnail until the renderer reports a frame, with a timeout for
/// graphs that never report one.
/// </summary>
internal class VideoRevealGateTests
{
    public void MediaOpenedAloneDoesNotRevealAVideo()
    {
        var gate = new VideoRevealGate();

        Assert.False(gate.NotifyMediaOpened(hasVideo: true, 1_000),
            "an open graph is not proof that a frame has been painted");
        Assert.True(gate.IsWaitingForFrame, "the thumbnail has to stay up");
        Assert.False(gate.IsRevealed, "nothing may be revealed yet");
    }

    public void TheFirstFrameAllowsExactlyOneReveal()
    {
        var gate = new VideoRevealGate();
        gate.NotifyMediaOpened(hasVideo: true, 1_000);

        Assert.True(gate.NotifyFrame(), "the first frame is the reveal signal");
        Assert.False(gate.NotifyFrame(), "later frames must not repeat the reveal");
        Assert.True(gate.TryTakeReveal(), "the reveal is still available");
        Assert.False(gate.TryTakeReveal(), "two code paths cannot both remove the thumbnail");
    }

    public void AudioRevealsWithoutWaitingForAFrame()
    {
        var gate = new VideoRevealGate();

        Assert.True(gate.NotifyMediaOpened(hasVideo: false, 1_000),
            "audio-only media never reports a frame");
        Assert.False(gate.IsWaitingForFrame, "there is nothing to wait for");
    }

    public void AFrameBeforeTheOpenedCallbackIsNotLost()
    {
        var gate = new VideoRevealGate();

        Assert.False(gate.NotifyFrame(),
            "the surface cannot be revealed before the graph is open");

        Assert.True(gate.NotifyMediaOpened(hasVideo: true, 1_000),
            "a frame that beat the routed callback already painted the surface");
        Assert.False(gate.IsWaitingForFrame, "there is nothing left to wait for");
        Assert.False(gate.NotifyFrame(), "the same frame cannot reveal twice");
    }

    public void TheTimeoutOnlyAppliesWhileTheFrameIsMissing()
    {
        var gate = new VideoRevealGate(frameTimeoutMilliseconds: 500);
        gate.NotifyMediaOpened(hasVideo: true, 1_000);

        Assert.False(gate.IsFrameTimeoutElapsed(1_499), "the frame still has time");
        Assert.True(gate.IsFrameTimeoutElapsed(1_500), "the fallback may take over");

        gate.NotifyFrame();
        Assert.False(gate.IsFrameTimeoutElapsed(9_000), "a painted frame ends the wait");
    }

    public void AFinishedRevealCannotBeReopenedByALateTimeout()
    {
        var gate = new VideoRevealGate(frameTimeoutMilliseconds: 100);
        gate.NotifyMediaOpened(hasVideo: true, 1_000);
        Assert.True(gate.TryTakeReveal(), "the timeout claims the reveal");

        Assert.False(gate.IsFrameTimeoutElapsed(5_000), "the wait is already over");
        Assert.False(gate.NotifyFrame(), "a slow frame is no longer a reveal signal");
    }

    public void ResetPreparesTheGateForTheNextFile()
    {
        var gate = new VideoRevealGate();
        gate.NotifyMediaOpened(hasVideo: true, 1_000);
        gate.NotifyFrame();
        gate.TryTakeReveal();

        gate.Reset();

        Assert.False(gate.IsRevealed, "the next video starts covered");
        Assert.False(gate.NotifyMediaOpened(hasVideo: true, 2_000),
            "the next video must wait for its own first frame");
        Assert.True(gate.IsWaitingForFrame, "the thumbnail is covering the new surface");
    }
}
