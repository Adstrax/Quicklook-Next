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

internal class ArchiveLoadProgressTests
{
    public void FirstAndFinalProgressAreAlwaysReported()
    {
        var throttle = new ArchiveLoadProgressThrottle();

        Assert.True(throttle.ShouldReport(0d, 0), "initial state");
        Assert.False(throttle.ShouldReport(10d, 0), "the interval has not elapsed");
        Assert.True(throttle.ShouldReport(100d, 0), "completion is never throttled");
    }

    public void AWholePercentStepNeedsTheMinimumInterval()
    {
        var throttle = new ArchiveLoadProgressThrottle();

        Assert.True(throttle.ShouldReport(12d, 0), "first progress");
        Assert.False(throttle.ShouldReport(12.9d, 500), "less than one percent");
        Assert.False(throttle.ShouldReport(13d, 60), "one percent too soon");
        Assert.True(throttle.ShouldReport(13d, 120), "one percent after the interval");
    }

    public void AMillionEntriesProduceOnlyAFewHundredNotifications()
    {
        var throttle = new ArchiveLoadProgressThrottle();
        var reports = 0;

        for (var entry = 0; entry < 1_000_000; entry++)
        {
            var percent = entry * 100d / 1_000_000d;
            if (throttle.ShouldReport(percent, entry / 10))
                reports++;
        }

        Assert.True(reports < 900, "the UI receives a bounded number of updates");
    }

    public void ProgressMovingBackwardsIsNotThrottled()
    {
        var throttle = new ArchiveLoadProgressThrottle();

        Assert.True(throttle.ShouldReport(60d, 200), "first progress");
        Assert.True(throttle.ShouldReport(40d, 210), "a new volume can restart the stream position");
    }
}
