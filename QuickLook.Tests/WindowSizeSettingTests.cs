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
using System.Windows;

namespace QuickLook.Tests;

/// <summary>
/// v5.0.4: the preview window remembers the size the user dragged it to
/// (<c>PreviewWindowSize</c>). The stored text is user-editable and survives monitor
/// changes, so parsing has to reject anything that cannot be a window size instead of
/// placing the preview off the desktop.
/// </summary>
internal class WindowSizeSettingTests
{
    public void RoundTripsAWindowSize()
    {
        var formatted = WindowSizeSetting.Format(new Size(1280, 720));
        Assert.Equal("1280x720", formatted, "invariant formatting");

        Assert.True(WindowSizeSetting.TryParse(formatted, out var size), "round trip parses");
        Assert.Equal(1280d, size.Width, "round trip width");
        Assert.Equal(720d, size.Height, "round trip height");
    }

    public void AcceptsFractionalSizesRoundedOnWrite()
    {
        Assert.True(WindowSizeSetting.TryParse("1024.5x768.25", out var size), "fractional size parses");
        Assert.Equal(1024.5d, size.Width, "fractional width");

        Assert.Equal("1025x768", WindowSizeSetting.Format(size), "fractional size is rounded when written");
    }

    public void AcceptsUppercaseSeparatorAndPadding()
    {
        Assert.True(WindowSizeSetting.TryParse(" 1280X720 ", out var size), "uppercase separator");
        Assert.Equal(1280d, size.Width, "width");
        Assert.Equal(720d, size.Height, "height");
    }

    public void RejectsMalformedValues()
    {
        foreach (var value in new[] { "", "   ", "1280", "1280x", "x720", "wide x tall", "1280x720x1080" })
        {
            Assert.False(WindowSizeSetting.TryParse(value, out _), $"rejects <{value}>");
        }
    }

    public void RejectsSizesOutsideTheAcceptedRange()
    {
        // Too small to be a window, and large enough to land off the desktop after a
        // monitor change.
        foreach (var value in new[] { "0x0", "10x10", "199x800", "800x149", "20001x800", "800x20001", "-1280x720" })
        {
            Assert.False(WindowSizeSetting.TryParse(value, out _), $"rejects <{value}>");
        }
    }

    public void RejectsCultureSpecificDecimals()
    {
        // A comma is a decimal separator in some locales but not in the stored format;
        // treating it as one would silently change the remembered size.
        Assert.False(WindowSizeSetting.TryParse("1280,5x720", out _), "comma decimal is rejected");
    }
}
