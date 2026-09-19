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
using QuickLook.Common.Plugin;
using System;
using System.Windows;

namespace QuickLook.Tests;

/// <summary>
/// v5.0.10: the sizing rules behind QL-Win/QuickLook#827 - "the preview window
/// spreads across all monitors when a large image is previewed on the external
/// 1080p screen". The bug report came from a laptop with a 4K panel at 250%, a
/// second 4K panel at 175% and a 1920x1080 screen at 100%; these tests use those
/// two extremes, because the mixed scaling is what made the numbers disagree.
///
/// The fix has two halves and both are covered here: the size a plugin asks for
/// is measured against the screen the window is placed on, and whatever comes
/// out of that is clamped to that screen before the window is moved.
/// </summary>
internal class PreviewWindowSizingTests
{
    // 3840x2160 at 250% - the laptop panel, in DIP.
    private static readonly Size Screen4KAt250 = new(3840 / 2.5, 2160 / 2.5);

    // 1920x1080 at 100%, minus a taskbar's worth of work area.
    private static readonly Size ScreenFullHd = new(1920, 1040);

    public void ContentThatAlreadyFitsIsNotEnlarged()
    {
        var ratio = PreviewWindowSizing.FitRatio(new Size(800, 600), ScreenFullHd, 0.8d);

        Assert.Equal(1d, ratio, "an 800x600 preview needs no scaling on a 1080p screen");
    }

    public void ALandscapeImageIsLimitedByTheWidth()
    {
        // The shape from the bug report: 4289x631 spread across three monitors.
        var size = PreviewWindowSizing.FitWithin(new Size(4289, 631), ScreenFullHd, 0.8d);

        Assert.True(Math.Abs(size.Width - 1536d) < 0.01d,
            $"width takes 80% of the screen, got {size.Width:0.##}");
        Assert.True(Math.Abs(size.Height - 226d) < 0.5d,
            $"height follows the aspect ratio, got {size.Height:0.##}");
        Assert.True(size.Height < size.Width, "the image stays landscape");
    }

    public void APortraitImageIsLimitedByTheHeight()
    {
        var size = PreviewWindowSizing.FitWithin(new Size(2334, 3000), ScreenFullHd, 0.8d);

        Assert.True(Math.Abs(size.Height - 832d) < 0.01d,
            $"height takes 80% of the screen, got {size.Height:0.##}");
        Assert.True(size.Width < ScreenFullHd.Width, "a portrait page stays narrower than the screen");
    }

    public void AShareAboveOneMeansTheWholeScreen()
    {
        var ratio = PreviewWindowSizing.FitRatio(new Size(800, 600), Screen4KAt250, 5d);

        Assert.Equal(1d, ratio, "a share above 100% is clamped to 100%, never enlarging the content");
    }

    public void AnInvalidShareNeverProducesAnEmptyWindow()
    {
        var content = new Size(4289, 631);
        var expected = PreviewWindowSizing.FitWithin(content, ScreenFullHd, 1d);

        foreach (var share in new[] { 0d, -1d, double.NaN })
        {
            var size = PreviewWindowSizing.FitWithin(content, ScreenFullHd, share);

            Assert.True(size.Width > 0d && size.Height > 0d,
                $"share {share} must not collapse the preview to nothing");
            Assert.True(Math.Abs(size.Width - expected.Width) < 0.01d &&
                        Math.Abs(size.Height - expected.Height) < 0.01d,
                $"share {share} is treated as the whole screen");
        }
    }

    public void AnUnknownScreenLeavesTheContentAlone()
    {
        // A failed monitor query reports 0x0; shrinking against it would make
        // the preview vanish, so the content keeps its natural size instead.
        var size = PreviewWindowSizing.FitWithin(new Size(4289, 631), new Size(0, 0), 0.8d);

        Assert.Equal(4289d, size.Width, "width is untouched");
        Assert.Equal(631d, size.Height, "height is untouched");
    }

    public void AWindowThatFitsIsNotTouchedByTheClamp()
    {
        var size = PreviewWindowSizing.ClampToDesktop(new Size(1154, 770), Screen4KAt250);

        Assert.Equal(1154d, size.Width, "width is untouched");
        Assert.Equal(770d, size.Height, "height is untouched");
    }

    public void AWindowLargerThanTheScreenIsPulledBackOntoIt()
    {
        var size = PreviewWindowSizing.ClampToDesktop(new Size(2600, 1400), Screen4KAt250);

        Assert.Equal(1536d, size.Width, "width is clamped to the screen");
        Assert.Equal(864d, size.Height, "height is clamped to the screen");
    }

    public void TheClampDoesNotEnlargeTheUserSizeOnABiggerScreen()
    {
        var size = PreviewWindowSizing.ClampToDesktop(new Size(800, 600), ScreenFullHd);

        Assert.Equal(800d, size.Width, "width is untouched");
        Assert.Equal(600d, size.Height, "height is untouched");
    }

    /// <summary>
    /// The end-to-end shape of #827: a page that was measured and shown at its
    /// natural size on the 1080p screen is then displayed on the 250% 4K panel.
    /// In pixels it is taller than that panel, which is what "the window spreads
    /// across the monitors" looked like; the clamp keeps it on one screen.
    /// </summary>
    public void ASizeMeasuredForOneScreenStaysInsideTheScreenItLandsOn()
    {
        var requested = PreviewWindowSizing.FitWithin(new Size(1400, 1000), ScreenFullHd, 0.9d);

        Assert.True(Math.Abs(requested.Height - 936d) < 0.01d,
            $"the page takes 90% of the 1080p work area, got {requested.Height:0.##}");
        Assert.True(requested.Height * 2.5d > 2160d,
            "unclamped, that request is taller than the 4K panel in pixels");

        var placed = PreviewWindowSizing.ClampToDesktop(requested, Screen4KAt250);

        Assert.True(placed.Width <= Screen4KAt250.Width, "the window stays inside the screen width");
        Assert.True(placed.Height <= Screen4KAt250.Height, "the window stays inside the screen height");
        Assert.True(placed.Height * 2.5d <= 2160d, "and inside the panel in pixels as well");
    }

    public void TheContextMeasuresAgainstTheScreenTheHostProvides()
    {
        var context = new ContextObject { HostDesktopSize = ScreenFullHd };

        var ratio = context.SetPreferredSizeFit(new Size(4289, 631), 0.8d);

        Assert.True(Math.Abs(ratio - 0.358d) < 0.001d, $"ratio, got {ratio:0.####}");
        Assert.True(Math.Abs(context.PreferredSize.Width - 1536d) < 0.01d,
            $"width, got {context.PreferredSize.Width:0.##}");
        Assert.True(context.PreferredSize.Width < ScreenFullHd.Width,
            "the requested size fits the screen it was measured against");
    }

    public void TheContextFallsBackToTheCurrentDesktopWithoutAHostSize()
    {
        var context = new ContextObject();

        var ratio = context.SetPreferredSizeFit(new Size(4289, 631), 0.8d);

        Assert.True(ratio > 0d, "a ratio is still produced");
        Assert.True(context.PreferredSize.Width <= 4289d && context.PreferredSize.Height <= 631d,
            "the fallback never enlarges the content either");
    }
}
