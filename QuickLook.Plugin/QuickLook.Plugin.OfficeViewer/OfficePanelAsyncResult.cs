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
using System.Threading;
using System.Threading.Tasks;

namespace QuickLook.Plugin.OfficeViewer;

internal enum OfficePanelContentTaskState
{
    Success,
    Empty,
    Faulted,
    Canceled,
}

/// <summary>
/// Reads a background Office-page build without touching <see cref="Task{TResult}.Result"/>
/// unless the task succeeded. A canceled task in particular must not turn into
/// an AggregateException on the UI thread.
/// </summary>
internal readonly record struct OfficePanelContentTaskResult(
    OfficePanelContentTaskState State,
    OfficePanelContent Content,
    Exception Error)
{
    internal bool IsSuccess => State == OfficePanelContentTaskState.Success;

    internal static OfficePanelContentTaskResult Read(Task<OfficePanelContent> task)
    {
        if (task.IsCanceled)
            return new(OfficePanelContentTaskState.Canceled, null, new TaskCanceledException(task));

        if (task.IsFaulted)
            return new(
                OfficePanelContentTaskState.Faulted,
                null,
                task.Exception?.GetBaseException() ?? new InvalidOperationException("Office rendering failed."));

        if (!task.IsCompletedSuccessfully)
            return new(
                OfficePanelContentTaskState.Faulted,
                null,
                new InvalidOperationException("Office rendering did not complete."));

        var content = task.Result;
        return content is null
            ? new(
                OfficePanelContentTaskState.Empty,
                null,
                new InvalidOperationException("The Office renderer returned no content."))
            : new(OfficePanelContentTaskState.Success, content, null);
    }

    /// <summary>
    /// Drops a successful result that is no longer current. Cached renders own
    /// a cloned picture folder, so failing to do this would leave that folder
    /// behind after every rapid switch.
    /// </summary>
    internal void Discard()
    {
        if (IsSuccess)
            OfficeImageStore.Cleanup(Content.ImageFolder);
    }
}

/// <summary>
/// Tracks the newest content request for one panel. Page and worksheet
/// switches can overlap, and only the last selection is allowed to reach the
/// WebView.
/// </summary>
internal sealed class LatestOfficeContentRequest
{
    private int _version;

    internal int Begin() => Interlocked.Increment(ref _version);

    internal bool IsCurrent(int version) => version == Volatile.Read(ref _version);

    internal void Invalidate() => Interlocked.Increment(ref _version);
}
