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
using System.Text;

namespace QuickLook.Tests;

internal class ReleaseHistoryServiceTests
{
    public void DraftsAndPrereleasesAreExcludedFromTheHistory()
    {
        var releases = ReleaseHistoryService.ParseReleasesJson(
            """
            [
              {
                "tag_name": "6.8.0",
                "name": "QuickLook-Next 6.8.0",
                "body": "## Changes",
                "html_url": "https://github.com/Adstrax/Quicklook-Next/releases/tag/6.8.0",
                "published_at": "2026-10-29T10:20:30Z",
                "draft": false,
                "prerelease": false
              },
              {
                "tag_name": "6.8.1",
                "name": "Draft",
                "body": "hidden",
                "published_at": null,
                "draft": true,
                "prerelease": false
              },
              {
                "tag_name": "6.9.0-beta.1",
                "name": "Beta",
                "body": "hidden",
                "published_at": "2026-10-30T10:20:30Z",
                "draft": false,
                "prerelease": true
              }
            ]
            """);

        Assert.Equal(1, releases.Count, "only published stable releases are shown");
        Assert.Equal("6.8.0", releases[0].Version, "the stable release is retained");
    }

    public void MissingReleasePageFallsBackToTheTagUrlAndDuplicatesAreIgnored()
    {
        var releases = ReleaseHistoryService.ParseReleasesJson(
            """
            [
              {
                "tag_name": "v6.8.0",
                "name": "QuickLook-Next 6.8.0",
                "body": "First",
                "published_at": "2026-10-29T10:20:30Z"
              },
              {
                "tag_name": "6.8.0",
                "name": "Duplicate",
                "body": "Second",
                "published_at": "2026-10-29T10:21:30Z"
              }
            ]
            """);

        Assert.Equal(1, releases.Count, "the same version is shown once");
        Assert.Equal("6.8.0", releases[0].Version, "the v-prefix is not shown twice");
        Assert.Equal(
            "https://github.com/Adstrax/QuickLook-Next/releases/tag/v6.8.0",
            releases[0].ReleaseNotesUrl,
            "a missing API URL is reconstructed from the tag");
    }

    public void ReleaseHistoryIsCappedForPredictableMemoryUse()
    {
        var json = new StringBuilder("[");
        for (var i = 0; i < ReleaseHistoryService.MaximumReleases + 5; i++)
        {
            if (i > 0)
                json.Append(',');
            json.Append(
                $$"""
                {
                  "tag_name": "6.{{100 + i}}.0",
                  "name": "Release {{i}}",
                  "body": "Notes {{i}}",
                  "published_at": "2026-10-29T10:20:30Z"
                }
                """);
        }
        json.Append(']');

        var releases = ReleaseHistoryService.ParseReleasesJson(json.ToString());

        Assert.Equal(
            ReleaseHistoryService.MaximumReleases,
            releases.Count,
            "the release list has a fixed upper bound");
    }

    public void OversizedReleaseBodyIsTruncatedBeforeItReachesTheDialog()
    {
        var body = new string('x', 100_050);
        var releases = ReleaseHistoryService.ParseReleasesJson(
            $$"""
            [
              {
                "tag_name": "6.8.0",
                "name": "QuickLook-Next 6.8.0",
                "body": "{{body}}",
                "published_at": "2026-10-29T10:20:30Z"
              }
            ]
            """);

        Assert.Equal(1, releases.Count, "the release is retained");
        Assert.True(releases[0].Notes.Length <= 100_005,
            "release notes are bounded before rendering");
        Assert.True(releases[0].Notes.EndsWith('…'),
            "a truncated body makes the cut visible");
    }
}
