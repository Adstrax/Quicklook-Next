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
using System.Windows;

namespace QuickLookNext.Helpers;

/// <summary>
/// v5.5.0: where the preview window goes when it is resized on the screen it already occupies.
///
/// <para>
/// The rule is the "9/10 layout": the window's size grows or shrinks around its old centre, and
/// the result is pulled back inside the monitor - a window that sits in the left third keeps its
/// left edge, one in the right third keeps its right edge, anything in the middle is nudged until
/// it is inside. That is the part of the positioning code that decides whether a preview stays on
/// the screen it was opened on, so it lives here as a pure function and is covered by tests
/// (mixed-DPI geometries included - see WindowPlacementTests).
/// </para>
///
/// <para>
/// The 0.1 margins are scaled by the monitor's DPI in the original code; that is preserved here
/// verbatim. It only decides *which* edge is kept, and every branch keeps the window inside the
/// monitor, so the scales are a behavioural quirk rather than a defect - but changing them would
/// change where existing users' windows land.
/// </para>
/// </summary>
internal static class WindowPlacement
{
    /// <summary>
    /// The pixel location for a window that is being resized to <paramref name="newSizeDip"/>
    /// while it stays on <paramref name="monitorPx"/>. The caller keeps the DIP size; WPF scales it.
    /// </summary>
    internal static Point PlaceNearExisting(Rect oldPx, Size newSizeDip,
        DisplayDeviceHelper.ScaleFactor scale, Rect monitorPx)
    {
        var limitPercentX = 0.1d * scale.Horizontal;
        var limitPercentY = 0.1d * scale.Vertical;

        var pxSize = new Size(scale.Horizontal * newSizeDip.Width, scale.Vertical * newSizeDip.Height);

        var newRect = Rect.Inflate(oldPx,
            (pxSize.Width - oldPx.Width) / 2d,
            (pxSize.Height - oldPx.Height) / 2d);

        var leftLimit = monitorPx.Left + monitorPx.Width * limitPercentX;
        var rightLimit = monitorPx.Right - monitorPx.Width * limitPercentX;
        var topLimit = monitorPx.Top + monitorPx.Height * limitPercentY;
        var bottomLimit = monitorPx.Bottom - monitorPx.Height * limitPercentY;

        if (oldPx.Left < leftLimit && oldPx.Right < rightLimit) // left third: keep the left edge
            newRect.X = Math.Max(oldPx.Left, monitorPx.Left);
        else if (oldPx.Left > leftLimit && oldPx.Right > rightLimit) // right third: keep the right edge
            newRect.X = Math.Min(oldPx.Right, monitorPx.Right) - newRect.Width;
        else // middle: nudge until it is inside
            newRect.Offset(
                Math.Max(0d, monitorPx.Left - newRect.Left) + Math.Min(0d, monitorPx.Right - newRect.Right),
                0d);

        if (oldPx.Top < topLimit && oldPx.Bottom < bottomLimit) // top third: keep the top edge
            newRect.Y = Math.Max(oldPx.Top, monitorPx.Top);
        else if (oldPx.Top > topLimit && oldPx.Bottom > bottomLimit) // bottom third: keep the bottom edge
            newRect.Y = Math.Min(oldPx.Bottom, monitorPx.Bottom) - newRect.Height;
        else // middle: nudge until it is inside
            newRect.Offset(0d,
                Math.Max(0d, monitorPx.Top - newRect.Top) + Math.Min(0d, monitorPx.Bottom - newRect.Bottom));

        // v5.5.0: the anchors above keep the *old* edge, which is wrong for a window that grew a
        // lot - the right/bottom anchor then pushes it off the opposite side (measured: a window
        // clamped to the width of a 250% panel hung 72 px off the left edge). This is the same
        // "pull it back inside" the middle branch does, applied to every branch; it is a no-op
        // whenever the window fits, so the anchor behaviour is unchanged.
        newRect.Offset(
            Math.Max(0d, monitorPx.Left - newRect.Left) + Math.Min(0d, monitorPx.Right - newRect.Right),
            0d);
        newRect.Offset(0d,
            Math.Max(0d, monitorPx.Top - newRect.Top) + Math.Min(0d, monitorPx.Bottom - newRect.Bottom));

        return newRect.Location;
    }
}
