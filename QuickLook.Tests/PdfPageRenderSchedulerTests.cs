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
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickLook.Tests;

/// <summary>
/// A burst of page changes must not leave obsolete full-size renders queued in
/// front of the page the user selected last.
/// </summary>
internal class PdfPageRenderSchedulerTests
{
    public void OnlyTheNewestQueuedPageIsRendered()
    {
        var renders = new List<int>();
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();

        using var scheduler = new PdfPageRenderScheduler();

        var first = scheduler.Request(() =>
        {
            lock (renders)
                renders.Add(1);
            started.Set();
            release.Wait(TimeSpan.FromSeconds(3));
            return Bitmap();
        });

        Assert.True(started.Wait(TimeSpan.FromSeconds(3)), "the first page starts rendering");

        var second = scheduler.Request(() =>
        {
            lock (renders)
                renders.Add(2);
            return Bitmap();
        });
        var third = scheduler.Request(() =>
        {
            lock (renders)
                renders.Add(3);
            return Bitmap();
        });

        Assert.True(second.IsCanceled, "the first waiting page is replaced");
        release.Set();

        WaitFor(first);
        WaitFor(third);

        lock (renders)
        {
            Assert.Equal(2, renders.Count, "only the in-flight page and the newest page run");
            Assert.Equal(1, renders[0], "the page already rendering is allowed to finish");
            Assert.Equal(3, renders[1], "the newest page runs next");
        }
    }

    public void TheWorkerReturnsToIdleAfterEachRender()
    {
        var renders = 0;
        using var scheduler = new PdfPageRenderScheduler();

        var first = scheduler.Request(() =>
        {
            renders++;
            return Bitmap();
        });
        WaitFor(first);

        var second = scheduler.Request(() =>
        {
            renders++;
            return Bitmap();
        });
        WaitFor(second);

        Assert.Equal(2, renders, "a later request starts a new worker");
    }

    public void PrefetchRunsAfterForegroundWork()
    {
        var renders = new List<int>();
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();

        using var scheduler = new PdfPageRenderScheduler();

        var foreground = scheduler.Request(() =>
        {
            lock (renders)
                renders.Add(1);
            started.Set();
            release.Wait(TimeSpan.FromSeconds(3));
            return Bitmap();
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(3)), "the foreground page starts");

        var next = scheduler.Request(() =>
        {
            lock (renders)
                renders.Add(2);
            return Bitmap();
        }, PdfPageRenderPriority.Prefetch);
        var previous = scheduler.Request(() =>
        {
            lock (renders)
                renders.Add(0);
            return Bitmap();
        }, PdfPageRenderPriority.Prefetch);

        release.Set();
        WaitFor(foreground);
        WaitFor(next);
        WaitFor(previous);

        lock (renders)
            Assert.Equal("1,2,0", string.Join(",", renders),
                "prefetch waits for the foreground page and keeps its queue order");
    }

    public void ANewForegroundPageCancelsQueuedPrefetch()
    {
        var renders = new List<int>();
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();

        using var scheduler = new PdfPageRenderScheduler();

        var first = scheduler.Request(() =>
        {
            lock (renders)
                renders.Add(1);
            started.Set();
            release.Wait(TimeSpan.FromSeconds(3));
            return Bitmap();
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(3)), "the first page starts");

        var prefetch = scheduler.Request(() =>
        {
            lock (renders)
                renders.Add(2);
            return Bitmap();
        }, PdfPageRenderPriority.Prefetch);

        var second = scheduler.Request(() =>
        {
            lock (renders)
                renders.Add(3);
            return Bitmap();
        });

        Assert.True(prefetch.IsCanceled, "a real page request retires queued prefetch");

        release.Set();
        WaitFor(first);
        WaitFor(second);

        lock (renders)
            Assert.Equal("1,3", string.Join(",", renders),
                "only the in-flight page and the newest foreground page run");
    }

    public void CancelingPrefetchLeavesForegroundWorkAlone()
    {
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        using var scheduler = new PdfPageRenderScheduler();

        var foreground = scheduler.Request(() =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(3));
            return Bitmap();
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(3)), "the foreground page starts");

        var prefetch = scheduler.Request(Bitmap, PdfPageRenderPriority.Prefetch);
        scheduler.CancelPrefetch();
        Assert.True(prefetch.IsCanceled, "queued prefetch is dropped");

        release.Set();
        WaitFor(foreground);
        Assert.NotNull(foreground.Result, "foreground work still completes");
    }

    public void DisposingCancelsOnlyTheWaitingPage()
    {
        var renders = 0;
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var scheduler = new PdfPageRenderScheduler();

        var running = scheduler.Request(() =>
        {
            renders++;
            started.Set();
            release.Wait(TimeSpan.FromSeconds(3));
            return Bitmap();
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(3)), "the first page starts rendering");

        var waiting = scheduler.Request(() =>
        {
            renders++;
            return Bitmap();
        });

        scheduler.Dispose();
        Assert.True(waiting.IsCanceled, "closing the preview retires the waiting page");

        release.Set();
        WaitFor(running);

        Assert.Equal(1, renders, "an in-flight render is not interrupted");
    }

    public void AFailedRenderDoesNotStopTheNextPage()
    {
        using var scheduler = new PdfPageRenderScheduler();

        var failed = scheduler.Request(() => throw new InvalidOperationException("broken page"));
        WaitFor(failed);
        Assert.Null(failed.Result, "a failed render returns no frame");

        var next = scheduler.Request(Bitmap);
        WaitFor(next);
        Assert.NotNull(next.Result, "the scheduler remains usable");
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
        Assert.True(ReferenceEquals(completed, task), "the render completes promptly");

        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Tests that expect cancellation assert Task.IsCanceled directly.
        }
    }
}
