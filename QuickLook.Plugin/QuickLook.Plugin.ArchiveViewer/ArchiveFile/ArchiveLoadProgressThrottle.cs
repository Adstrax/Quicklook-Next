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
/// Limits archive progress notifications independently of entry count. A large
/// archive can contain hundreds of thousands of entries while the progress bar
/// only needs a few visible updates per second.
/// </summary>
internal sealed class ArchiveLoadProgressThrottle
{
    internal const long MinimumIntervalMilliseconds = 120;

    private long _lastReportTimestamp = long.MinValue;
    private double _lastReportedPercent = -1;

    internal bool ShouldReport(double percent, long timestamp)
    {
        if (!double.IsFinite(percent))
            return false;

        percent = Math.Clamp(percent, 0d, 100d);
        if (_lastReportedPercent < 0 ||
            percent >= 100d ||
            percent < _lastReportedPercent ||
            (percent - _lastReportedPercent >= 1d &&
             timestamp - _lastReportTimestamp >= MinimumIntervalMilliseconds))
        {
            _lastReportedPercent = percent;
            _lastReportTimestamp = timestamp;
            return true;
        }

        return false;
    }
}
