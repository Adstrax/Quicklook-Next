// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.PDFViewer;

internal enum PdfPageRenderPriority
{
    Foreground,
    Prefetch
}

/// <summary>
/// Serializes full-page PDF renders, keeps only the newest queued foreground
/// page, and allows at most two low-priority prefetch pages behind it.
/// <para>
/// Page changes used to start one Task.Run each. During a burst of arrow-key
/// presses every obsolete page still reached PDFium and the page the user had
/// selected waited behind all of them. A foreground request now replaces every
/// page that has not started yet. Prefetch work survives only while no newer
/// page has been requested.
/// </para>
/// </summary>
internal sealed class PdfPageRenderScheduler : IDisposable
{
    private const int MaxQueuedPrefetches = 2;

    private sealed class PendingRequest
    {
        internal Func<BitmapSource> Render;
        internal TaskCompletionSource<BitmapSource> Completion;
    }

    private readonly object _sync = new();
    private readonly List<PendingRequest> _prefetch = [];
    private PendingRequest _foreground;
    private bool _workerActive;
    private bool _disposed;

    internal Task<BitmapSource> Request(
        Func<BitmapSource> render,
        PdfPageRenderPriority priority = PdfPageRenderPriority.Foreground)
    {
        if (render is null)
            throw new ArgumentNullException(nameof(render));

        List<PendingRequest> canceled = null;
        PendingRequest request;
        var startWorker = false;

        lock (_sync)
        {
            if (_disposed)
                return CanceledTask();

            request = new PendingRequest
            {
                Render = render,
                Completion = new TaskCompletionSource<BitmapSource>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
            };

            if (priority == PdfPageRenderPriority.Prefetch)
            {
                if (_prefetch.Count >= MaxQueuedPrefetches)
                {
                    canceled = [_prefetch[0]];
                    _prefetch.RemoveAt(0);
                }

                _prefetch.Add(request);
            }
            else
            {
                canceled = [];
                if (_foreground is not null)
                    canceled.Add(_foreground);
                canceled.AddRange(_prefetch);

                _foreground = request;
                _prefetch.Clear();
            }

            if (!_workerActive)
            {
                _workerActive = true;
                startWorker = true;
            }
        }

        if (canceled is not null)
        {
            foreach (var pending in canceled)
                pending.Completion.TrySetCanceled();
        }

        if (startWorker)
            _ = Task.Run(WorkerLoop);

        return request.Completion.Task;
    }

    public void Dispose()
    {
        List<PendingRequest> canceled;

        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            canceled = [];
            if (_foreground is not null)
                canceled.Add(_foreground);
            canceled.AddRange(_prefetch);

            _foreground = null;
            _prefetch.Clear();
        }

        foreach (var pending in canceled)
            pending.Completion.TrySetCanceled();
    }

    /// <summary>
    /// Drops a queued render when the viewer can paint the requested page from
    /// cache instead. A render that has already started is left alone.
    /// </summary>
    internal void CancelPending()
    {
        List<PendingRequest> canceled;

        lock (_sync)
        {
            if (_disposed)
                return;

            canceled = [];
            if (_foreground is not null)
                canceled.Add(_foreground);
            canceled.AddRange(_prefetch);

            _foreground = null;
            _prefetch.Clear();
        }

        foreach (var pending in canceled)
            pending.Completion.TrySetCanceled();
    }

    internal void CancelPrefetch()
    {
        List<PendingRequest> canceled;

        lock (_sync)
        {
            if (_disposed || _prefetch.Count == 0)
                return;

            canceled = [.. _prefetch];
            _prefetch.Clear();
        }

        foreach (var pending in canceled)
            pending.Completion.TrySetCanceled();
    }

    private void WorkerLoop()
    {
        while (true)
        {
            PendingRequest request;

            lock (_sync)
            {
                if (_disposed)
                {
                    _workerActive = false;
                    return;
                }

                if (_foreground is not null)
                {
                    request = _foreground;
                    _foreground = null;
                }
                else if (_prefetch.Count > 0)
                {
                    request = _prefetch[0];
                    _prefetch.RemoveAt(0);
                }
                else
                {
                    _workerActive = false;
                    return;
                }
            }

            BitmapSource image = null;
            try
            {
                image = request.Render();
            }
            catch (Exception)
            {
                // A document can be closed between queueing and rendering.
                // Completing with null lets the viewer keep its previous frame.
            }

            request.Completion.TrySetResult(image);
        }
    }

    private static Task<BitmapSource> CanceledTask()
    {
        var completion = new TaskCompletionSource<BitmapSource>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetCanceled();
        return completion.Task;
    }
}
