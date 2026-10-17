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

namespace QuickLook.Plugin.Shared;

/// <summary>
/// v6.0.0: identifies the WebView2 navigation that is still allowed to commit its document.
/// A reused panel can start the next document before the previous navigation reports completion;
/// without this check the older completion could post its payload into the newer page.
/// </summary>
internal sealed class WebpageNavigationGate
{
    private ulong _latestNavigationId;

    internal void Accept(ulong navigationId)
    {
        if (navigationId != 0)
            _latestNavigationId = navigationId;
    }

    internal bool IsCurrent(ulong navigationId) =>
        navigationId != 0 && navigationId == _latestNavigationId;

    internal void Reset() => _latestNavigationId = 0;
}
