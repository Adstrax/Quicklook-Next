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
/// v3.12.0: the page for a .pptx/.pptm. v5.7.0: the rail lists the slides, and picking one shows that
/// slide - the deck is not one long scroll any more.
/// </summary>
public sealed class PresentationPanel : OfficePanelBase
{
    /// <summary>
    /// v5.7.1: the colour the page has before it paints is the deck's own desk - PptxToHtml draws the slide
    /// on #F3F3F3, so the wait and the slide that lands on it match (see OfficePanelBase).
    /// </summary>
    private static readonly System.Windows.Media.Color SlideDesk =
        System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3);

    public PresentationPanel(string path) : base(SlideDesk)
    {
        // v6.1.2: reuse the parsed deck and its slide pictures across side-button switches.
        NavigateAsync(() => OfficePanelCache.Get(
            path,
            folder => PptxToHtml.Convert(path, folder, ImageHost)));
    }

    protected override void OnNavigationSelected(int index)
    {
        if (index < 0 || index >= Navigation.Count)
            return;

        RunScript($"showSlide({index})");
    }
}
