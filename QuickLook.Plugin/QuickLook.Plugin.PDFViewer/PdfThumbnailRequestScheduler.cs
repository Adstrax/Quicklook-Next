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
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.PDFViewer;

internal enum ThumbnailRequestKind
{
    Prefetch,
    Visible
}

/// <summary>
/// Serializes thumbnail rasterization and drops queued work for pages that the
/// virtualized strip has already scrolled past.
/// <para>
/// Each realized list item used to start its own task. During a fast scroll
/// those tasks accumulated in front of PDFium's render lock, so the strip kept
/// rasterizing pages that were no longer visible. This queue gives visible
/// pages priority, uses one renderer at a time, and can be paused while the
/// full-size page is being rendered.
/// </para>
/// </summary>
internal sealed class PdfThumbnailRequestScheduler : IDisposable
{
    private const int VisiblePrefetchRadius = 1;

    private sealed class PendingRequest
    {
        internal int Page;
        internal ThumbnailRequestKind Kind;
        internal long Sequence;
        internal int Priority;
        internal bool IsRendering;
        internal TaskCompletionSource<BitmapSource> Completion;
    }

    private readonly Func<int, BitmapSource> _render;
    private readonly int _pageCount;
    private readonly bool _allowPrefetch;
    private readonly object _sync = new();
    private readonly Dictionary<int, PendingRequest> _pending = [];
    private readonly SemaphoreSlim _workSignal = new(0);

    private int _firstVisible = -1;
    private int _lastVisible = -1;
    private int _pauseDepth;
    private long _sequence;
    private bool _disposed;

    internal PdfThumbnailRequestScheduler(
        Func<int, BitmapSource> render,
        int pageCount,
        bool allowPrefetch)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _pageCount = Math.Max(0, pageCount);
        _allowPrefetch = allowPrefetch;

        _ = Task.Run(WorkerLoopAsync);
    }

    /// <summary>
    /// Requests one page. A repeated request for the same page shares the
    /// in-flight render instead of starting another one.
    /// </summary>
    internal Task<BitmapSource> Request(
        int page,
        ThumbnailRequestKind kind = ThumbnailRequestKind.Visible)
    {
        var signal = false;
        Task<BitmapSource> task;

        lock (_sync)
        {
            if (_disposed || page < 0 || page >= _pageCount ||
                (kind == ThumbnailRequestKind.Prefetch && !_allowPrefetch))
                return CanceledTask();

            if (_pending.TryGetValue(page, out var existing))
            {
                if (kind == ThumbnailRequestKind.Visible)
                    existing.Kind = ThumbnailRequestKind.Visible;

                existing.Priority = PriorityFor(page, existing.Kind);
                return existing.Completion.Task;
            }

            var request = new PendingRequest
            {
                Page = page,
                Kind = kind,
                Sequence = ++_sequence,
                Priority = PriorityFor(page, kind),
                Completion = new TaskCompletionSource<BitmapSource>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
            };

            _pending.Add(page, request);
            task = request.Completion.Task;
            signal = true;
        }

        if (signal)
            _workSignal.Release();

        return task;
    }

    /// <summary>
    /// Updates the pages that actually occupy the strip's viewport. Pending
    /// work farther away than one neighbouring page is cancelled before it
    /// reaches PDFium, unless the virtualized panel still has a live container
    /// for that page. A request already inside a render is allowed to finish.
    /// </summary>
    internal void SetVisibleRange(
        int firstVisible,
        int lastVisible,
        ISet<int> retainedPages = null)
    {
        if (_pageCount <= 0)
            return;

        firstVisible = Math.Clamp(firstVisible, 0, _pageCount - 1);
        lastVisible = Math.Clamp(lastVisible, firstVisible, _pageCount - 1);

        List<PendingRequest> cancel = null;

        lock (_sync)
        {
            if (_disposed)
                return;

            _firstVisible = firstVisible;
            _lastVisible = lastVisible;

            var keepFirst = firstVisible - VisiblePrefetchRadius;
            var keepLast = lastVisible + VisiblePrefetchRadius;

            foreach (var request in _pending.Values)
            {
                if (request.IsRendering)
                    continue;

                var retained = retainedPages?.Contains(request.Page) == true;
                if (!retained &&
                    (request.Page < keepFirst || request.Page > keepLast))
                {
                    cancel ??= [];
                    cancel.Add(request);
                    continue;
                }

                request.Priority = PriorityFor(request.Page, request.Kind);
            }

            if (cancel is not null)
            {
                foreach (var request in cancel)
                    _pending.Remove(request.Page);
            }
        }

        if (cancel is not null)
        {
            foreach (var request in cancel)
                request.Completion.TrySetCanceled();
        }
    }

    /// <summary>
    /// Stops starting new thumbnail renders. Full-size page renders use this so
    /// a burst of list work cannot delay the page the user is looking at.
    /// </summary>
    internal void Pause()
    {
        lock (_sync)
            _pauseDepth++;
    }

    internal void Resume()
    {
        var signal = false;

        lock (_sync)
        {
            if (_disposed)
                return;

            if (_pauseDepth > 0)
                _pauseDepth--;

            signal = _pauseDepth == 0;
        }

        if (signal)
            _workSignal.Release();
    }

    public void Dispose()
    {
        List<PendingRequest> cancel;

        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            cancel = [];
            foreach (var request in _pending.Values)
            {
                if (!request.IsRendering)
                    cancel.Add(request);
            }

            _pending.Clear();
        }

        foreach (var request in cancel)
            request.Completion.TrySetCanceled();

        _workSignal.Release();
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            await _workSignal.WaitAsync().ConfigureAwait(false);

            while (true)
            {
                PendingRequest request;

                lock (_sync)
                {
                    if (_disposed)
                        return;

                    if (_pauseDepth > 0)
                        break;

                    request = TakeNext();
                    if (request is null)
                        break;

                    request.IsRendering = true;
                }

                BitmapSource image = null;
                try
                {
                    image = _render(request.Page);
                }
                catch (Exception)
                {
                    // A document can be disposed between queueing and rendering.
                    // The list item simply keeps its loading placeholder.
                }

                lock (_sync)
                {
                    if (_pending.TryGetValue(request.Page, out var current) &&
                        ReferenceEquals(current, request))
                        _pending.Remove(request.Page);
                }

                request.Completion.TrySetResult(image);
            }
        }
    }

    private PendingRequest TakeNext()
    {
        PendingRequest best = null;

        foreach (var request in _pending.Values)
        {
            if (request.IsRendering)
                continue;

            if (best is null ||
                request.Priority > best.Priority ||
                (request.Priority == best.Priority && request.Sequence < best.Sequence))
                best = request;
        }

        return best;
    }

    private int PriorityFor(int page, ThumbnailRequestKind kind)
    {
        if (_firstVisible >= 0 &&
            page >= _firstVisible && page <= _lastVisible)
            return 3;

        // The list realizes a little beyond the viewport. Those pages, and the
        // explicit neighbours, are useful but must wait for the visible ones.
        if (_firstVisible >= 0 &&
            page >= _firstVisible - VisiblePrefetchRadius &&
            page <= _lastVisible + VisiblePrefetchRadius)
            return 2;

        return kind == ThumbnailRequestKind.Visible ? 1 : 0;
    }

    private static Task<BitmapSource> CanceledTask()
    {
        var completion = new TaskCompletionSource<BitmapSource>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetCanceled();
        return completion.Task;
    }
}
