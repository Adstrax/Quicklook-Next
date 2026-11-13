// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLookNext;

namespace QuickLook.Tests;

internal class ReleaseVersionTests
{
    public void PrereleaseTagsAreParsedAndOrdered() =>
        AssertVersionOrder("v6.9.0-beta.10", "6.9.0-beta.2");

    public void StableIsNewerThanPrereleaseWithTheSameCore() =>
        AssertVersionOrder("6.9.0", "6.9.0-rc.1");

    public void HigherCoreWinsAcrossPrereleaseSuffixes() =>
        AssertVersionOrder("6.9.0-beta.1", "6.8.9");

    public void BuildMetadataDoesNotAffectOrdering() =>
        AssertVersionOrder("6.9.0+build.2", "6.9.0+build.1", expectedComparison: 0);

    public void TaggedDisplayVersionDropsPrefixAndMetadata()
    {
        Assert.True(ReleaseVersion.TryParse("v6.9.0-beta.2+abc123", out var version),
            "a conventional GitHub tag parses");
        Assert.Equal("6.9.0-beta.2", version.DisplayVersion, "the tag is display-ready");
        Assert.True(version.IsPrerelease, "the suffix is recognized as prerelease");
    }

    public void MalformedVersionsAreRejected()
    {
        Assert.False(ReleaseVersion.TryParse("latest", out _), "text");
        Assert.False(ReleaseVersion.TryParse("6", out _), "single component");
        Assert.False(ReleaseVersion.TryParse("6.9.0-", out _), "empty suffix");
        Assert.False(ReleaseVersion.TryParse("6.9.0-beta..1", out _), "empty identifier");
    }

    private static void AssertVersionOrder(string newer, string older, int expectedComparison = 1)
    {
        Assert.True(ReleaseVersion.TryParse(newer, out var newerVersion), newer);
        Assert.True(ReleaseVersion.TryParse(older, out var olderVersion), older);
        Assert.Equal(
            expectedComparison,
            newerVersion.CompareTo(olderVersion),
            $"{newer} compared with {older}");
    }
}
