// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Plugin.ArchiveViewer.ArchiveFile;

namespace QuickLook.Tests;

internal class ArchiveSplitLayoutTests
{
    public void InvalidOrExtremeRatiosFallBackToAUsableSplit()
    {
        Assert.Equal(
            ArchiveSplitLayout.DefaultRatio,
            ArchiveSplitLayout.Normalize(double.NaN),
            "a missing or corrupt ratio uses the balanced default");
        Assert.Equal(
            ArchiveSplitLayout.MinimumRatio,
            ArchiveSplitLayout.Normalize(0.01),
            "the file list cannot be dragged away completely");
        Assert.Equal(
            ArchiveSplitLayout.MaximumRatio,
            ArchiveSplitLayout.Normalize(0.99),
            "the preview cannot be dragged away completely");
    }

    public void WidthsBecomeARatioThatSurvivesWindowResizing()
    {
        Assert.Equal(
            0.6d,
            ArchiveSplitLayout.RatioFromWidths(600, 400),
            "the split is stored as the list's share of both panes");
        Assert.Equal(
            ArchiveSplitLayout.DefaultRatio,
            ArchiveSplitLayout.RatioFromWidths(0, 0),
            "a layout that has not been measured keeps the default");
    }
}
