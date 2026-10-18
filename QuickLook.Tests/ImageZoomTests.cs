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
using System;

namespace QuickLook.Tests;

/// <summary>
/// v5.6.6: the image viewer's wheel zoom. The rules are about feel - a notch has to be the same
/// change whatever the picture's size, and the fit / 1:1 landmarks must stay reachable without a
/// jump - so they are pinned here rather than left to a mouse and a screenshot.
/// </summary>
internal class ImageZoomTests
{
    public void AStepIsTheSameRatioWhereverItStarts()
    {
        Assert.True(Math.Abs(ImageZoom.Step(1d, 1) - 1.1d) < 0.0001, "one notch from 100%");
        Assert.True(Math.Abs(ImageZoom.Step(0.3d, 1) - 0.33d) < 0.0001, "one notch from a small fit factor");
        Assert.True(Math.Abs(ImageZoom.Step(4d, 1) - 4.4d) < 0.0001, "one notch from 400%");

        // Out is the mirror of in.
        Assert.True(Math.Abs(ImageZoom.Step(4.4d, -1) - 4d) < 0.0001, "one notch back out");

        // High-resolution wheels and touchpads send fractions of a notch.
        Assert.True(Math.Abs(ImageZoom.Step(1d, 0.5) - Math.Sqrt(1.1d)) < 0.0001, "half a notch");
    }

    public void ANotchThatArrivesAtALandmarkLandsOnIt()
    {
        // 0.95 -> 1.045 crosses 100% and was heading there: land on exactly 100%.
        Assert.True(Math.Abs(ImageZoom.LandOnLandmark(0.95d, 1.045d, 1d) - 1d) < 0.0001, "just past 100%");
        Assert.True(Math.Abs(ImageZoom.LandOnLandmark(1.05d, 0.955d, 1d) - 1d) < 0.0001, "just below, zooming out");

        // Same for the fit landmark.
        Assert.True(Math.Abs(ImageZoom.LandOnLandmark(0.29d, 0.32d, 0.3d) - 0.3d) < 0.0001, "arriving at fit");
    }

    public void ANotchThatWouldTeleportPastALandmarkOnlyStepsTowardsIt()
    {
        // This is the case that made zooming feel different per picture: a large image whose fit
        // factor is 0.3 used to jump straight to 100% on the first notch.
        var zoomed = ImageZoom.LandOnLandmark(0.3d, 0.33d, 1d);
        Assert.True(Math.Abs(zoomed - 0.33d) < 0.0001, "the landmark is not even crossed");

        // A jump that does cross it (a pinch, or a fast wheel) is shortened instead of landing.
        var clamped = ImageZoom.LandOnLandmark(0.5d, 1.5d, 1d);
        Assert.True(clamped > 1d && clamped < 1.5d, "stepped past 100% without teleporting to it");
        Assert.True(Math.Abs(clamped - ImageZoom.LandmarkLandingRatio) < 0.0001, "and by no more than the landing ratio");
    }

    public void AFactorThatIsNotNearALandmarkIsLeftAlone()
    {
        Assert.True(Math.Abs(ImageZoom.LandOnLandmark(2d, 2.2d, 1d) - 2.2d) < 0.0001, "above 100%");
        Assert.True(Math.Abs(ImageZoom.LandOnLandmark(0.5d, 0.45d, 0.3d) - 0.45d) < 0.0001, "approaching fit but not crossing");
    }

    public void NonsenseInputsDoNotProduceNonsenseFactors()
    {
        Assert.Equal(1.5d, ImageZoom.LandOnLandmark(1d, 1.5d, 0d), "a zero landmark is ignored");
        Assert.Equal(1.5d, ImageZoom.LandOnLandmark(1d, 1.5d, double.NaN), "so is a NaN landmark");
        // A NaN request passes through so the caller can decide; the viewer drops it.
        Assert.True(double.IsNaN(ImageZoom.LandOnLandmark(1d, double.NaN, 1d)), "a NaN request passes through");
        Assert.Equal(0d, ImageZoom.Step(0d, 3), "a zero factor stays zero");
    }

    public void FitUsesTheSameViewportForMeasuringAndRendering()
    {
        Assert.True(Math.Abs(ImageZoom.FitFactor(
                new System.Windows.Size(800, 1000),
                new System.Windows.Size(600, 800)) - 1.25d) < 0.0001,
            "the narrower axis decides the fit");

        Assert.Equal(1d, ImageZoom.FitFactor(
                new System.Windows.Size(0, 1000),
                new System.Windows.Size(600, 800)),
            "an unmeasured viewport does not produce a zero scale");
    }

    public void MagnifyingPastTheDecodedDetailAsksForMore()
    {
        var viewport = new System.Windows.Size(800, 600);
        var real = new System.Windows.Size(8000, 6000);
        var decoded = new System.Windows.Size(800, 600);

        // Fit, 100% DPI: the decode matches what is on screen, so nothing to do.
        Assert.Null(ImageZoom.NeededDetailSize(viewport, 1d, 1d, 1d, real, decoded),
            "nothing to gain at the size already decoded");

        // 400%: the pixels on screen are 4x what was decoded.
        var needed = ImageZoom.NeededDetailSize(viewport, 4d, 1d, 1d, real, decoded);
        Assert.NotNull(needed, "more detail is worth asking for");
        Assert.Equal(3200d, needed.Value.Width, "the width being looked at");
        Assert.Equal(2400d, needed.Value.Height, "and the height");
    }

    public void TheFileIsTheCeilingForDetail()
    {
        var viewport = new System.Windows.Size(800, 600);
        var real = new System.Windows.Size(2000, 1500);

        // 800% of an 800x600 window would be 6400x4800, but the file only has 2000x1500.
        var needed = ImageZoom.NeededDetailSize(viewport, 8d, 1d, 1d, real,
            new System.Windows.Size(800, 600));

        Assert.NotNull(needed, "the file can still give more");
        Assert.Equal(2000d, needed.Value.Width, "bounded by the file's own width");
        Assert.Equal(1500d, needed.Value.Height, "and height");
    }

    public void ASmallerGainIsNotWorthADecode()
    {
        var viewport = new System.Windows.Size(800, 600);
        var real = new System.Windows.Size(8000, 6000);

        // Asked for 10% more than what is loaded: below the growth threshold.
        Assert.Null(ImageZoom.NeededDetailSize(viewport, 1.1d, 1d, 1d, real,
            new System.Windows.Size(800, 600)), "10% more is not worth a decode");

        // 30% more is.
        Assert.NotNull(ImageZoom.NeededDetailSize(viewport, 1.3d, 1d, 1d, real,
            new System.Windows.Size(800, 600)), "30% more is");
    }

    public void DisplayScalingCountsAsDetail()
    {
        var viewport = new System.Windows.Size(800, 600);
        var real = new System.Windows.Size(8000, 6000);

        // At 200% display scaling the same window shows twice as many device pixels.
        var needed = ImageZoom.NeededDetailSize(viewport, 1d, 2d, 2d, real,
            new System.Windows.Size(800, 600));

        Assert.NotNull(needed, "display scaling is detail too");
        Assert.Equal(1600d, needed.Value.Width, "device pixels, not DIP");
        Assert.Equal(1200d, needed.Value.Height, "and its height");
    }

    public void AThumbnailKeepsItsOwnCoordinateSpace()
    {
        var source = new System.Windows.Size(1200, 900);
        var real = new System.Windows.Size(8000, 6000);

        Assert.Null(ImageZoom.NeededDetailSizeForSource(source, 1d, 1d, 1d, real),
            "the first thumbnail already covers the fit view");

        var needed = ImageZoom.NeededDetailSizeForSource(source, 2d, 1d, 1d, real);
        Assert.NotNull(needed, "two times the thumbnail is worth a decode");
        Assert.Equal(2400d, needed.Value.Width, "twice the source width");
        Assert.Equal(1800d, needed.Value.Height, "and height");
    }

    public void TheFileIsTheCeilingForTheSourceSpace()
    {
        var source = new System.Windows.Size(800, 600);
        var real = new System.Windows.Size(2000, 1500);

        var needed = ImageZoom.NeededDetailSizeForSource(source, 4d, 1d, 1d, real);

        Assert.NotNull(needed, "the file can still give more");
        Assert.Equal(2000d, needed.Value.Width, "bounded by the file's own width");
        Assert.Equal(1500d, needed.Value.Height, "and height");
    }

    public void ASmallGainIsNotWorthASecondDecode()
    {
        var source = new System.Windows.Size(1200, 900);
        var real = new System.Windows.Size(8000, 6000);

        Assert.Null(ImageZoom.NeededDetailSizeForSource(source, 1.1d, 1d, 1d, real),
            "10% more than the loaded bitmap is not worth a decode");
        Assert.NotNull(ImageZoom.NeededDetailSizeForSource(source, 1.3d, 1d, 1d, real),
            "30% more is");
    }

    public void DisplayScalingCountsForTheSourceSpaceToo()
    {
        var source = new System.Windows.Size(800, 600);
        var real = new System.Windows.Size(8000, 6000);

        var needed = ImageZoom.NeededDetailSizeForSource(source, 1.5d, 2d, 2d, real);

        Assert.NotNull(needed, "the device pixels are what matter");
        Assert.Equal(2400d, needed.Value.Width, "800 * 1.5 * 2");
        Assert.Equal(1800d, needed.Value.Height, "and its height");
    }

    public void ANewDecodeKeepsTheVisibleSize()
    {
        var oldSource = new System.Windows.Size(1200, 900);
        var newSource = new System.Windows.Size(2400, 1800);

        var newZoom = ImageZoom.ZoomAfterSourceChange(2d, oldSource, newSource);

        Assert.True(Math.Abs(newZoom - 1d) < 0.0001, "a doubled bitmap halves the zoom");
        Assert.True(Math.Abs(oldSource.Width * 2d - newSource.Width * newZoom) < 0.0001,
            "the layout width does not change");
    }

    public void AnInvalidSourceChangeLeavesTheZoomAlone()
    {
        Assert.Equal(2d, ImageZoom.ZoomAfterSourceChange(
            2d, new System.Windows.Size(1200, 900), new System.Windows.Size(0, 0)),
            "a zero-sized new source is ignored");
        Assert.Equal(2d, ImageZoom.ZoomAfterSourceChange(
            2d, new System.Windows.Size(0, 0), new System.Windows.Size(1200, 900)),
            "a zero-sized old source is ignored");
    }

    public void TheFirstDecodeCoversTheDevicePixelsOnScreen()
    {
        var preferredDip = new System.Windows.Size(800, 600);
        var decodedPixels = ImageZoom.ToDevicePixels(preferredDip, 1.5d, 1.5d);

        Assert.Equal(1200d, decodedPixels.Width, "the monitor shows 1.5 physical pixels per DIP");
        Assert.Equal(900d, decodedPixels.Height, "in both directions");

        // DpiHack stores the bitmap at the display scale, so its WPF size is back in DIP.
        var sourceDip = new System.Windows.Size(
            decodedPixels.Width / 1.5d,
            decodedPixels.Height / 1.5d);

        Assert.Null(ImageZoom.NeededDetailSizeForSource(
                sourceDip, 1d, 1.5d, 1.5d, new System.Windows.Size(8000, 6000)),
            "the fit view no longer needs a second decode just to become sharp");
    }

    public void AnInvalidDisplayScaleLeavesTheDecodeSizeAlone()
    {
        var display = new System.Windows.Size(800, 600);

        Assert.Equal(display, ImageZoom.ToDevicePixels(display, 0d, 1.5d),
            "zero scale is ignored");
        Assert.Equal(display, ImageZoom.ToDevicePixels(display, double.NaN, 1.5d),
            "NaN scale is ignored");
        Assert.Equal(System.Windows.Size.Empty, ImageZoom.ToDevicePixels(
            System.Windows.Size.Empty, 2d, 2d), "an empty request stays empty");
    }
}
