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

namespace QuickLook.Plugin.Shared;

/// <summary>
/// Builds the per-load URL used by panels that serve an embedded page from the
/// <c>file://quicklook/</c> host. The version token lets a readiness message name
/// the document it belongs to; the nonce makes Chromium navigate even when the
/// previous preview used the same host.
/// </summary>
public static class WebpageNavigationUri
{
    public static Uri CreateFresh(Uri baseUri, int version)
    {
        if (baseUri == null)
            throw new ArgumentNullException(nameof(baseUri));

        if (version <= 0)
            throw new ArgumentOutOfRangeException(nameof(version));

        return new Uri(
            $"{baseUri.GetLeftPart(UriPartial.Path)}?v={version}&n={Guid.NewGuid():N}");
    }

    public static string GetToken(Uri uri)
    {
        var query = uri?.Query;
        if (string.IsNullOrEmpty(query))
            return string.Empty;

        foreach (var part in query.TrimStart('?').Split('&'))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 &&
                string.Equals(part.Substring(0, separator), "v", StringComparison.Ordinal))
                return Uri.UnescapeDataString(part.Substring(separator + 1));
        }

        return string.Empty;
    }
}
