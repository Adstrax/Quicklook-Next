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
/// v6.1.3: the thumbnail strip is virtualized, so the same small page is asked
/// for again every time it scrolls back into view. Remembering it must not turn
/// into unbounded memory, and a stale or unfrozen bitmap must never be served.
/// </summary>
internal class PdfThumbnailCacheTests
{
    private const double Scale = 1.25d;
    private const double Density = 2d;

    public void ARealizedThumbnailIsServedFromTheCache()
    {
        var cache = new PdfThumbnailCache();
        var thumbnail = Bitmap(8, 8);
        Assert.True(cache.Add(4, Scale, Density, thumbnail), "the thumbnail is retained");

        Assert.True(ReferenceEquals(thumbnail, cache.Find(4, Scale, Density)), "the very same bitmap comes back");
    }

    public void APageThatWasNeverRenderedHasNoThumbnail()
    {
        var cache = new PdfThumbnailCache();
        cache.Add(4, Scale, Density, Bitmap(8, 8));

        Assert.Null(cache.Find(5, Scale, Density), "another page is not answered with the wrong bitmap");
    }

    public void AThumbnailFromAnotherDisplayScaleIsNotServed()
    {
        var cache = new PdfThumbnailCache();
        cache.Add(4, Scale, Density, Bitmap(8, 8));

        Assert.Null(cache.Find(4, Scale + 0.5d, Density),
            "a thumbnail rendered for another monitor is not reused");
        Assert.Null(cache.Find(4, Scale, Density + 1d),
            "a thumbnail with another pixel density is not reused");
    }

    public void AnOversizedThumbnailIsNotRetained()
    {
        var cache = new PdfThumbnailCache();

        // 1024x1024 BGRA is 4 MB, above the 2 MB single-thumbnail limit.
        Assert.False(cache.Add(0, Scale, Density, Bitmap(1024, 1024)), "an implausibly large thumbnail is refused");
        Assert.Equal(0, cache.Count, "and nothing is left behind");
    }

    public void AnUnfrozenThumbnailIsNotRetained()
    {
        var cache = new PdfThumbnailCache();

        Assert.False(cache.Add(0, Scale, Density, Bitmap(8, 8, freeze: false)),
            "a bitmap that cannot be shared with the UI thread is refused");
    }

    public void ReRenderingAPageReplacesItsThumbnail()
    {
        var cache = new PdfThumbnailCache();
        cache.Add(0, Scale, Density, Bitmap(8, 8));
        var replacement = Bitmap(8, 8);
        cache.Add(0, Scale, Density, replacement);

        Assert.Equal(1, cache.Count, "a page has one thumbnail");
        Assert.True(ReferenceEquals(replacement, cache.Find(0, Scale, Density)), "the newest render wins");
    }

    public void TheLeastRecentlyUsedThumbnailIsEvictedFirst()
    {
        var cache = new PdfThumbnailCache();

        for (var page = 0; page < 300; page++)
            cache.Add(page, Scale, Density, Bitmap(4, 4));

        Assert.NotNull(cache.Find(0, Scale, Density), "touching page 0 makes it recent");

        cache.Add(300, Scale, Density, Bitmap(4, 4));

        Assert.Null(cache.Find(1, Scale, Density), "the untouched thumbnail is the one dropped");
        Assert.NotNull(cache.Find(0, Scale, Density), "the recently used thumbnail stays");
        Assert.Equal(300, cache.Count, "the entry count stays at its limit");
    }

    public void ClearingDropsEveryThumbnail()
    {
        var cache = new PdfThumbnailCache();
        cache.Add(0, Scale, Density, Bitmap(8, 8));
        cache.Clear();

        Assert.Equal(0, cache.Count, "no thumbnail survives the clear");
        Assert.Equal(0L, cache.Bytes, "and the byte total is reset");
    }

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
