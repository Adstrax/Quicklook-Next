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
/// v6.1.3: keeps recently rasterized pages as frozen bitmaps so going back to a
/// page the user has already seen does not rasterize it again.
/// <para>
/// The previous model kept exactly one page (<c>_renderedPage</c>), so flipping
/// forward and back rendered every page twice and, for a large drawing, showed
/// the page arrive in two visible steps. Entries are keyed by the file's length
/// and timestamp as well as the page number, so an edited file cannot serve an
/// old page, and by the display scale, so a bitmap rendered for another monitor
/// is never reused and shown soft.
/// </para>
/// <para>
/// The budget is counted in decoded pixels, not compressed bytes: a page is
/// several megabytes in memory whatever its PDF stream size. Pages larger than
/// the single-entry limit are still shown, they are simply not retained.
/// </para>
/// <para>
/// v6.1.5: one page is kept at one sharpness only. Zooming through several
/// steps used to leave every rasterization behind, so a single page could take
/// all eight slots and evict the neighbouring pages that page flipping needs.
/// A sharper render now replaces every render it already covers, while a
/// sharper bitmap already in the cache is never dropped for a smaller one.
/// </para>
/// </summary>
internal sealed class PdfPageBitmapCache
{
    /// <summary>Same slack the viewer uses for "already detailed enough".</summary>
    private const double FactorTolerance = 0.02d;

    /// <summary>
    /// Renders within this relative distance are the same zoom level, so the
    /// newer one replaces the older instead of keeping both.
    /// </summary>
    private const double ReplacementTolerance = 0.01d;

    /// <summary>Display scale must match within this relative error.</summary>
    private const double ScaleTolerance = 0.01d;

    /// <summary>
    /// Shared across previews: reopening the same PDF reuses its pages. The
    /// budget is deliberately modest - a rasterized page costs several megabytes
    /// - so the cache cannot become the reason the idle previewer holds memory.
    /// </summary>
    internal static readonly PdfPageBitmapCache Shared =
        new(maxEntries: 8, maxBytes: 64L * 1024 * 1024, maxSingleBytes: 32L * 1024 * 1024);

    private sealed class Entry
    {
        internal string Path;
        internal long Length;
        internal long Ticks;
        internal int Page;
        internal double Factor;
        internal double Scale;
        internal BitmapSource Image;
        internal long Bytes;
        internal long Used;
    }

    private readonly object _sync = new();
    private readonly List<Entry> _entries = [];
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly long _maxSingleBytes;
    private long _bytes;
    private long _clock;

    internal PdfPageBitmapCache(int maxEntries, long maxBytes, long maxSingleBytes)
    {
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxSingleBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSingleBytes));

        _maxEntries = maxEntries;
        _maxBytes = Math.Max(maxBytes, maxSingleBytes);
        _maxSingleBytes = maxSingleBytes;
    }

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

    /// <summary>
    /// Returns the least detailed cached bitmap that is still at least as sharp
    /// as <paramref name="factor"/> asks for, or null. Reusing a sharper bitmap
    /// is what keeps the page from going soft when the user zooms back out.
    /// </summary>
    internal BitmapSource Find(
        string filePath,
        long fileLength,
        long fileTicks,
        int page,
        double factor,
        double scale,
        out double usedFactor)
    {
        usedFactor = double.NaN;

        if (string.IsNullOrEmpty(filePath) ||
            factor <= 0d || double.IsNaN(factor) || scale <= 0d || double.IsNaN(scale))
            return null;

        lock (_sync)
        {
            Entry best = null;
            foreach (var entry in _entries)
            {
                if (entry.Page != page ||
                    !string.Equals(entry.Path, filePath, StringComparison.OrdinalIgnoreCase) ||
                    entry.Length != fileLength ||
                    entry.Ticks != fileTicks)
                    continue;

                if (!ScaleMatches(entry.Scale, scale))
                    continue;

                if (entry.Factor < factor * (1d - FactorTolerance))
                    continue;

                if (best is null || entry.Factor < best.Factor)
                    best = entry;
            }

            if (best is null)
                return null;

            best.Used = ++_clock;
            usedFactor = best.Factor;
            return best.Image;
        }
    }

    /// <summary>
    /// Retains a freshly rendered page. Returns false when the bitmap is not
    /// usable from other threads or is too large to be worth keeping; the caller
    /// still shows the page, it just cannot be reused later.
    /// </summary>
    internal bool Add(
        string filePath,
        long fileLength,
        long fileTicks,
        int page,
        double factor,
        double scale,
        BitmapSource image)
    {
        if (string.IsNullOrEmpty(filePath) || image is null || !image.IsFrozen)
            return false;
        if (factor <= 0d || double.IsNaN(factor) || scale <= 0d || double.IsNaN(scale))
            return false;

        var bytes = EstimateBytes(image);
        if (bytes <= 0 || bytes > _maxSingleBytes)
            return false;

        lock (_sync)
        {
            // Keep only the sharpest rasterization of this page. Every entry
            // at or below the new factor is redundant: Find would either pick
            // the new bitmap or a sharper one that is still there. A sharper
            // entry than the new render is kept, because zooming back out can
            // reuse it without calling PDFium again.
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var existing = _entries[i];
                if (existing.Page != page ||
                    !string.Equals(existing.Path, filePath, StringComparison.OrdinalIgnoreCase) ||
                    existing.Length != fileLength ||
                    existing.Ticks != fileTicks ||
                    !ScaleMatches(existing.Scale, scale))
                    continue;

                if (existing.Factor > factor * (1d + ReplacementTolerance))
                    continue;

                RemoveAt(i);
            }

            _entries.Add(new Entry
            {
                Path = filePath,
                Length = fileLength,
                Ticks = fileTicks,
                Page = page,
                Factor = factor,
                Scale = scale,
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

    internal static long EstimateBytes(BitmapSource image)
    {
        if (image is null || image.PixelWidth <= 0 || image.PixelHeight <= 0)
            return 0;

        var bytesPerPixel = Math.Max(1, (image.Format.BitsPerPixel + 7) / 8);
        return (long)image.PixelWidth * image.PixelHeight * bytesPerPixel;
    }

    /// <summary>
    /// Whether a page of this pixel size could be kept at all. Prefetching asks
    /// first so it never spends a render on a page the cache would refuse.
    /// </summary>
    internal bool CanRetain(long pixelWidth, long pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return false;

        // 4 bytes per pixel is the widest format Pdfium hands back; using it
        // here errs on the side of not prefetching.
        return pixelWidth * pixelHeight * 4L <= _maxSingleBytes;
    }

    private void Trim()
    {
        while (_entries.Count > _maxEntries || _bytes > _maxBytes)
        {
            var oldest = -1;
            for (var i = 0; i < _entries.Count; i++)
            {
                if (oldest < 0 || _entries[i].Used < _entries[oldest].Used)
                    oldest = i;
            }

            // The just-added entry is never the oldest, so this loop always
            // makes progress unless a single entry exceeds the byte budget,
            // which Add has already refused.
            if (oldest < 0)
                return;

            RemoveAt(oldest);
        }
    }

    private void RemoveAt(int index)
    {
        _bytes -= _entries[index].Bytes;
        _entries.RemoveAt(index);
    }

    private static bool ScaleMatches(double left, double right)
        => Math.Abs(left - right) <= Math.Max(left, right) * ScaleTolerance;
}
