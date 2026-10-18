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
using System.Windows;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.PDFViewer;

/// <summary>
/// v6.1.9: keeps the last couple of sharp tiles so panning back over a region
/// the user has just seen does not call PDFium again. A tile is only reused for
/// the same page, zoom and display scale when it still covers everything that
/// is visible. The budget is in pixels, because a tile is a frozen bitmap
/// whatever its compressed size would have been.
/// </summary>
internal sealed class PdfTileCache
{
    private const double FactorTolerance = 0.01d;
    private const double ScaleTolerance = 0.01d;

    private sealed class Entry
    {
        internal int Page;
        internal double ViewFactor;
        internal double Scale;
        internal Rect Region;
        internal BitmapSource Image;
        internal long Pixels;
        internal long Used;
    }

    private readonly object _sync = new();
    private readonly List<Entry> _entries = [];
    private readonly int _maxEntries;
    private readonly long _maxPixels;
    private long _pixels;
    private long _clock;

    internal PdfTileCache(int maxEntries, long maxPixels)
    {
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPixels));

        _maxEntries = maxEntries;
        _maxPixels = maxPixels;
    }

    internal int Count
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    internal long Pixels
    {
        get
        {
            lock (_sync)
                return _pixels;
        }
    }

    internal bool TryFind(
        int page,
        double viewFactor,
        double scale,
        Rect visiblePage,
        out BitmapSource image,
        out Rect region)
    {
        image = null;
        region = Rect.Empty;

        if (visiblePage.IsEmpty || viewFactor <= 0d || double.IsNaN(viewFactor) ||
            scale <= 0d || double.IsNaN(scale))
            return false;

        lock (_sync)
        {
            Entry best = null;
            foreach (var entry in _entries)
            {
                if (entry.Page != page ||
                    !Close(entry.ViewFactor, viewFactor, FactorTolerance) ||
                    !Close(entry.Scale, scale, ScaleTolerance) ||
                    !PdfSharpTile.Covers(entry.Region, visiblePage, 0.5d))
                    continue;

                if (best is null || entry.Used > best.Used)
                    best = entry;
            }

            if (best is null)
                return false;

            best.Used = ++_clock;
            image = best.Image;
            region = best.Region;
            return true;
        }
    }

    internal bool Add(
        int page,
        double viewFactor,
        double scale,
        Rect region,
        BitmapSource image)
    {
        if (image is null || !image.IsFrozen || region.IsEmpty ||
            viewFactor <= 0d || double.IsNaN(viewFactor) ||
            scale <= 0d || double.IsNaN(scale))
            return false;

        var pixels = (long)image.PixelWidth * image.PixelHeight;
        if (pixels <= 0 || pixels > _maxPixels)
            return false;

        lock (_sync)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var existing = _entries[i];
                if (existing.Page != page ||
                    !Close(existing.ViewFactor, viewFactor, FactorTolerance) ||
                    !Close(existing.Scale, scale, ScaleTolerance) ||
                    !RegionsMatch(existing.Region, region))
                    continue;

                RemoveAt(i);
            }

            _entries.Add(new Entry
            {
                Page = page,
                ViewFactor = viewFactor,
                Scale = scale,
                Region = region,
                Image = image,
                Pixels = pixels,
                Used = ++_clock,
            });
            _pixels += pixels;

            Trim();
            return true;
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _pixels = 0;
            _clock = 0;
        }
    }

    private void Trim()
    {
        while (_entries.Count > _maxEntries || _pixels > _maxPixels)
        {
            var oldest = -1;
            for (var i = 0; i < _entries.Count; i++)
            {
                if (oldest < 0 || _entries[i].Used < _entries[oldest].Used)
                    oldest = i;
            }

            if (oldest < 0)
                return;

            RemoveAt(oldest);
        }
    }

    private void RemoveAt(int index)
    {
        _pixels -= _entries[index].Pixels;
        _entries.RemoveAt(index);
    }

    private static bool Close(double left, double right, double tolerance)
        => Math.Abs(left - right) <= Math.Max(Math.Abs(left), Math.Abs(right)) * tolerance;

    private static bool RegionsMatch(Rect left, Rect right)
        => Math.Abs(left.X - right.X) <= 0.5d &&
           Math.Abs(left.Y - right.Y) <= 0.5d &&
           Math.Abs(left.Width - right.Width) <= 0.5d &&
           Math.Abs(left.Height - right.Height) <= 0.5d;
}
