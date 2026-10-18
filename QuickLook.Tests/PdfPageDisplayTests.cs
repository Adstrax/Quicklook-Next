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
/// v6.1.7: the page must keep the size the user asked for even when the bitmap
/// behind it is sharper (a cache hit or the first-page overscan). These cases
/// pin the conversion between the view factor and the bitmap's own factor.
/// </summary>
internal class PdfPageDisplayTests
{
    public void ASharperBitmapIsShownAtALowerZoom()
    {
        var zoom = PdfPageDisplay.ZoomFor(viewFactor: 1.1d, bitmapFactor: 1.5d);

        Assert.True(Math.Abs(zoom - 1.1d / 1.5d) < 1e-9, "the panel zooms the sharper bitmap down");
        Assert.True(Math.Abs(1.5d * zoom - 1.1d) < 1e-9, "the page still covers the requested view");
    }

    public void ABitmapAtTheViewFactorIsShownAtOneToOne()
    {
        Assert.Equal(1d, PdfPageDisplay.ZoomFor(1.5d, 1.5d), "no compensation is needed");
    }

    public void TheFirstPageOverscanIsCompensated()
    {
        var zoom = PdfPageDisplay.ZoomFor(viewFactor: 0.8d, bitmapFactor: 1d);

        Assert.True(Math.Abs(zoom - 0.8d) < 1e-9, "a 1.25x overscan is shown at 0.8x");
    }

    public void AViewAtFitIsRecognized()
    {
        Assert.True(PdfPageDisplay.IsAtFit(0.8d, 0.8d), "the same factor is fit");
        Assert.True(PdfPageDisplay.IsAtFit(0.81d, 0.8d), "a one-percent difference is still fit");
        Assert.False(PdfPageDisplay.IsAtFit(1.0d, 0.8d), "a zoomed view is not fit");
    }

    public void TheFitToleranceIsRelative()
    {
        Assert.True(PdfPageDisplay.IsAtFit(0.505d, 0.5d), "one percent is within the tolerance");
        Assert.False(PdfPageDisplay.IsAtFit(0.53d, 0.5d), "six percent is outside it");
    }

    public void NonsenseFactorsDoNotChangeTheZoom()
    {
        Assert.Equal(1d, PdfPageDisplay.ZoomFor(0d, 1d), "a missing view factor leaves 1:1");
        Assert.Equal(1d, PdfPageDisplay.ZoomFor(1d, double.NaN), "a missing bitmap factor leaves 1:1");
        Assert.False(PdfPageDisplay.IsAtFit(double.NaN, 1d), "an unknown view is not fit");
        Assert.False(PdfPageDisplay.IsAtFit(1d, 0d), "an unknown fit is not fit");
    }

    public void TheUserScrollWinsOverTheRequestTimePosition()
    {
        var requested = new Point(10d, 10d);

        Assert.Equal(new Point(10d, 220d),
            PdfPageDisplay.ScrollToRestore(requested, new Point(10d, 220d)),
            "a scroll during the render is kept");
        Assert.Equal(requested,
            PdfPageDisplay.ScrollToRestore(requested, new Point(10.4d, 10.4d)),
            "a sub-pixel difference is not a user scroll");
    }
}
