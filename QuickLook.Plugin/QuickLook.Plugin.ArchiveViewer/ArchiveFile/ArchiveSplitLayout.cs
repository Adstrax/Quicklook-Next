// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

/// <summary>
/// Keeps the archive list and its embedded preview at a stable, useful share of
/// the window after the user has moved the splitter.
/// </summary>
internal static class ArchiveSplitLayout
{
    internal const double DefaultRatio = 0.52d;
    internal const double MinimumRatio = 0.22d;
    internal const double MaximumRatio = 0.78d;

    internal static double Normalize(double ratio)
        => double.IsFinite(ratio)
            ? Math.Clamp(ratio, MinimumRatio, MaximumRatio)
            : DefaultRatio;

    internal static double RatioFromWidths(double listWidth, double previewWidth)
    {
        var total = listWidth + previewWidth;
        return total > 0 && double.IsFinite(total)
            ? Normalize(listWidth / total)
            : DefaultRatio;
    }
}
