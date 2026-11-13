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
using System.Threading.Tasks;

namespace QuickLook.Common.Helpers;

public enum BackgroundTaskState
{
    Success,
    Empty,
    Faulted,
    Canceled
}

/// <summary>
/// Captures a completed background task without reading <see cref="Task{TResult}.Result"/>
/// while it is canceled or faulted. Reading every task, including one whose preview
/// has already closed, also observes its exception and prevents it from surfacing
/// later on the finalizer thread.
/// </summary>
public readonly record struct BackgroundTaskResult<T>(
    BackgroundTaskState State,
    T Value,
    Exception Error)
{
    public bool IsSuccess => State == BackgroundTaskState.Success;

    public static BackgroundTaskResult<T> Read(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.IsCanceled)
            return new(BackgroundTaskState.Canceled, default, new TaskCanceledException(task));

        if (task.IsFaulted)
        {
            return new(
                BackgroundTaskState.Faulted,
                default,
                task.Exception?.GetBaseException() ??
                new InvalidOperationException("The background task failed."));
        }

        if (!task.IsCompletedSuccessfully)
        {
            return new(
                BackgroundTaskState.Faulted,
                default,
                new InvalidOperationException("The background task did not complete."));
        }

        var value = task.Result;
        return value is null
            ? new(
                BackgroundTaskState.Empty,
                default,
                new InvalidOperationException("The background task returned no result."))
            : new(BackgroundTaskState.Success, value, null);
    }
}
