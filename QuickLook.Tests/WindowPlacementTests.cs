// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using QuickLook.Common.Helpers;
using QuickLookNext.Helpers;
using System;
using System.Windows;

namespace QuickLook.Tests;

/// <summary>
/// v5.5.0: the "9/10 layout" that decides where a resized preview window lands. Upstream #827 was
/// a window that ended up wider than the screen it was placed on, so the invariant these tests
/// defend is: <b>clamp the size to the screen first, then place it - and the window is inside.</b>
///
/// The geometries come from the machine in that bug report (a 3840x2160 panel at 250% next to a
/// 1920x1080 screen at 100%), which is exactly what could not be exercised on a single screen.
/// </summary>
internal class WindowPlacementTests
{
    private static readonly DisplayDeviceHelper.ScaleFactor FullHd = new() { Horizontal = 1f, Vertical = 1f };

    private static readonly DisplayDeviceHelper.ScaleFactor FourK250 = new() { Horizontal = 2.5f, Vertical = 2.5f };

    public void AWindowInTheLeftThirdKeepsItsLeftEdge()
    {
        var monitor = new Rect(0, 0, 1920, 1080);

        var location = WindowPlacement.PlaceNearExisting(
            oldPx: new Rect(50, 100, 350, 300),
            newSizeDip: new Size(350, 300),
            FullHd,
            monitor);

        Assert.Equal(50d, location.X, "left edge kept");
        Assert.Equal(100d, location.Y, "top edge kept");
    }

    public void AWindowHangingOffTheRightEdgeIsPulledBackInside()
    {
        var monitor = new Rect(0, 0, 1920, 1080);

        var location = WindowPlacement.PlaceNearExisting(
            oldPx: new Rect(1700, 900, 400, 300), // right edge at 2100, below the bottom at 1200
            newSizeDip: new Size(400, 300),
            FullHd,
            monitor);

        Assert.Equal(1520d, location.X, "right edge lands on the monitor's right edge");
        Assert.Equal(780d, location.Y, "bottom edge lands on the monitor's bottom edge");
        Assert.True(location.X + 400d <= monitor.Right, "and the window is inside");
        Assert.True(location.Y + 300d <= monitor.Bottom, "vertically too");
    }

    /// <summary>
    /// The full pipeline for the reported bug: clamp the requested size to the screen, then place
    /// it - the window ends up completely inside, even when the content wanted to be much bigger.
    /// </summary>
    public void AClampedWindowIsPlacedCompletelyInsideTheScreen()
    {
        var monitorDip = new Size(1920, 1080);
        var monitor = new Rect(0, 0, monitorDip.Width, monitorDip.Height);

        var clamped = PreviewWindowSizing.ClampToDesktop(new Size(2600, 1400), monitorDip);
        var location = WindowPlacement.PlaceNearExisting(
            oldPx: new Rect(500, 300, 300, 200),
            newSizeDip: clamped,
            FullHd,
            monitor);

        Assert.Equal(0d, location.X, "pushed to the left edge");
        Assert.Equal(0d, location.Y, "and to the top");
        Assert.True(location.X + clamped.Width <= monitor.Right, "right side inside");
        Assert.True(location.Y + clamped.Height <= monitor.Bottom, "bottom side inside");
    }

    /// <summary>
    /// The mixed-DPI shape: the same picture is measured for the 250% 4K panel in DIP (so it comes
    /// out small in DIP but large in pixels), and for the 1080p screen in its own DIP. Whichever
    /// screen it lands on, the window stays inside that screen's pixels.
    /// </summary>
    public void TheSamePictureStaysInsideEachScreenItLandsOn()
    {
        var image = new Size(4289, 631);

        var fourKPx = new Rect(0, 0, 3840, 2160);
        var fourKDip = new Size(3840 / 2.5, 2160 / 2.5);
        var fittedForFourK = PreviewWindowSizing.FitWithin(image, fourKDip, 0.8d);
        var onFourK = WindowPlacement.PlaceNearExisting(
            oldPx: new Rect(1000, 700, 2000, 400), newSizeDip: fittedForFourK, FourK250, fourKPx);

        var fullHdPx = new Rect(3840, 0, 1920, 1080);
        var fullHdDip = new Size(1920, 1080);
        var fittedForFullHd = PreviewWindowSizing.FitWithin(image, fullHdDip, 0.8d);
        var onFullHd = WindowPlacement.PlaceNearExisting(
            oldPx: new Rect(3900, 300, 1000, 300), newSizeDip: fittedForFullHd, FullHd, fullHdPx);

        AssertInside(onFourK, fittedForFourK, FourK250, fourKPx, "4K at 250%");
        AssertInside(onFullHd, fittedForFullHd, FullHd, fullHdPx, "1080p at 100%");

        // The same picture is a different number of pixels on each screen - which is the whole
        // point of measuring in DIP and placing in pixels.
        Assert.True(fittedForFourK.Width * 2.5d > fittedForFullHd.Width,
            "the 250% panel gets more pixels for the same content");
    }

    private static void AssertInside(Point location, Size sizeDip,
        DisplayDeviceHelper.ScaleFactor scale, Rect monitorPx, string what)
    {
        var pxWidth = sizeDip.Width * scale.Horizontal;
        var pxHeight = sizeDip.Height * scale.Vertical;

        Assert.True(location.X >= monitorPx.Left - 0.01 && location.X + pxWidth <= monitorPx.Right + 0.01,
            $"{what}: horizontally inside (x={location.X:0.##}, w={pxWidth:0.##}, monitor={monitorPx.Width:0.##})");
        Assert.True(location.Y >= monitorPx.Top - 0.01 && location.Y + pxHeight <= monitorPx.Bottom + 0.01,
            $"{what}: vertically inside (y={location.Y:0.##}, h={pxHeight:0.##}, monitor={monitorPx.Height:0.##})");
    }
}
