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
/// v6.1.8: the geometry of the sharp-tile overlay. The panel reports the
/// visible part in the base bitmap's DIP space; PDFium wants page units. These
/// conversions are kept separate from the control so the alignment arithmetic
/// can be tested without opening a window.
/// </summary>
internal static class PdfSharpTile
{
    /// <summary>
    /// Converts the visible rectangle of the displayed bitmap to page units.
    /// The bitmap's DIP size is the page size times its render factor.
    /// </summary>
    internal static Rect VisiblePageRect(Rect visibleSource, double bitmapFactor)
    {
        if (visibleSource.IsEmpty ||
            visibleSource.Width <= 0d || visibleSource.Height <= 0d ||
            bitmapFactor <= 0d || double.IsNaN(bitmapFactor))
            return Rect.Empty;

        return new Rect(
            visibleSource.X / bitmapFactor,
            visibleSource.Y / bitmapFactor,
            visibleSource.Width / bitmapFactor,
            visibleSource.Height / bitmapFactor);
    }

    /// <summary>
    /// Grows the visible page rectangle by a margin and clamps it to the page,
    /// so a small scroll stays inside the already rendered tile while the tile
    /// never asks PDFium for pixels outside the page.
    /// </summary>
    internal static Rect RegionFor(Rect visiblePage, Size pageSize, double margin)
        => RegionFor(visiblePage, pageSize, margin, 0d, default);

    /// <summary>
    /// The same region, stretched further in the direction the user has just
    /// panned. The next viewport is then already covered when the user keeps
    /// moving, instead of being repainted after every stop.
    /// </summary>
    internal static Rect RegionFor(
        Rect visiblePage, Size pageSize, double margin, double leadingMargin, Vector movement)
    {
        if (visiblePage.IsEmpty ||
            visiblePage.Width <= 0d || visiblePage.Height <= 0d ||
            pageSize.Width <= 0d || pageSize.Height <= 0d ||
            margin < 0d || double.IsNaN(margin) ||
            leadingMargin < 0d || double.IsNaN(leadingMargin))
            return Rect.Empty;

        var page = new Rect(0d, 0d, pageSize.Width, pageSize.Height);
        var left = margin + (movement.X < -0.01d ? leadingMargin : 0d);
        var right = margin + (movement.X > 0.01d ? leadingMargin : 0d);
        var top = margin + (movement.Y < -0.01d ? leadingMargin : 0d);
        var bottom = margin + (movement.Y > 0.01d ? leadingMargin : 0d);

        var expanded = new Rect(
            visiblePage.X - visiblePage.Width * left,
            visiblePage.Y - visiblePage.Height * top,
            visiblePage.Width * (1d + left + right),
            visiblePage.Height * (1d + top + bottom));
        var region = Rect.Intersect(page, expanded);

        return region.IsEmpty || region.Width <= 0d || region.Height <= 0d
            ? Rect.Empty
            : region;
    }

    /// <summary>Whether the tile already covers everything that is visible.</summary>
    internal static bool Covers(Rect tile, Rect visiblePage, double tolerance)
    {
        if (tile.IsEmpty || visiblePage.IsEmpty)
            return false;

        var slack = Math.Max(0d, tolerance);
        return visiblePage.X >= tile.X - slack &&
               visiblePage.Y >= tile.Y - slack &&
               visiblePage.Right <= tile.Right + slack &&
               visiblePage.Bottom <= tile.Bottom + slack;
    }
}
