// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Plugin.PDFViewer;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickLook.Tests;

/// <summary>
/// v6.1.3: the PDF viewer keeps rasterized pages so flipping back and forth does
/// not rasterize the same page twice. Serving a bitmap that is too soft, or one
/// belonging to the previous version of the file, would be worse than rendering
/// again, so both rules are pinned here.
/// </summary>
internal class PdfPageBitmapCacheTests
{
    private const long FileLength = 4_000_000L;
    private const long FileTicks = 638_000_000_000_000_000L;
    private const string FilePath = @"C:\documents\paper.pdf";

    public void ARevisitedPageIsServedFromTheCache()
    {
        var cache = Cache();
        var page = Bitmap(10, 10);
        Assert.True(cache.Add(FilePath, FileLength, FileTicks, 3, 1d, 1d, page), "the page is retained");

        var hit = cache.Find(FilePath, FileLength, FileTicks, 3, 1d, 1d, out var factor);

        Assert.True(ReferenceEquals(page, hit), "the very same bitmap comes back");
        Assert.Equal(1d, factor, "the cached render factor is reported");
    }

    public void ACachedPageMustBeAtLeastAsSharpAsTheViewAsks()
    {
        var cache = Cache();
        cache.Add(FilePath, FileLength, FileTicks, 3, 1d, 1d, Bitmap(10, 10));

        Assert.Null(cache.Find(FilePath, FileLength, FileTicks, 3, 1.3d, 1d, out _),
            "a bitmap rendered smaller than the view is not reused");
    }

    public void ASharperPageIsReusedWhenZoomingBackOut()
    {
        var cache = Cache();
        var sharp = Bitmap(30, 30);
        cache.Add(FilePath, FileLength, FileTicks, 3, 1.5d, 1d, sharp);

        var hit = cache.Find(FilePath, FileLength, FileTicks, 3, 1d, 1d, out var factor);

        Assert.True(ReferenceEquals(sharp, hit), "the sharper bitmap is preferred over re-rendering");
        Assert.Equal(1.5d, factor, "the bitmap keeps the factor it was rendered at");
    }

    public void TheLeastDetailedPageThatIsStillSharpEnoughWins()
    {
        var cache = Cache();
        var detailed = Bitmap(30, 30);
        var cheap = Bitmap(10, 10);
        cache.Add(FilePath, FileLength, FileTicks, 3, 2d, 1d, detailed);
        cache.Add(FilePath, FileLength, FileTicks, 3, 1d, 1d, cheap);

        var hit = cache.Find(FilePath, FileLength, FileTicks, 3, 1d, 1d, out var factor);

        Assert.True(ReferenceEquals(cheap, hit), "the cheaper sufficient bitmap is used");
        Assert.Equal(1d, factor, "and its factor is reported");
    }

    public void AChangedFileIsNotServedFromTheCache()
    {
        var cache = Cache();
        cache.Add(FilePath, FileLength, FileTicks, 3, 1d, 1d, Bitmap(10, 10));

        Assert.Null(cache.Find(FilePath, FileLength, FileTicks + 1, 3, 1d, 1d, out _),
            "an edited file cannot read pages rendered from its old bytes");
        Assert.Null(cache.Find(FilePath, FileLength + 1, FileTicks, 3, 1d, 1d, out _),
            "a replaced file of a different length cannot either");
    }

    public void TwoFilesWithTheSameStampAreNotConfused()
    {
        var cache = Cache();
        var page = Bitmap(10, 10);
        cache.Add(FilePath, FileLength, FileTicks, 3, 1d, 1d, page);

        Assert.Null(cache.Find(@"C:\documents\other.pdf", FileLength, FileTicks, 3, 1d, 1d, out _),
            "another file of the same size and timestamp must not reuse this page");
        Assert.True(ReferenceEquals(
            page, cache.Find(@"C:\DOCUMENTS\PAPER.PDF", FileLength, FileTicks, 3, 1d, 1d, out _)),
            "the same Windows path is matched case-insensitively");
    }

    public void ABitmapFromAnotherDisplayScaleIsNotServed()
    {
        var cache = Cache();
        cache.Add(FilePath, FileLength, FileTicks, 3, 1d, 1d, Bitmap(10, 10));

        Assert.Null(cache.Find(FilePath, FileLength, FileTicks, 3, 1d, 1.5d, out _),
            "a page rasterized for another monitor is not reused");
    }

    public void TheLeastRecentlyUsedPageIsEvictedFirst()
    {
        var cache = new PdfPageBitmapCache(2, 64L * 1024 * 1024, 64L * 1024 * 1024);
        cache.Add(FilePath, FileLength, FileTicks, 0, 1d, 1d, Bitmap(10, 10));
        cache.Add(FilePath, FileLength, FileTicks, 1, 1d, 1d, Bitmap(10, 10));

        Assert.NotNull(cache.Find(FilePath, FileLength, FileTicks, 0, 1d, 1d, out _), "touching page 0 makes it recent");

        cache.Add(FilePath, FileLength, FileTicks, 2, 1d, 1d, Bitmap(10, 10));

        Assert.Null(cache.Find(FilePath, FileLength, FileTicks, 1, 1d, 1d, out _), "the untouched page is the one dropped");
        Assert.NotNull(cache.Find(FilePath, FileLength, FileTicks, 0, 1d, 1d, out _), "the recently used page stays");
    }

    public void TheByteBudgetAlsoEvictsPages()
    {
        // One 10x10 BGRA page is 400 bytes; the cache may hold two of them.
        var cache = new PdfPageBitmapCache(10, 900, 900);
        cache.Add(FilePath, FileLength, FileTicks, 0, 1d, 1d, Bitmap(10, 10));
        cache.Add(FilePath, FileLength, FileTicks, 1, 1d, 1d, Bitmap(10, 10));
        cache.Add(FilePath, FileLength, FileTicks, 2, 1d, 1d, Bitmap(10, 10));

        Assert.Equal(2, cache.Count, "the entry count follows the byte budget");
        Assert.True(cache.Bytes <= 900, "the retained bitmaps stay inside the budget");
        Assert.Null(cache.Find(FilePath, FileLength, FileTicks, 0, 1d, 1d, out _), "the oldest page left the cache");
    }

    public void AnOversizedPageIsShownButNotRetained()
    {
        var cache = new PdfPageBitmapCache(4, 64L * 1024 * 1024, 1_000);

        Assert.False(cache.Add(FilePath, FileLength, FileTicks, 0, 1d, 1d, Bitmap(100, 100)),
            "a page above the single-entry limit is not kept");
        Assert.Equal(0, cache.Count, "and nothing is left behind");
    }

    public void PrefetchingSkipsPagesTheCacheWouldRefuse()
    {
        var cache = new PdfPageBitmapCache(4, 64L * 1024 * 1024, 1_000);

        Assert.False(cache.CanRetain(100, 100), "a page that cannot be retained is not rendered ahead");
        Assert.True(cache.CanRetain(10, 10), "a page that fits the budget is worth rendering ahead");
    }

    public void AnUnfrozenBitmapIsNotRetained()
    {
        var cache = Cache();

        Assert.False(cache.Add(FilePath, FileLength, FileTicks, 0, 1d, 1d, Bitmap(10, 10, freeze: false)),
            "a bitmap that cannot be shared across threads is refused");
    }

    public void ReRenderingTheSamePageKeepsOneCopy()
    {
        var cache = Cache();
        cache.Add(FilePath, FileLength, FileTicks, 0, 1d, 1d, Bitmap(10, 10));
        var bytes = cache.Bytes;
        cache.Add(FilePath, FileLength, FileTicks, 0, 1d, 1d, Bitmap(10, 10));

        Assert.Equal(1, cache.Count, "the page is stored once");
        Assert.Equal(bytes, cache.Bytes, "and counted once");
    }

    /// <summary>
    /// v6.1.5: wheel zooming renders the same page at several factors. Keeping
    /// every one of them used to fill the whole cache with a single page.
    /// </summary>
    public void ZoomingInKeepsOnlyTheSharpestRasterization()
    {
        var cache = Cache();
        cache.Add(FilePath, FileLength, FileTicks, 3, 1d, 1d, Bitmap(10, 10));
        cache.Add(FilePath, FileLength, FileTicks, 3, 1.5d, 1d, Bitmap(20, 20));
        var sharp = Bitmap(30, 30);
        cache.Add(FilePath, FileLength, FileTicks, 3, 2d, 1d, sharp);

        Assert.Equal(1, cache.Count, "the page keeps one rasterization after zooming");
        Assert.Equal(PdfPageBitmapCache.EstimateBytes(sharp), cache.Bytes, "only the sharpest one is counted");

        var hit = cache.Find(FilePath, FileLength, FileTicks, 3, 1d, 1d, out var factor);
        Assert.True(ReferenceEquals(sharp, hit), "zooming back out reuses the sharpest bitmap");
        Assert.Equal(2d, factor, "and reports the factor it was rendered at");
    }

    /// <summary>
    /// The other direction: a smaller render must not throw away a sharper
    /// bitmap that is still useful when the user zooms in again.
    /// </summary>
    public void AddingASmallerRenderKeepsTheSharperRasterization()
    {
        var cache = Cache();
        var sharp = Bitmap(30, 30);
        cache.Add(FilePath, FileLength, FileTicks, 3, 2d, 1d, sharp);
        cache.Add(FilePath, FileLength, FileTicks, 3, 1d, 1d, Bitmap(10, 10));

        Assert.Equal(2, cache.Count, "the sharper render is not replaced by the smaller one");
        Assert.True(ReferenceEquals(
            sharp, cache.Find(FilePath, FileLength, FileTicks, 3, 1.6d, 1d, out _)),
            "magnifying again still finds the sharper bitmap");
    }

    public void ARenderFromAnotherDisplayScaleDoesNotReplaceThisOne()
    {
        var cache = Cache();
        var here = Bitmap(10, 10);
        cache.Add(FilePath, FileLength, FileTicks, 3, 1d, 1d, here);
        cache.Add(FilePath, FileLength, FileTicks, 3, 2d, 2d, Bitmap(20, 20));

        Assert.Equal(2, cache.Count, "a render for another monitor is a separate entry");
        Assert.True(ReferenceEquals(
            here, cache.Find(FilePath, FileLength, FileTicks, 3, 1d, 1d, out _)),
            "the bitmap for this monitor is still served");
    }

    public void ClearingDropsEveryPage()
    {
        var cache = Cache();
        cache.Add(FilePath, FileLength, FileTicks, 0, 1d, 1d, Bitmap(10, 10));
        cache.Clear();

        Assert.Equal(0, cache.Count, "no page survives the clear");
        Assert.Equal(0L, cache.Bytes, "and the byte total is reset");
    }

    private static PdfPageBitmapCache Cache()
        => new(8, 64L * 1024 * 1024, 32L * 1024 * 1024);

    private static BitmapSource Bitmap(int width, int height, bool freeze = true)
    {
        var stride = width * 4;
        var image = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, new byte[stride * height], stride);

        if (freeze)
            image.Freeze();

        return image;
    }
}
