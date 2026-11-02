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
/// v6.7.7: internal WPF hosts can be visible to Windows while remaining completely
/// absent from the user's screen. Low-memory restart protection must not mistake
/// the WebView2 parking window for an open dialog.
/// </summary>
internal class WindowHelperTests
{
    public void InternalHostDefaultsToFalse()
    {
        var window = new Window();

        Assert.False(WindowHelper.GetIsInternalHost(window),
            "ordinary windows must still protect an in-flight restart");
    }

    public void InternalHostCanBeMarkedAndUnmarked()
    {
        var window = new Window();

        WindowHelper.SetIsInternalHost(window, true);
        Assert.True(WindowHelper.GetIsInternalHost(window),
            "the WebView2 parking window is classified as an internal host");

        WindowHelper.SetIsInternalHost(window, false);
        Assert.False(WindowHelper.GetIsInternalHost(window),
            "the marker is not sticky");
    }
}
