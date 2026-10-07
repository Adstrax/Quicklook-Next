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

using System;
using System.Windows;

namespace QuickLook.Common.Helpers;

/// <summary>
/// v5.6.6: the arithmetic behind the image viewer's wheel zoom.
///
/// <para>
/// Two rules, and both exist because of how the zoom *feels* rather than what it computes:
/// the step is relative (so a notch is the same change at 10% and at 800%), and the two
/// landmarks - fit-to-window and 1:1 - are landed on instead of jumped over. The old
/// implementation snapped onto a landmark from wherever the notch started, so one notch could
/// go from a large image's 0.3 fit straight to 100%, while a picture that already sat near
/// 1:1 barely moved; that is why the same gesture felt different from picture to picture.
/// </para>
/// </summary>
public static class ImageZoom
{
    /// <summary>One wheel notch changes the factor by this ratio.</summary>
    public const double StepRatio = 1.1d;

    /// <summary>
    /// How much a notch may overshoot a landmark and still land on it. Bigger than 1 so the
    /// landmarks stay reachable with a wheel (a notch that arrives just past 100% still lands on
    /// it), small enough that nothing teleports across them.
    /// </summary>
    public const double LandmarkLandingRatio = 1.3d;

    /// <summary>
    /// The factor <paramref name="notches"/> wheel notches away from <paramref name="current"/>.
    /// Fractional notches are allowed: high-resolution wheels and touchpads send deltas that are
    /// not multiples of 120.
    /// </summary>
    public static double Step(double current, double notches)
    {
        if (current <= 0 || double.IsNaN(current))
            return current;

        return current * Math.Pow(StepRatio, notches);
    }

    /// <summary>
    /// The factor to use when a step from <paramref name="current"/> to <paramref name="requested"/>
    /// crosses <paramref name="landmark"/>. Landing on the landmark is kept, but only when the step
    /// was heading there anyway; a step that would overshoot by more than
    /// <see cref="LandmarkLandingRatio"/> is shortened to that much of the way instead.
    /// </summary>
    public static double LandOnLandmark(double current, double requested, double landmark)
    {
        if (landmark <= 0 || double.IsNaN(landmark) || double.IsNaN(requested))
            return requested;

        var crossing = current < landmark && requested > landmark
                       || current > landmark && requested < landmark;
        if (!crossing)
            return requested;

        var overshoot = requested > landmark ? requested / landmark : landmark / requested;
        if (overshoot <= LandmarkLandingRatio)
            return landmark;

        return requested > landmark
            ? landmark * LandmarkLandingRatio
            : landmark / LandmarkLandingRatio;
    }

    /// <summary>
    /// A re-decode is only worth it when it would add at least this much detail. Smaller gains
    /// would mean decoding the file again for every notch.
    /// </summary>
    public const double DetailGrowthFactor = 1.25d;

    /// <summary>
    /// v5.6.6: how large the file should be decoded for the view as it is now, or an empty size
    /// when what is loaded already covers it.
    ///
    /// <para>
    /// The native provider decodes at the size the window asked for and then scales that
    /// placeholder up to its cap, which is why magnifying a picture used to go soft: the pixels
    /// being magnified were never decoded. Zooming past the loaded detail therefore asks for the
    /// file again at the size actually being looked at - bounded by the file's own size, because
    /// no decode can add detail that is not in the file.
    /// </para>
    /// </summary>
    /// <returns>The size to decode at, or <c>null</c> when the loaded detail already covers it.</returns>
    public static Size? NeededDetailSize(Size viewport, double zoomFactor, double dpiScaleX, double dpiScaleY,
        Size realSize, Size alreadyDecoded)
    {
        if (viewport.IsEmpty || realSize.IsEmpty || zoomFactor <= 0 ||
            double.IsNaN(zoomFactor) || double.IsNaN(dpiScaleX) || double.IsNaN(dpiScaleY) ||
            dpiScaleX <= 0 || dpiScaleY <= 0)
        {
            return null;
        }

        var needed = new Size(
            Math.Min(realSize.Width, viewport.Width * zoomFactor * dpiScaleX),
            Math.Min(realSize.Height, viewport.Height * zoomFactor * dpiScaleY));

        if (needed.Width <= alreadyDecoded.Width * DetailGrowthFactor &&
            needed.Height <= alreadyDecoded.Height * DetailGrowthFactor)
        {
            return null;
        }

        return needed;
    }
}
