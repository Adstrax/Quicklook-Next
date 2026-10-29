// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Plugin.OfficeViewer;
using System;

namespace QuickLook.Tests;

internal class OfficePanelNavigationTests
{
    public void FloatingNavigationIsInjectedAndEscapesLabels()
    {
        var html = "<html><body><main>paper</main></body></html>";
        var navigation = new[]
        {
            new OfficeNavEntry("Chapter <one> & details", "chapter-1"),
            new OfficeNavEntry("\"Quoted\"", "chapter-2", 2),
        };

        var result = OfficePanelNavigation.Inject(html, navigation, 1);

        Assert.True(result.Contains("id=\"ql-office-navigation\"", StringComparison.Ordinal),
            "the floating navigation is added inside the document page");
        Assert.True(result.Contains("position:fixed", StringComparison.Ordinal),
            "the navigation does not take a layout column");
        Assert.True(result.Contains("Chapter &lt;one&gt; &amp; details", StringComparison.Ordinal),
            "navigation labels are HTML encoded");
        Assert.False(result.Contains("Chapter <one> & details", StringComparison.Ordinal),
            "a label cannot inject markup");
        Assert.True(result.Contains("--ql-level:2", StringComparison.Ordinal),
            "the hierarchy level is preserved");
        Assert.True(result.Contains("data-index=\"1\"", StringComparison.Ordinal) &&
                    result.Contains("aria-current=\"true\"", StringComparison.Ordinal),
            "the selected row is highlighted");
    }

    public void NavigationSupportsCollapseAndResize()
    {
        var result = OfficePanelNavigation.Inject(
            "<body></body>",
            [new OfficeNavEntry("Section", "section")],
            0);

        Assert.True(result.Contains("ql-office-navigation-toggle", StringComparison.Ordinal),
            "a persistent show/hide control is present");
        Assert.True(result.Contains("data-collapsed=\"true\"", StringComparison.Ordinal),
            "the navigation can collapse out of the reading area");
        Assert.True(result.Contains("ql-office-navigation-resize", StringComparison.Ordinal) &&
                    result.Contains("cursor:ew-resize", StringComparison.Ordinal),
            "the navigation has a draggable right edge");
        Assert.True(result.Contains("{type:'office-navigation',index}", StringComparison.Ordinal),
            "selection is posted to the Office host");
    }

    public void MessagesAreRoutedOnlyForOfficeNavigation()
    {
        Assert.True(
            OfficePanelNavigation.TryReadSelection("{\"type\":\"office-navigation\",\"index\":3}", out var index),
            "office navigation messages are recognised");
        Assert.Equal(3, index, "the selected navigation index is preserved");

        Assert.False(
            OfficePanelNavigation.TryReadSelection("{\"type\":\"slide\",\"index\":3}", out _),
            "PowerPoint's scroll messages keep their existing route");
        Assert.True(
            OfficePanelNavigation.TryReadPosition("{\"type\":\"office-position\",\"index\":5}", out var position),
            "the current Word section is recognised");
        Assert.Equal(5, position, "the current Word section index is preserved");
        Assert.False(
            OfficePanelNavigation.TryReadSelection("{\"type\":\"office-position\",\"index\":5}", out _),
            "scroll position messages never start another navigation");
        Assert.False(
            OfficePanelNavigation.TryReadSelection("{\"action\":\"rows\",\"start\":300}", out _),
            "spreadsheet pager messages keep their existing route");
        Assert.False(
            OfficePanelNavigation.TryReadSelection("not json", out _),
            "malformed messages never interrupt scrolling");
    }

    public void PageWithoutNavigationIsLeftUntouched()
    {
        var html = "<html><body>plain</body></html>";
        Assert.True(ReferenceEquals(html, OfficePanelNavigation.Inject(html, [], 0)),
            "pages with no structure do not grow a navigation surface");
    }
}
