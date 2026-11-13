// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLookNext.Helpers;
using System;

namespace QuickLook.Tests;

internal class UpdaterReleaseSelectionTests
{
    public void NewestPublishedPreviewsAreSelected()
    {
        var releases = Releases(
            ("6.8.0", false, false, "2026-11-01T00:00:00Z"),
            ("6.9.0-beta.2", true, false, "2026-11-02T00:00:00Z"),
            ("6.9.0-beta.10", true, false, "2026-11-03T00:00:00Z"),
            ("6.10.0-beta.1", true, true, "2026-11-04T00:00:00Z"));

        var selected = Updater.SelectNewestReleaseTag(releases, "6.8.0");

        Assert.Equal("6.9.0-beta.10", selected,
            "numeric prerelease identifiers are ordered numerically");
    }

    public void StableReleaseWinsWhenCoreVersionsMatch()
    {
        var releases = Releases(
            ("6.9.0-beta.3", true, false, "2026-11-03T00:00:00Z"),
            ("6.9.0", false, false, "2026-11-02T00:00:00Z"));

        var selected = Updater.SelectNewestReleaseTag(releases, "6.8.9");

        Assert.Equal("6.9.0", selected,
            "a stable release is preferred to a prerelease of the same version");
    }

    public void InstalledPrereleaseCanAdvanceWithinTheSameCore()
    {
        var releases = Releases(
            ("6.9.0-beta.10", true, false, "2026-11-03T00:00:00Z"),
            ("6.9.0-beta.2", true, false, "2026-11-02T00:00:00Z"));

        var selected = Updater.SelectNewestReleaseTag(releases, "6.9.0-beta.2");

        Assert.Equal("6.9.0-beta.10", selected,
            "an installed beta can receive a later beta from the same line");
    }

    public void MalformedOrOlderReleasesAreNotOffered()
    {
        var releases = Releases(
            ("nightly", true, false, "2026-11-04T00:00:00Z"),
            ("6.8.0", false, false, "2026-11-03T00:00:00Z"),
            ("6.7.9", false, false, "2026-11-02T00:00:00Z"));

        Assert.True(Updater.SelectNewestReleaseTag(releases, "6.8.0") is null,
            "nothing newer than the installed version is selected");
    }

    private static Updater.ReleaseCandidate[] Releases(
        params (string Tag, bool Prerelease, bool Draft, string Published)[] values)
    {
        var releases = new Updater.ReleaseCandidate[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            releases[i] = new Updater.ReleaseCandidate(
                value.Tag,
                value.Draft,
                DateTimeOffset.Parse(value.Published));
        }

        return releases;
    }
}
