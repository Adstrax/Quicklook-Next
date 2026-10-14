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
/// v3.12.0: the page for a .pptx/.pptm. v5.6.19: the rail lists the slides, and picking one shows that
/// slide - the deck is not one long scroll any more.
/// </summary>
public sealed class PresentationPanel : OfficePanelBase
{
    public PresentationPanel(string path)
    {
        NavigateAsync(() =>
        {
            var folder = OfficeImageStore.CreateFolder();
            return PptxToHtml.Convert(path, folder, ImageHost);
        });
    }

    protected override void OnNavigationSelected(int index)
    {
        if (index < 0 || index >= Navigation.Count)
            return;

        RunScript($"showSlide({index})");
    }
}
