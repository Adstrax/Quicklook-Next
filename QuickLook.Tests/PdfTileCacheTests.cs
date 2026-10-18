// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Plugin.PDFViewer;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickLook.Tests;

/// <summary>
/// v6.1.9: panning back over a region must reuse the sharp tile the user just
/// saw, but only while it still covers the view at the same zoom and monitor.
/// </summary>
internal class PdfTileCacheTests
{
    public void ACoveringTileIsReused()
    {
        var cache = new PdfTileCache(2, 32_000_000);
        var image = Bitmap(10, 10);
        var region = new Rect(0d, 0d, 400d, 400d);
        cache.Add(3, 2d, 1.5d, region, image);

        var hit = cache.TryFind(3, 2d, 1.5d, new Rect(10d, 10d, 300d, 300d), out var found, out var foundRegion);

        Assert.True(hit, "a tile that covers the view is reused");
        Assert.True(ReferenceEquals(image, found), "the same bitmap comes back");
        Assert.Equal(region, foundRegion, "its region is reported for positioning");
    }

    public void ATileFromAnotherZoomIsNotReused()
    {
        var cache = new PdfTileCache(2, 32_000_000);
        cache.Add(3, 2d, 1.5d, new Rect(0d, 0d, 400d, 400d), Bitmap(10, 10));

        Assert.False(cache.TryFind(3, 2.4d, 1.5d, new Rect(10d, 10d, 100d, 100d), out _, out _),
            "a different zoom must be rendered again");
        Assert.False(cache.TryFind(3, 2d, 2d, new Rect(10d, 10d, 100d, 100d), out _, out _),
            "a tile from another monitor is not reused");
    }

    public void ATileThatDoesNotCoverTheViewIsNotReused()
    {
        var cache = new PdfTileCache(2, 32_000_000);
        cache.Add(3, 2d, 1.5d, new Rect(0d, 0d, 400d, 400d), Bitmap(10, 10));

        Assert.False(cache.TryFind(3, 2d, 1.5d, new Rect(350d, 350d, 200d, 200d), out _, out _),
            "the viewport moved past the tile");
    }

    public void TheLeastRecentlyUsedTileIsEvicted()
    {
        var cache = new PdfTileCache(2, 32_000_000);
        cache.Add(0, 1d, 1d, new Rect(0d, 0d, 100d, 100d), Bitmap(10, 10));
        cache.Add(1, 1d, 1d, new Rect(0d, 0d, 100d, 100d), Bitmap(10, 10));

        Assert.True(cache.TryFind(0, 1d, 1d, new Rect(10d, 10d, 50d, 50d), out _, out _),
            "touching page 0 makes it the most recent");
        cache.Add(2, 1d, 1d, new Rect(0d, 0d, 100d, 100d), Bitmap(10, 10));

        Assert.Equal(2, cache.Count, "the cache keeps its two entries");
        Assert.True(cache.TryFind(0, 1d, 1d, new Rect(10d, 10d, 50d, 50d), out _, out _),
            "the recently used tile stays");
        Assert.False(cache.TryFind(1, 1d, 1d, new Rect(10d, 10d, 50d, 50d), out _, out _),
            "the untouched tile is dropped");
    }

    public void AnOversizedTileIsNotRetained()
    {
        var cache = new PdfTileCache(2, 1_000);

        Assert.False(cache.Add(0, 1d, 1d, new Rect(0d, 0d, 100d, 100d), Bitmap(100, 100)),
            "a tile above the pixel budget is not kept");
        Assert.Equal(0, cache.Count, "and nothing is left behind");
    }

    public void ClearingDropsEveryTile()
    {
        var cache = new PdfTileCache(2, 32_000_000);
        cache.Add(0, 1d, 1d, new Rect(0d, 0d, 100d, 100d), Bitmap(10, 10));
        cache.Clear();

        Assert.Equal(0, cache.Count, "no tile survives the clear");
        Assert.Equal(0L, cache.Pixels, "and the pixel total is reset");
    }

    private static BitmapSource Bitmap(int width, int height)
    {
        var stride = width * 4;
        var image = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, new byte[stride * height], stride);
        image.Freeze();
        return image;
    }
}
