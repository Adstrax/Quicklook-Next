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
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.PDFViewer;

/// <summary>
/// v6.1.3: remembers the small page images in the thumbnail strip.
/// <para>
/// The strip is virtualized, so scrolling out of view and back recreates the
/// item and asks for its thumbnail again. Without a cache every return trip
/// rasterized the same pages again, which showed as a column of blank boxes
/// filling in while the list scrolled. Thumbnails are tiny, so the budget is
/// far smaller than the full-page cache's.
/// </para>
/// <para>All operations are thread safe; thumbnails are frozen bitmaps.</para>
/// </summary>
internal sealed class PdfThumbnailCache
{
    private const int MaxEntries = 300;
    private const long MaxBytes = 24L * 1024 * 1024;
    private const long MaxSingleBytes = 2L * 1024 * 1024;

    private sealed class Entry
    {
        internal int Page;
        internal double Scale;
        internal double PixelDensity;
        internal BitmapSource Image;
        internal long Bytes;
        internal long Used;
    }

    private readonly object _sync = new();
    private readonly List<Entry> _entries = [];
    private long _bytes;
    private long _clock;

    internal int Count
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    internal long Bytes
    {
        get
        {
            lock (_sync)
                return _bytes;
        }
    }

    internal BitmapSource Find(int page, double scale, double pixelDensity)
    {
        if (!IsValidResolution(scale, pixelDensity))
            return null;

        lock (_sync)
        {
            foreach (var entry in _entries)
            {
                if (entry.Page != page ||
                    !ScaleMatches(entry.Scale, scale) ||
                    !ScaleMatches(entry.PixelDensity, pixelDensity))
                    continue;

                entry.Used = ++_clock;
                return entry.Image;
            }
        }

        return null;
    }

    /// <summary>Retains a thumbnail; unfrozen or oversized images are refused.</summary>
    internal bool Add(int page, double scale, double pixelDensity, BitmapSource image)
    {
        if (image is null || !image.IsFrozen || !IsValidResolution(scale, pixelDensity))
            return false;

        var bytes = PdfPageBitmapCache.EstimateBytes(image);
        if (bytes <= 0 || bytes > MaxSingleBytes)
            return false;

        lock (_sync)
        {
            // A second render of the same page replaces the first one.
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Page != page)
                    continue;

                RemoveAt(i);
            }

            _entries.Add(new Entry
            {
                Page = page,
                Scale = scale,
                PixelDensity = pixelDensity,
                Image = image,
                Bytes = bytes,
                Used = ++_clock,
            });
            _bytes += bytes;

            Trim();
            return true;
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _bytes = 0;
            _clock = 0;
        }
    }

    private void Trim()
    {
        while (_entries.Count > MaxEntries || _bytes > MaxBytes)
        {
            var oldest = 0;
            for (var i = 1; i < _entries.Count; i++)
            {
                if (_entries[i].Used < _entries[oldest].Used)
                    oldest = i;
            }

            RemoveAt(oldest);
        }
    }

    private void RemoveAt(int index)
    {
        _bytes -= _entries[index].Bytes;
        _entries.RemoveAt(index);
    }

    private static bool IsValidResolution(double scale, double pixelDensity)
        => scale > 0d && !double.IsNaN(scale) &&
           pixelDensity > 0d && !double.IsNaN(pixelDensity);

    private static bool ScaleMatches(double left, double right)
        => Math.Abs(left - right) <= Math.Max(left, right) * 0.01d;
}
