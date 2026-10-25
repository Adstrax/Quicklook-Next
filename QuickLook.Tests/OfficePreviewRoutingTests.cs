// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using OfficeViewerPlugin = QuickLook.Plugin.OfficeViewer.Plugin;

namespace QuickLook.Tests;

internal class OfficePreviewRoutingTests
{
    public void PresentationFormatsAlwaysHaveAnAvailableViewer()
    {
        var plugin = new OfficeViewerPlugin();

        Assert.True(plugin.CanHandle(@"C:\deck.pptx"),
            "PPTX must use the self-rendered viewer or its native fallback");
        Assert.True(plugin.CanHandle(@"C:\deck.pptm"),
            "macro-enabled PPTM must be accepted and use the same render path");
    }
}
