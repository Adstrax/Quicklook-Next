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

using QuickLook.Plugin.OfficeViewer;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLook.Tests;

internal class OfficePanelAsyncResultTests
{
    public void ASuccessfulTaskYieldsItsContent()
    {
        var content = Content();
        var result = OfficePanelContentTaskResult.Read(Task.FromResult(content));

        Assert.True(result.IsSuccess, "a completed task is usable");
        Assert.True(ReferenceEquals(content, result.Content), "the exact page is returned");
        Assert.Null(result.Error, "success has no error");
    }

    public void AnEmptyTaskIsReportedAsAFailure()
    {
        var result = OfficePanelContentTaskResult.Read(Task.FromResult<OfficePanelContent>(null));

        Assert.Equal(OfficePanelContentTaskState.Empty, result.State, "null is not a rendered page");
        Assert.NotNull(result.Error, "an empty result explains what happened");
    }

    public void AFaultedTaskExposesItsBaseException()
    {
        var original = new InvalidOperationException("bad OOXML");
        var result = OfficePanelContentTaskResult.Read(Task.FromException<OfficePanelContent>(original));

        Assert.Equal(OfficePanelContentTaskState.Faulted, result.State, "the parser failed");
        Assert.True(ReferenceEquals(original, result.Error), "the actionable exception is preserved");
    }

    public void ACanceledTaskDoesNotThrowWhenRead()
    {
        var canceled = Task.FromCanceled<OfficePanelContent>(new CancellationToken(true));
        var result = OfficePanelContentTaskResult.Read(canceled);

        Assert.Equal(OfficePanelContentTaskState.Canceled, result.State, "cancellation is distinct");
        Assert.True(result.Error is TaskCanceledException, "cancellation has a matching error");
        Assert.Null(result.Content, "a canceled task has no page");
    }

    public void TheNewestRequestSupersedesEveryOlderOne()
    {
        var requests = new LatestOfficeContentRequest();
        var first = requests.Begin();
        var second = requests.Begin();

        Assert.False(requests.IsCurrent(first), "the old worksheet or page is stale");
        Assert.True(requests.IsCurrent(second), "the newest selection remains current");
    }

    public void InvalidatingARequestRejectsTheOutstandingWork()
    {
        var requests = new LatestOfficeContentRequest();
        var pending = requests.Begin();
        requests.Invalidate();

        Assert.False(requests.IsCurrent(pending), "closing or quiescing retires pending work");
    }

    public void DiscardingAStaleResultReleasesItsPictureFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"qkn-office-stale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var picture = Path.Combine(folder, "picture.png");
        File.WriteAllBytes(picture, [1, 2, 3]);

        try
        {
            var result = OfficePanelContentTaskResult.Read(
                Task.FromResult(Content(imageFolder: folder)));

            result.Discard();

            Assert.False(Directory.Exists(folder), "a superseded render cannot leave its pictures behind");
        }
        finally
        {
            OfficeImageStore.Cleanup(folder);
        }
    }

    private static OfficePanelContent Content(string imageFolder = null) => new()
    {
        Html = "<html><body>page</body></html>",
        ImageFolder = imageFolder,
    };
}
