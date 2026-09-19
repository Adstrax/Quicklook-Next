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
/// v5.0.10: the arithmetic behind "make the preview window fit the screen".
///
/// <para>
/// On a desktop with monitors of different resolution *and* scaling the size a
/// preview asked for and the size the window was finally given were derived
/// from two different monitors - the plugin measured against the monitor of the
/// foreground window, the viewer then centred the window on the monitor it was
/// placed on. When those two are not the same (the classic 4K laptop panel next
/// to an external 1080p screen), a large landscape image came out wider than
/// the screen it landed on and the preview stretched across several monitors
/// (upstream QL-Win/QuickLook#827).
/// </para>
///
/// <para>
/// Everything here is a pure function of its arguments on purpose: this is the
/// part of that fix which can be verified without owning a second monitor.
/// </para>
/// </summary>
public static class PreviewWindowSizing
{
    /// <summary>
    /// The ratio <paramref name="content"/> has to be scaled by so that it fits
    /// into <paramref name="desktop"/> * <paramref name="maxRatio"/>.
    /// <para>
    /// Never enlarges (the result is at most 1), and never returns 0 either: a
    /// missing or nonsensical limit - a failed monitor query, a zero, negative
    /// or NaN ratio - is treated as "the whole screen" instead of turning the
    /// preview into a zero-sized window.
    /// </para>
    /// </summary>
    /// <param name="content">The size the content wants, in DIP.</param>
    /// <param name="desktop">The usable area of the target monitor, in DIP.</param>
    /// <param name="maxRatio">The share of that area the content may take (0..1).</param>
    public static double FitRatio(Size content, Size desktop, double maxRatio)
    {
        // A content size without an extent cannot be scaled sensibly.
        if (!IsUsable(content.Width) || !IsUsable(content.Height))
            return 1d;

        // Clamp the requested share: enlarging is never allowed, and an invalid
        // share falls back to the full screen (see the summary above).
        if (double.IsNaN(maxRatio) || maxRatio > 1d || maxRatio <= 0d)
            maxRatio = 1d;

        // Without a usable desktop there is nothing to fit against; leaving the
        // content alone is the only answer that cannot produce a broken window.
        if (!IsUsable(desktop.Width) || !IsUsable(desktop.Height))
            return 1d;

        var widthRatio = desktop.Width * maxRatio / content.Width;
        var heightRatio = desktop.Height * maxRatio / content.Height;

        var ratio = Math.Min(widthRatio, heightRatio);
        if (double.IsNaN(ratio) || ratio <= 0d)
            return 1d;

        return Math.Min(ratio, 1d);
    }

    /// <summary>
    /// <paramref name="content"/> scaled by <see cref="FitRatio"/> - the size
    /// the viewer should give the content.
    /// </summary>
    public static Size FitWithin(Size content, Size desktop, double maxRatio)
    {
        var ratio = FitRatio(content, desktop, maxRatio);
        if (ratio >= 1d)
            return content;

        return new Size(content.Width * ratio, content.Height * ratio);
    }

    /// <summary>
    /// Shrinks <paramref name="size"/> so that it fits onto
    /// <paramref name="desktop"/>. This is the last guard before the window is
    /// placed: whatever a plugin asked for - or whatever the window kept from
    /// the monitor it was sized on - the window never ends up larger than the
    /// screen it is shown on.
    /// <para>
    /// Never enlarges, and does nothing at all when the desktop is unknown
    /// (0x0, e.g. a failed monitor query).
    /// </para>
    /// </summary>
    public static Size ClampToDesktop(Size size, Size desktop)
    {
        if (!IsUsable(size.Width) || !IsUsable(size.Height))
            return size;

        var width = IsUsable(desktop.Width) ? Math.Min(size.Width, desktop.Width) : size.Width;
        var height = IsUsable(desktop.Height) ? Math.Min(size.Height, desktop.Height) : size.Height;

        return new Size(width, height);
    }

    private static bool IsUsable(double value) =>
        value > 0d && !double.IsNaN(value) && !double.IsInfinity(value);
}
