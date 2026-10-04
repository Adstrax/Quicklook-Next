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
/// v5.6.2: the low memory mode keeps its idle footprint, but must not make every
/// preview pay the window's first-Show cost - measured at 325 ms per preview instead
/// of 120 ms, because the window is rebuilt on every close. These are the rules that
/// decide when the parked window is warmed and when it is handed back.
/// </summary>
internal class PreviewSessionTests
{
    private const long TimeoutMs = 90000;

    public void NothingIsWarmedUntilTheUserPreviewsSomething()
    {
        var session = new PreviewSession();

        Assert.False(session.ShouldWarm(lowMemoryMode: true), "low memory mode, nothing previewed yet");
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

    public void AnOpenPreviewIsNeverReleased()
    {
        var session = new PreviewSession();
        session.Opened(0);

        // Closing the window would close the preview the user is reading.
        Assert.False(session.ShouldRelease(TimeoutMs * 10, true, previewOpen: true, TimeoutMs),
            "a preview is open");
    }

    public void TheNormalModeNeverReleasesItsWindow()
    {
        var session = new PreviewSession();
        session.Opened(0);
        session.Closed(0);

        Assert.False(session.ShouldRelease(TimeoutMs * 10, lowMemoryMode: false, previewOpen: false, TimeoutMs),
            "normal mode");
    }

    public void ASessionThatNeverPreviewedHasNothingToRelease()
    {
        var session = new PreviewSession();

        Assert.False(session.ShouldRelease(TimeoutMs * 10, true, previewOpen: false, TimeoutMs),
            "nothing was warmed");
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

        const long fiveMinutes = 5 * 60 * 1000;

        Assert.False(session.ShouldRelease(TimeoutMs + 1, true, false, fiveMinutes),
            "past the default but not past the chosen wait");
        Assert.True(session.ShouldRelease(fiveMinutes + 1, true, false, fiveMinutes),
            "past the chosen wait");
    }
}
