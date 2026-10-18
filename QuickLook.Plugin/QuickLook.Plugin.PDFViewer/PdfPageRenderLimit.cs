// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Windows;

namespace QuickLook.Plugin.PDFViewer;

/// <summary>
/// v6.1.6: bounds the raster PDFium produces when a page is magnified.
/// <para>
/// A page is rendered at the view's zoom, so the fifth wheel notch on a large
/// page can ask for a bitmap of several hundred megapixels. PDFium allocates
/// that bitmap, and converting it to a WPF image copies it once more; that is
/// hundreds of megabytes for a view that only shows a handful of characters.
/// The raster is capped at the same 40 MP ceiling the image viewer uses. The
/// bitmap is then tagged with a proportionally lower DPI, which keeps its
/// displayed size unchanged - the view sees the same page, only magnified from
/// a smaller source once the cap is reached.
/// </para>
/// </summary>
internal static class PdfPageRenderLimit
{
    /// <summary>40 MP, i.e. ~160 MB per BGRA copy.</summary>
    internal const long MaxPixels = 40_000_000L;

    /// <summary>
    /// WPF cannot paint a texture wider or taller than this on common drivers.
    /// A very long page can stay under <see cref="MaxPixels"/> and still exceed
    /// it, so the longest edge is bounded separately.
    /// </summary>
    internal const int MaxDimension = 16_384;

    /// <summary>
    /// The proportion of the requested raster that may actually be rendered.
    /// Returns 1 when the page fits the budget. The ratio is applied to both the
    /// pixel size and the bitmap DPI, so the DIP size stays exactly what the
    /// viewer asked for.
    /// </summary>
    internal static double Ratio(Size pageSize, double factor, double scale, double density)
    {
        if (pageSize.Width <= 0d || pageSize.Height <= 0d ||
            factor <= 0d || double.IsNaN(factor) ||
            scale <= 0d || double.IsNaN(scale) ||
            density <= 0d || double.IsNaN(density))
            return 1d;

        var width = pageSize.Width * scale * density * factor;
        var height = pageSize.Height * scale * density * factor;
        if (double.IsInfinity(width) || double.IsInfinity(height))
            return 1d;

        var ratio = 1d;
        var pixels = width * height;
        if (pixels > MaxPixels)
            ratio = Math.Sqrt(MaxPixels / pixels);

        var longest = Math.Max(width, height) * ratio;
        if (longest > MaxDimension)
            ratio *= MaxDimension / longest;

        // A cap that removes less than a pixel's worth of detail is not worth a
        // different DPI tag.
        return ratio >= 0.999d ? 1d : Math.Max(ratio, 0.0001d);
    }
}
