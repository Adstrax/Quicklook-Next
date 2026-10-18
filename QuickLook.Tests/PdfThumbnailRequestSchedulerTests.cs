// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Plugin.PDFViewer;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickLook.Tests;

/// <summary>
/// The virtualized thumbnail strip can realize hundreds of pages during one
/// flick. Only the pages still near the viewport should reach the PDF renderer.
/// </summary>
internal class PdfThumbnailRequestSchedulerTests
{
    public void ScrollingPastAPageCancelsItsQueuedRender()
    {
        var renders = new List<int>();
        using var scheduler = new PdfThumbnailRequestScheduler(
            page =>
            {
                lock (renders)
                    renders.Add(page);
                return Bitmap();
            },
            pageCount: 30,
            allowPrefetch: true);

        scheduler.Pause();
        var stale = scheduler.Request(2);
        scheduler.SetVisibleRange(18, 20);

        Assert.True(stale.IsCanceled, "a page outside the viewport is retired");

        scheduler.Resume();
        WaitFor(stale);

        lock (renders)
            Assert.Equal(0, renders.Count, "the stale page never reaches the renderer");
    }

    public void ReturningToAPageCreatesAFreshRequest()
    {
        var renders = new List<int>();
        using var scheduler = new PdfThumbnailRequestScheduler(
            page =>
            {
                lock (renders)
                    renders.Add(page);
                return Bitmap();
            },
            pageCount: 30,
            allowPrefetch: true);

        scheduler.Pause();
        var stale = scheduler.Request(2);
        scheduler.SetVisibleRange(18, 20);
        Assert.True(stale.IsCanceled, "the first visit is retired");

        scheduler.SetVisibleRange(2, 4);
        var fresh = scheduler.Request(2);
        scheduler.Resume();
        WaitFor(fresh);

        Assert.False(fresh.IsCanceled, "the returned page is requested again");
        Assert.NotNull(fresh.Result, "its thumbnail is produced");
        lock (renders)
            Assert.Equal(1, renders.Count, "only the current visit is rendered");
    }

    public void APageKeptByARealizedContainerIsNotCancelled()
    {
        var renders = new List<int>();
        using var scheduler = new PdfThumbnailRequestScheduler(
            page =>
            {
                lock (renders)
                    renders.Add(page);
                return Bitmap();
            },
            pageCount: 30,
            allowPrefetch: true);

        scheduler.Pause();
        var realized = scheduler.Request(2);
        scheduler.SetVisibleRange(18, 20, new HashSet<int> { 2 });

        Assert.False(realized.IsCanceled,
            "an item that still has a container must not be left on its loading image");

        scheduler.Resume();
        WaitFor(realized);

        Assert.NotNull(realized.Result, "the realized item eventually receives its thumbnail");
        lock (renders)
            Assert.Equal(1, renders.Count, "it is rendered exactly once");
    }

    public void VisiblePagesRunBeforePrefetchPages()
    {
        var renders = new List<int>();
        using var scheduler = new PdfThumbnailRequestScheduler(
            page =>
            {
                lock (renders)
                    renders.Add(page);
                return Bitmap();
            },
            pageCount: 20,
            allowPrefetch: true);

        scheduler.SetVisibleRange(3, 5);
        scheduler.Pause();

        var prefetch = scheduler.Request(9, ThumbnailRequestKind.Prefetch);
        var visible = scheduler.Request(4);
        scheduler.Resume();

        WaitFor(prefetch);
        WaitFor(visible);

        lock (renders)
        {
            Assert.Equal(2, renders.Count, "both pages are rendered");
            Assert.Equal(4, renders[0], "the visible page goes first");
            Assert.Equal(9, renders[1], "prefetch waits for visible work");
        }
    }

    public void RepeatedRequestsForOnePageShareOneRender()
    {
        var renderCount = 0;
        using var scheduler = new PdfThumbnailRequestScheduler(
            _ =>
            {
                renderCount++;
                return Bitmap();
            },
            pageCount: 10,
            allowPrefetch: true);

        scheduler.Pause();
        var first = scheduler.Request(6);
        var second = scheduler.Request(6);
        scheduler.Resume();

        WaitFor(first);
        WaitFor(second);

        Assert.True(ReferenceEquals(first, second), "both bindings wait on the same job");
        Assert.Equal(1, renderCount, "the page is rasterized once");
    }

    public void LowMemoryModeRejectsPrefetch()
    {
        var renderCount = 0;
        using var scheduler = new PdfThumbnailRequestScheduler(
            _ =>
            {
                renderCount++;
                return Bitmap();
            },
            pageCount: 10,
            allowPrefetch: false);

        var prefetch = scheduler.Request(4, ThumbnailRequestKind.Prefetch);

        Assert.True(prefetch.IsCanceled, "no page is rendered ahead in low memory mode");
        Assert.Equal(0, renderCount, "the renderer is never called");
    }

    public void DisposingTheViewerRetiresQueuedWork()
    {
        var renderCount = 0;
        var scheduler = new PdfThumbnailRequestScheduler(
            _ =>
            {
                renderCount++;
                return Bitmap();
            },
            pageCount: 10,
            allowPrefetch: true);

        scheduler.Pause();
        var pending = scheduler.Request(1);
        scheduler.Dispose();

        Assert.True(pending.IsCanceled, "closing the preview retires its pending thumbnails");
        Assert.Equal(0, renderCount, "nothing starts after disposal");
    }

    private static BitmapSource Bitmap()
    {
        const int width = 4;
        const int height = 4;
        var stride = width * 4;
        var image = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[stride * height],
            stride);
        image.Freeze();
        return image;
    }

    private static void WaitFor(Task task)
    {
        var completed = Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)))
            .GetAwaiter()
            .GetResult();
        Assert.True(ReferenceEquals(completed, task), "the scheduler job completes promptly");

        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Callers that expect cancellation assert Task.IsCanceled directly.
        }
    }
}
