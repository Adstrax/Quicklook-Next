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

namespace QuickLook.Tests;

/// <summary>
/// v6.1.8: the sharp tile must be laid over exactly the part of the page the
/// base bitmap is showing. These cases pin the conversions behind that overlay.
/// </summary>
internal class PdfSharpTileTests
{
    public void TheVisibleBitmapRectBecomesPageUnits()
    {
        var page = PdfSharpTile.VisiblePageRect(new Rect(200d, 100d, 400d, 300d), 2d);

        Assert.Equal(new Rect(100d, 50d, 200d, 150d), page,
            "the bitmap's DIP space is the page times its render factor");
    }

    public void TheTileGrowsAroundTheVisibleArea()
    {
        var region = PdfSharpTile.RegionFor(
            new Rect(100d, 100d, 100d, 100d), new Size(1000d, 1000d), 0.1d);

        Assert.Equal(new Rect(90d, 90d, 120d, 120d), region,
            "a ten percent margin is added on every side");
    }

    public void TheTileIsClampedToThePage()
    {
        var region = PdfSharpTile.RegionFor(
            new Rect(0d, 0d, 100d, 100d), new Size(1000d, 1000d), 0.5d);

        Assert.Equal(new Rect(0d, 0d, 150d, 150d), region,
            "no part of the tile lies outside the page");
    }

    public void TheTileLeansTowardTheDirectionOfTravel()
    {
        var view = new Rect(100d, 100d, 100d, 100d);
        var page = new Size(1000d, 1000d);

        Assert.Equal(new Rect(90d, 90d, 160d, 120d),
            PdfSharpTile.RegionFor(view, page, 0.1d, 0.4d, new Vector(1d, 0d)),
            "moving right leaves room on the right");
        Assert.Equal(new Rect(50d, 90d, 160d, 120d),
            PdfSharpTile.RegionFor(view, page, 0.1d, 0.4d, new Vector(-1d, 0d)),
            "moving left leaves room on the left");
        Assert.Equal(new Rect(90d, 90d, 160d, 160d),
            PdfSharpTile.RegionFor(view, page, 0.1d, 0.4d, new Vector(1d, 1d)),
            "a diagonal drag grows both leading sides");
    }

    public void ATileThatCoversTheViewNeedsNoRerender()
    {
        var tile = new Rect(0d, 0d, 400d, 400d);

        Assert.True(PdfSharpTile.Covers(tile, new Rect(0.2d, 0.2d, 300d, 300d), 0.5d),
            "a sub-pixel offset is still covered");
        Assert.False(PdfSharpTile.Covers(tile, new Rect(0d, 0d, 500d, 300d), 0.5d),
            "a viewport that leaves the tile needs a new one");
    }

    public void InvalidGeometryYieldsNothing()
    {
        Assert.True(PdfSharpTile.VisiblePageRect(Rect.Empty, 1d).IsEmpty, "no visible area, no tile");
        Assert.True(PdfSharpTile.VisiblePageRect(new Rect(0d, 0d, 10d, 10d), 0d).IsEmpty,
            "a missing bitmap factor cannot be converted");
        Assert.True(PdfSharpTile.RegionFor(Rect.Empty, new Size(10d, 10d), 0.1d).IsEmpty,
            "an empty view has no region");
        Assert.False(PdfSharpTile.Covers(Rect.Empty, new Rect(0d, 0d, 1d, 1d), 0d),
            "an empty tile covers nothing");
    }
}
