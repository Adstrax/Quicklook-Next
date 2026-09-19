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

namespace QuickLook.Plugin.ImageViewer.Helpers;

/// <summary>
/// v5.0.5: the guard for very large images (upstream
/// <see href="https://github.com/QL-Win/QuickLook/issues/1054">#1054 "Crash when previewing
/// large images"</see>).
/// <para>
/// A 64 MP image decoded at full resolution measured 1.2-1.7 GB of private memory here, against
/// ~40 MB for a normal photo; past ~100 MP that is an out-of-memory crash on an 8 GB machine.
/// The panel only ever displays a fit-to-window or zoomed view, so the decode is capped at
/// <see cref="DefaultMaxPixels"/> and scaled down proportionally beyond that. The file's real
/// pixel size still shows in the title, and the preview window still opens at the image's aspect
/// ratio - only sharpness at extreme zoom is affected.
/// </para>
/// </summary>
internal static class DecodePixelLimit
{
    /// <summary>Default cap: 40 MP, i.e. ~160 MB per BGRA copy.</summary>
    internal const long DefaultMaxPixels = 40_000_000L;

    /// <summary>A configured cap below this is a typo, not a preference.</summary>
    private const long MinMaxPixels = 1_000_000L;

    /// <summary>
    /// <c>MaxDecodePixels</c> from the plugin's own config. <c>0</c> disables the cap (the
    /// pre-5.0.5 behaviour, handy when investigating a decode problem).
    /// </summary>
    internal static long MaxPixels
    {
        get
        {
            var configured = SettingHelper.Get(
                "MaxDecodePixels", (int)DefaultMaxPixels, "QuickLook.Plugin.ImageViewer");

            return configured <= 0 ? 0 : Math.Max(MinMaxPixels, configured);
        }
    }

    /// <summary>Whether decoding this image will be scaled down.</summary>
    internal static bool IsLimited(Size fullSize)
    {
        var max = MaxPixels;
        return max > 0 && fullSize.Width > 0 && fullSize.Height > 0 &&
               fullSize.Width * fullSize.Height > max;
    }

    /// <summary>
    /// The size to decode at: the original size, or the proportional reduction that fits the
    /// cap (aspect ratio preserved).
    /// </summary>
    internal static Size Limit(Size fullSize)
    {
        var max = MaxPixels;
        if (max <= 0 || fullSize.Width <= 0 || fullSize.Height <= 0)
            return fullSize;

        var pixels = fullSize.Width * fullSize.Height;
        if (pixels <= max)
            return fullSize;

        var scale = Math.Sqrt(max / pixels);
        return new Size(
            Math.Max(1d, Math.Round(fullSize.Width * scale)),
            Math.Max(1d, Math.Round(fullSize.Height * scale)));
    }
}
