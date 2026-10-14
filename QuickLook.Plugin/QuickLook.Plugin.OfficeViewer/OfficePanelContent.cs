// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v5.7.0: one row of a self-rendered Office panel's left rail - an outline entry for Word, a slide for
/// PowerPoint, a worksheet for Excel.
/// </summary>
/// <param name="Label">What the row shows.</param>
/// <param name="Target">
/// What selecting it means for the page: the id of the element to scroll to for Word and PowerPoint, the
/// worksheet name for Excel.
/// </param>
/// <param name="Level">Indentation, so an outline reads as a hierarchy.</param>
public sealed record OfficeNavEntry(string Label, string Target, int Level = 0);

/// <summary>
/// v5.7.0: what a converter hands to its panel: the page, the structure behind the left rail, and - when
/// the page references pictures - the folder they were written to. The folder is served over a virtual host
/// instead of inlining base64: a template's 25 MB photo used to turn one slide into 34 MB of HTML, which
/// WebView2 then had to parse (and which routinely ran past NavigateToString's 2 MB limit for Word images).
/// </summary>
public sealed class OfficePanelContent
{
    public required string Html { get; init; }

    public IReadOnlyList<OfficeNavEntry> Navigation { get; init; } = [];

    public int SelectedIndex { get; init; }

    public string ImageFolder { get; init; }
}
