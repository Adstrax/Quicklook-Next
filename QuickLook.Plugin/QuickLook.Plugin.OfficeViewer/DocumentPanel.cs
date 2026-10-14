// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>v3.11.0: the page for a .docx/.docm, with the document's outline in the rail.</summary>
public sealed class DocumentPanel : OfficePanelBase
{
    public DocumentPanel(string path)
    {
        NavigateAsync(() =>
        {
            // v3.44.0: the pictures go to a temp folder served over a virtual host, so a photo-heavy
            // document no longer has to fit in one HTML string (NavigateToString stops at 2 MB).
            var folder = OfficeImageStore.CreateFolder();
            return DocxToHtml.Convert(path, folder, ImageHost);
        });
    }
}
