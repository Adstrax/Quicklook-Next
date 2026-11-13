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

using QuickLook.Common.Helpers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLook.Tests;

internal class BackgroundTaskResultTests
{
    public void ASuccessfulTaskYieldsItsResult()
    {
        var result = BackgroundTaskResult<string>.Read(Task.FromResult("ready"));

        Assert.True(result.IsSuccess, "a completed task is usable");
        Assert.Equal("ready", result.Value, "the result is preserved");
        Assert.Null(result.Error, "success has no error");
    }

    public void AnEmptyTaskIsReportedSeparately()
    {
        var result = BackgroundTaskResult<string>.Read(Task.FromResult<string>(null));

        Assert.Equal(BackgroundTaskState.Empty, result.State, "null is not a successful payload");
        Assert.NotNull(result.Error, "an empty result explains what happened");
    }

    public void AFaultedTaskObservesAndPreservesTheOriginalError()
    {
        var original = new InvalidOperationException("broken document");
        var task = Task.FromException<string>(original);

        var result = BackgroundTaskResult<string>.Read(task);

        Assert.Equal(BackgroundTaskState.Faulted, result.State, "the task is reported as failed");
        Assert.True(ReferenceEquals(original, result.Error), "the actionable error is preserved");
        Assert.True(task.Exception is not null, "reading the result observes the task exception");
    }

    public void ACanceledTaskDoesNotThrowWhenRead()
    {
        var task = Task.FromCanceled<string>(new CancellationToken(canceled: true));

        var result = BackgroundTaskResult<string>.Read(task);

        Assert.Equal(BackgroundTaskState.Canceled, result.State, "cancellation is distinct");
        Assert.True(result.Error is TaskCanceledException, "cancellation has a matching error");
        Assert.Null(result.Value, "a canceled task has no result");
    }
}
