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
/// A normal launch owns a warm session immediately. Low-memory mode evaluates fixed
/// windows and records only whether each window saw a preview, never every preview.
/// </summary>
internal class PreviewSessionTests
{
    public void TheColdIdleProcessHasNothingToWarm()
    {
        var session = new PreviewSession();

        Assert.False(session.ShouldWarm(lowMemoryMode: true), "the release restart stays cold");
    }

    public void ANormalLaunchStartsAWarmSessionImmediately()
    {
        var session = new PreviewSession();
        session.Started();

        Assert.True(session.IsActive, "the launch owns a session");
        Assert.True(session.ShouldWarm(lowMemoryMode: true), "and prepares the parked window");
        Assert.False(session.HasPreviewInWindow, "the first window starts without preview activity");
    }

    public void TheNormalModeKeepsItsWindowWarmAllAlong()
    {
        var session = new PreviewSession();

        Assert.True(session.ShouldWarm(lowMemoryMode: false), "before the first preview");
        session.Opened();
        Assert.True(session.ShouldWarm(lowMemoryMode: false), "and after it");
    }

    public void OnceTheUserPreviewsTheRebuiltWindowIsWarmed()
    {
        var session = new PreviewSession();
        session.Opened();

        Assert.True(session.ShouldWarm(lowMemoryMode: true), "while the first preview is open");
        Assert.True(session.ShouldWarm(lowMemoryMode: true), "after closing it");
    }

    public void AWindowWithoutPreviewReleasesTheWarmProcess()
    {
        var session = new PreviewSession();
        session.Started();

        Assert.Equal(
            PreviewSession.WindowResult.Release,
            session.CompleteWindow(lowMemoryMode: true, previewOpen: false),
            "nothing used the warm process during the first full window");
    }

    public void APreviewKeepsTheNextCompleteWindowWarm()
    {
        var session = new PreviewSession();
        session.Started();
        session.Opened();

        Assert.Equal(
            PreviewSession.WindowResult.KeepWarm,
            session.CompleteWindow(lowMemoryMode: true, previewOpen: false),
            "one preview is enough to keep the next window warm");
        Assert.Equal(
            PreviewSession.WindowResult.Release,
            session.CompleteWindow(lowMemoryMode: true, previewOpen: false),
            "the complete window with no preview releases the process");
    }

    public void EveryWindowWithActivityEarnsAnotherWarmWindow()
    {
        var session = new PreviewSession();
        session.Started();

        session.Opened();
        Assert.Equal(
            PreviewSession.WindowResult.KeepWarm,
            session.CompleteWindow(lowMemoryMode: true, previewOpen: false),
            "the first active window keeps the process warm");

        session.Opened();
        Assert.Equal(
            PreviewSession.WindowResult.KeepWarm,
            session.CompleteWindow(lowMemoryMode: true, previewOpen: false),
            "activity in the second window keeps it warm again");
    }

    public void RepeatedPreviewsOnlySetOneWindowFlag()
    {
        var session = new PreviewSession();
        var changes = 0;
        session.ActivityChanged += (_, _) => changes++;

        session.Started();
        session.Opened();
        session.Opened();
        session.Opened();

        Assert.True(session.HasPreviewInWindow, "the window records that it saw a preview");
        Assert.Equal(1, changes, "previews do not move or restart the fixed window");
    }

    public void AnOpenPreviewCountsAsActivityAtTheBoundary()
    {
        var session = new PreviewSession();
        session.Started();

        Assert.Equal(
            PreviewSession.WindowResult.KeepWarm,
            session.CompleteWindow(lowMemoryMode: true, previewOpen: true),
            "an open preview is activity and must never be closed by the release");
    }

    public void TheFirstPreviewAfterAReleaseStartsAFreshWindow()
    {
        var session = new PreviewSession();
        var changes = 0;
        session.ActivityChanged += (_, _) => changes++;

        session.Started();
        Assert.Equal(
            PreviewSession.WindowResult.Release,
            session.CompleteWindow(lowMemoryMode: true, previewOpen: false),
            "the first window finds no activity");

        session.Opened();
        Assert.Equal(2, changes, "the first preview starts a new fixed window");
        Assert.Equal(
            PreviewSession.WindowResult.KeepWarm,
            session.CompleteWindow(lowMemoryMode: true, previewOpen: false),
            "and its activity earns another warm window");
    }

    public void ABlockedReleaseStaysPendingWhileTheSessionIsActive()
    {
        var session = new PreviewSession();
        session.Started();
        session.CompleteWindow(lowMemoryMode: true, previewOpen: false);

        Assert.True(
            session.ShouldRetryBlockedRelease(lowMemoryMode: true, previewOpen: false),
            "another window may close and allow the release later");
        Assert.False(
            session.ShouldRetryBlockedRelease(lowMemoryMode: true, previewOpen: true),
            "a preview opening during the retry owns a fresh window");
        Assert.False(
            session.ShouldRetryBlockedRelease(lowMemoryMode: false, previewOpen: false),
            "turning low memory mode off cancels the pending release");

        session.Opened();
        Assert.False(
            session.ShouldRetryBlockedRelease(lowMemoryMode: true, previewOpen: false),
            "a new preview window replaces the pending release");

        session.CompleteWindow(lowMemoryMode: true, previewOpen: false);
        session.End();
        Assert.False(
            session.ShouldRetryBlockedRelease(lowMemoryMode: true, previewOpen: false),
            "an ended session has nothing left to release");
    }

    public void TheNormalModeNeverReleasesItsWindow()
    {
        var session = new PreviewSession();
        session.Opened();

        Assert.Equal(
            PreviewSession.WindowResult.Inactive,
            session.CompleteWindow(lowMemoryMode: false, previewOpen: false),
            "normal mode has no release window");
    }

    public void AnEndedSessionGoesBackToCold()
    {
        var session = new PreviewSession();
        session.Opened();
        session.End();

        Assert.False(session.ShouldWarm(lowMemoryMode: true), "the next window is built cold");
        Assert.Equal(
            PreviewSession.WindowResult.Inactive,
            session.CompleteWindow(lowMemoryMode: true, previewOpen: false),
            "and there is no running window to complete");
    }

    public void PreviewingAgainAfterAnEndedSessionStartsANewOne()
    {
        var session = new PreviewSession();
        session.Opened();
        session.End();

        session.Opened();
        Assert.True(session.ShouldWarm(lowMemoryMode: true), "warm again");
        Assert.Equal(
            PreviewSession.WindowResult.KeepWarm,
            session.CompleteWindow(lowMemoryMode: true, previewOpen: false),
            "and its new window starts over");
    }
}
