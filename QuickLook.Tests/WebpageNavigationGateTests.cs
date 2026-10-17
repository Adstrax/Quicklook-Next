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

using QuickLook.Plugin.Shared;

namespace QuickLook.Tests;

/// <summary>
/// v6.0.0: a reused web panel may only commit the document from its latest navigation.
/// </summary>
internal class WebpageNavigationGateTests
{
    public void OnlyTheNewestNavigationIsCurrent()
    {
        var gate = new WebpageNavigationGate();

        gate.Accept(41);
        gate.Accept(42);

        Assert.True(gate.IsCurrent(42), "the newer navigation can commit");
        Assert.False(gate.IsCurrent(41), "a late completion from the older navigation must be dropped");
    }

    public void AnUnseenCompletionCannotCommit()
    {
        var gate = new WebpageNavigationGate();

        gate.Accept(7);

        Assert.False(gate.IsCurrent(8), "only a completion whose start was observed can commit");
    }

    public void ARecreatedControllerStartsWithNoCurrentNavigation()
    {
        var gate = new WebpageNavigationGate();
        gate.Accept(7);

        gate.Reset();

        Assert.False(gate.IsCurrent(7), "navigation ids from the retired controller must not carry over");
    }

    public void ARepeatedCompletionForTheLatestNavigationIsHarmless()
    {
        var gate = new WebpageNavigationGate();

        gate.Accept(9);
        gate.Accept(9);

        Assert.True(gate.IsCurrent(9), "the latest navigation remains current");
    }

    public void OnlyTheNewestDocumentLoadCanReportReady()
    {
        var state = new WebpageContentLoadState();

        var older = state.Begin();
        var newer = state.Begin();
        state.MarkReady();

        Assert.True(state.IsReady(newer), "the newest document can be shown");
        Assert.False(state.IsReady(older), "a superseded document cannot complete the handoff");
    }

    public void ReadinessDoesNotCarryIntoTheNextDocument()
    {
        var state = new WebpageContentLoadState();
        var first = state.Begin();
        state.MarkReady();

        var second = state.Begin();

        Assert.False(state.IsReady(first), "the previous document is no longer current");
        Assert.False(state.IsReady(second), "the new document has not rendered yet");
    }

    public void ResettingThePanelCannotCompleteAWaitingHandoff()
    {
        var state = new WebpageContentLoadState();
        var version = state.Begin();
        state.MarkReady();

        state.Reset();

        Assert.False(state.IsReady(version), "a rebuilt browser cannot acknowledge the old document");
    }
}
