// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v3.11.0: the page for a .docx/.docm. v5.7.0: the document is shown as the pages it is made of, in one
/// scroll, while the rail keeps the document's own outline - its headings and its table of contents - as the
/// way around it; a rail row is an anchor and scrolling is what getting there takes, which is what the base
/// class already does with a navigation target.
/// </summary>
public sealed class DocumentPanel : OfficePanelBase
{
    public DocumentPanel(string path)
    {
        // v5.7.0: the pictures go to a temp folder served over a virtual host, so a photo-heavy
        // document no longer has to fit in one HTML string (NavigateToString stops at 2 MB).
        // v6.1.2: an unchanged document reuses its parsed page and images when the user comes back
        // to it; the cache hands the panel its own copy, so normal disposal still applies.
        NavigateAsync(() => OfficePanelCache.Get(
            path,
            folder => DocxToHtml.Convert(path, folder, ImageHost)));
    }
}
