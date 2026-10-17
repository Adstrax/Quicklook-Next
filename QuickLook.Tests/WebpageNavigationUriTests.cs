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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.

using QuickLook.Plugin.Shared;
using System;

namespace QuickLook.Tests;

/// <summary>
/// The embedded-page URL carries the document version used by the first-frame
/// handoff, and a nonce that forces Chromium to load a fresh document.
/// </summary>
internal class WebpageNavigationUriTests
{
    public void TheVersionTokenSurvivesNavigation()
    {
        var uri = WebpageNavigationUri.CreateFresh(new Uri("file://quicklook/"), 17);

        Assert.Equal("17", WebpageNavigationUri.GetToken(uri), "the document version is carried in the URL");
    }

    public void EveryLoadGetsADifferentUrl()
    {
        var first = WebpageNavigationUri.CreateFresh(new Uri("file://quicklook/"), 17);
        var second = WebpageNavigationUri.CreateFresh(new Uri("file://quicklook/"), 17);

        Assert.False(first == second, "the nonce must make repeated loads distinct");
    }

    public void AnOrdinaryFileUrlHasNoReadinessToken()
    {
        var uri = new Uri("file:///C:/preview/font.woff2");

        Assert.Equal(string.Empty, WebpageNavigationUri.GetToken(uri), "ordinary files do not acknowledge embedded documents");
    }
}
