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
using System.Globalization;
using System.Windows;

namespace QuickLook.Common.Helpers;

/// <summary>
/// v5.0.4: the serialised form of the preview window's remembered custom size
/// (<c>PreviewWindowSize</c>, e.g. <c>1280x720</c>, invariant culture). Kept out of the
/// viewer window so the parsing and the bounds check can be unit tested.
/// </summary>
public static class WindowSizeSetting
{
    /// <summary>Narrowest window accepted from the settings store.</summary>
    public const double MinWidth = 200d;

    /// <summary>Shortest window accepted from the settings store.</summary>
    public const double MinHeight = 150d;

    /// <summary>Longest side accepted from the settings store - a stale value from a
    /// monitor that no longer exists must not be able to place a window off the desktop.</summary>
    public const double MaxSide = 20000d;

    public static string Format(Size size)
    {
        // AwayFromZero: half-way sizes round the way a user expects ("1024.5" -> 1025)
        // instead of the .NET default banker's rounding.
        var width = Math.Round(size.Width, MidpointRounding.AwayFromZero);
        var height = Math.Round(size.Height, MidpointRounding.AwayFromZero);

        return FormattableString.Invariant($"{width}x{height}");
    }

    public static bool TryParse(string value, out Size size)
    {
        size = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('x', 'X');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ||
            !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
            return false;

        if (width < MinWidth || height < MinHeight || width > MaxSide || height > MaxSide)
            return false;

        size = new Size(width, height);
        return true;
    }
}
