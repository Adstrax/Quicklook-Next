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
/// v6.1.7: the arithmetic that keeps the page at the size the user asked for.
/// <para>
/// A cached page can be sharper than the current view, and the first page is
/// rendered slightly larger than the fit. In both cases the bitmap is displayed
/// at a zoom other than 1:1. The zoom has to be the view factor divided by the
/// bitmap's own factor, or the page changes size when a sharper cache entry is
/// reused. Keeping that calculation here also makes the state the viewer stores
/// unambiguous: it remembers the bitmap's factor, while the panel remembers the
/// relative zoom.
/// </para>
/// </summary>
internal static class PdfPageDisplay
{
    /// <summary>Same slack the viewer uses for "this is the fit factor".</summary>
    internal const double FitTolerance = 0.02d;

    /// <summary>Scroll offsets closer than this are the same position.</summary>
    private const double MovementTolerance = 1d;

    /// <summary>
    /// The zoom that makes a bitmap rendered at <paramref name="bitmapFactor"/>
    /// cover the view at <paramref name="viewFactor"/>.
    /// </summary>
    internal static double ZoomFor(double viewFactor, double bitmapFactor)
    {
        if (viewFactor <= 0d || double.IsNaN(viewFactor) ||
            bitmapFactor <= 0d || double.IsNaN(bitmapFactor))
            return 1d;

        return viewFactor / bitmapFactor;
    }

    /// <summary>Whether the view is at the page's fit factor.</summary>
    internal static bool IsAtFit(double viewFactor, double fitFactor)
    {
        if (viewFactor <= 0d || double.IsNaN(viewFactor) ||
            fitFactor <= 0d || double.IsNaN(fitFactor))
            return false;

        return Math.Abs(viewFactor - fitFactor) <= fitFactor * FitTolerance;
    }

    /// <summary>
    /// A render completes asynchronously, so the user may have scrolled while
    /// it was in flight. Restoring the position captured at request time would
    /// pull the page back; the position the user moved to wins.
    /// </summary>
    internal static Point ScrollToRestore(Point requested, Point current)
    {
        var moved = Math.Abs(current.X - requested.X) > MovementTolerance ||
                    Math.Abs(current.Y - requested.Y) > MovementTolerance;

        return moved ? current : requested;
    }
}
