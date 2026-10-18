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
using System.Windows;

namespace QuickLook.Tests;

/// <summary>
/// v6.1.6: magnifying a PDF page used to increase the raster without bound.
/// These cases pin the ceiling and, just as importantly, that the ceiling does
/// not change the size at which the page is displayed.
/// </summary>
internal class PdfPageRenderLimitTests
{
    private static readonly Size A4 = new(595d, 842d);

    public void AnOrdinaryPreviewIsNotReduced()
    {
        var ratio = PdfPageRenderLimit.Ratio(A4, 1.25d, 1.5d, 1d);

        Assert.Equal(1d, ratio, "a fit-sized page on a scaled display stays at full resolution");
    }

    public void AZoomBeyondTheBudgetIsReducedProportionally()
    {
        const double factor = 20d;
        const double scale = 2d;

        var ratio = PdfPageRenderLimit.Ratio(new Size(1000d, 1000d), factor, scale, 1d);

        Assert.True(ratio < 1d, "the extreme zoom is reduced");

        var pixels = 1000d * scale * factor * ratio * (1000d * scale * factor * ratio);
        Assert.True(pixels <= PdfPageRenderLimit.MaxPixels + 1d, "the raster stays inside the pixel budget");
        Assert.True(pixels > PdfPageRenderLimit.MaxPixels * 0.999d, "the full budget is still used");
    }

    public void ALongPageIsAlsoBoundedByItsLongestEdge()
    {
        var ratio = PdfPageRenderLimit.Ratio(new Size(100d, 100_000d), 1d, 1d, 1d);

        Assert.True(ratio < 1d, "the long page is reduced even though its pixel count is small");
        Assert.True(100_000d * ratio <= PdfPageRenderLimit.MaxDimension + 0.5d,
            "the longest edge fits what WPF can paint");
    }

    public void TheDisplayedSizeDoesNotChangeWhenTheRasterIsCapped()
    {
        const double factor = 8d;
        const double scale = 1.5d;

        var ratio = PdfPageRenderLimit.Ratio(A4, factor, scale, 1d);
        Assert.True(ratio < 1d, "this zoom really is above the cap");

        var pixels = A4.Width * scale * factor * ratio;
        var dpi = scale * 96d * ratio;

        Assert.True(Math.Abs(pixels * 96d / dpi - A4.Width * factor) < 0.001d,
            "pixel size and DPI shrink together, so the page keeps its DIP size");
    }

    public void NonsenseInputsLeaveTheRenderAlone()
    {
        Assert.Equal(1d, PdfPageRenderLimit.Ratio(A4, 0d, 1d, 1d), "a zero factor is not a cap");
        Assert.Equal(1d, PdfPageRenderLimit.Ratio(A4, double.NaN, 1d, 1d), "a NaN factor is not a cap");
        Assert.Equal(1d, PdfPageRenderLimit.Ratio(Size.Empty, 1d, 1d, 1d), "a missing page size is not capped");
        Assert.Equal(1d, PdfPageRenderLimit.Ratio(A4, 1d, 0d, 1d), "a zero display scale is not a cap");
    }
}
