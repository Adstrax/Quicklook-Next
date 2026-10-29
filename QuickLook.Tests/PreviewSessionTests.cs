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

using QuickLookNext.Helpers;

namespace QuickLook.Tests;

/// <summary>
/// v6.5.0: a normal launch owns a warm session immediately, and the configured idle
/// wait can release it even before the first preview. The deliberate cold-idle process
/// warms again only after a real preview.
/// </summary>
internal class PreviewSessionTests
{
    private const long TimeoutMs = 300000;

    public void TheColdIdleProcessHasNothingToWarm()
    {
        var session = new PreviewSession();

        Assert.False(session.ShouldWarm(lowMemoryMode: true), "the release restart stays cold");
    }

    public void ANormalLaunchStartsAWarmSessionImmediately()
    {
        var session = new PreviewSession();
        session.Started(0);

        Assert.True(session.IsActive, "the launch owns a session");
        Assert.True(session.ShouldWarm(lowMemoryMode: true), "and prepares the parked window");
    }

    public void TheNormalModeKeepsItsWindowWarmAllAlong()
    {
        var session = new PreviewSession();

        Assert.True(session.ShouldWarm(lowMemoryMode: false), "before the first preview");
        session.Opened(0);
        Assert.True(session.ShouldWarm(lowMemoryMode: false), "and after it");
    }

    public void OnceTheUserPreviewsTheRebuiltWindowIsWarmed()
    {
        var session = new PreviewSession();
        session.Opened(0);

        Assert.True(session.ShouldWarm(lowMemoryMode: true), "while the first preview is open");

        // The window is rebuilt on close - that rebuild is what used to be cold.
        session.Closed(1_000);
        Assert.True(session.ShouldWarm(lowMemoryMode: true), "after closing it");
    }

    public void TheParkedWindowIsReleasedExactlyAtTheIdleTimeout()
    {
        var session = new PreviewSession();
        session.Opened(0);
        session.Closed(1_000);

        Assert.False(session.ShouldRelease(1_000 + TimeoutMs - 1, true, previewOpen: false, TimeoutMs),
            "a moment before the timeout");
        Assert.True(session.ShouldRelease(1_000 + TimeoutMs, true, previewOpen: false, TimeoutMs),
            "at the timeout");
    }

    public void EverySessionActivityRearmsTheIdleDeadline()
    {
        var session = new PreviewSession();
        var changes = 0;
        session.ActivityChanged += (_, _) => changes++;

        session.Opened(0);
        Assert.Equal(1, changes, "opening starts the countdown");

        // The original timer can already have fired while the preview stayed
        // open. Closing must notify the host so it schedules a fresh wait from
        // the instant the preview went away.
        session.Closed(TimeoutMs * 10);
        Assert.Equal(2, changes, "closing restarts the countdown");
        Assert.False(
            session.ShouldRelease(
                TimeoutMs * 11 - 1, lowMemoryMode: true, previewOpen: false, TimeoutMs),
            "the fresh wait is measured from the close");
        Assert.True(
            session.ShouldRelease(
                TimeoutMs * 11, lowMemoryMode: true, previewOpen: false, TimeoutMs),
            "and releases when that fresh wait expires");

        session.End();
        Assert.Equal(3, changes, "ending the session retires its timer");
    }

    public void AnOpenPreviewIsNeverReleased()
    {
        var session = new PreviewSession();
        session.Opened(0);

        // Closing the window would close the preview the user is reading.
        Assert.False(session.ShouldRelease(TimeoutMs * 10, true, previewOpen: true, TimeoutMs),
            "a preview is open");
    }

    public void ABlockedReleaseStaysPendingWhileTheSessionIsActive()
    {
        var session = new PreviewSession();
        session.Started(0);

        Assert.True(
            session.ShouldRetryBlockedRelease(lowMemoryMode: true, previewOpen: false),
            "another window may close and allow the release later");
        Assert.False(
            session.ShouldRetryBlockedRelease(lowMemoryMode: true, previewOpen: true),
            "a newly opened preview owns the next activity deadline");
        Assert.False(
            session.ShouldRetryBlockedRelease(lowMemoryMode: false, previewOpen: false),
            "turning low memory mode off cancels the pending release");

        session.End();
        Assert.False(
            session.ShouldRetryBlockedRelease(lowMemoryMode: true, previewOpen: false),
            "an ended session has nothing left to release");
    }

    public void TheNormalModeNeverReleasesItsWindow()
    {
        var session = new PreviewSession();
        session.Opened(0);
        session.Closed(0);

        Assert.False(session.ShouldRelease(TimeoutMs * 10, lowMemoryMode: false, previewOpen: false, TimeoutMs),
            "normal mode");
    }

    public void IdleDurationTracksTheLastActivity()
    {
        var session = new PreviewSession();
        session.Started(1_000);

        Assert.Equal(4_000, session.IdleFor(5_000), "time since launch");

        session.Opened(8_000);
        Assert.Equal(2_000, session.IdleFor(10_000), "opening resets the idle span");

        session.Closed(12_000);
        Assert.Equal(3_000, session.IdleFor(15_000), "closing resets the idle span");

        session.End();
        Assert.Equal(0, session.IdleFor(99_000), "an ended session has no idle span");
    }

    public void AStartupSessionCanBeReleasedWithoutAPreview()
    {
        var session = new PreviewSession();
        session.Started(0);

        Assert.False(session.ShouldRelease(TimeoutMs - 1, true, previewOpen: false, TimeoutMs),
            "the startup warm-up still has time");
        Assert.True(session.ShouldRelease(TimeoutMs, true, previewOpen: false, TimeoutMs),
            "the timeout releases a launch that never previewed");
    }

    public void AnEndedSessionGoesBackToCold()
    {
        var session = new PreviewSession();
        session.Opened(0);
        session.Closed(10);
        session.End();

        Assert.False(session.ShouldWarm(lowMemoryMode: true), "the next window is built cold");
        Assert.False(session.ShouldRelease(TimeoutMs * 10, true, previewOpen: false, TimeoutMs),
            "and there is nothing left to release");
    }

    public void PreviewingAgainAfterAnEndedSessionStartsANewOne()
    {
        var session = new PreviewSession();
        session.Opened(0);
        session.Closed(10);
        session.End();

        session.Opened(TimeoutMs * 5);
        Assert.True(session.ShouldWarm(lowMemoryMode: true), "warm again");
        Assert.False(session.ShouldRelease(TimeoutMs * 5 + TimeoutMs - 1, true, previewOpen: false, TimeoutMs),
            "and the countdown starts over");
    }

    /// <summary>
    /// v5.6.3: the wait is the user's choice, so the rule has to take it from the
    /// caller rather than from a constant.
    /// </summary>
    public void TheWaitIsWhateverTheUserPicked()
    {
        var session = new PreviewSession();
        session.Opened(0);
        session.Closed(0);

        const long thirtyMinutes = 30 * 60 * 1000;

        Assert.False(session.ShouldRelease(TimeoutMs + 1, true, false, thirtyMinutes),
            "past the default but not past the chosen wait");
        Assert.True(session.ShouldRelease(thirtyMinutes + 1, true, false, thirtyMinutes),
            "past the chosen wait");
    }
}
