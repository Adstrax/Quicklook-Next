// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using QuickLookNext.Helpers;
using System;
using System.Linq;

namespace QuickLook.Tests;

/// <summary>
/// v5.8.0: two rules decide what the family warm-up does, and both of them are the kind of
/// thing that breaks silently - a warm-up that prepares a family too few costs a slow
/// preview nobody can attribute, and one that starts before the user has previewed
/// anything costs idle memory the low memory mode was supposed to give back.
/// </summary>
internal class PreviewWarmUpTests : SettingsFixture
{
    public override void Setup()
    {
        base.Setup();
        StartupWarmUp.SetColdIdle(false);
        PreviewWarmUp.ResetForTests();
    }

    public override void Teardown()
    {
        StartupWarmUp.SetColdIdle(false);
        PreviewWarmUp.ResetForTests();
        base.Teardown();
    }

    /// <summary>
    /// Every family the warm-up knows, in the order a user with no history gets them. The
    /// list is spelled out here rather than read from the class: it is the promise the
    /// release notes make, so a change to it should have to change this test too.
    /// </summary>
    private static readonly string[] Families =
    [
        "QuickLook.Plugin.TextViewer",
        "QuickLook.Plugin.ImageViewer",
        "QuickLook.Plugin.MarkdownViewer",
        "QuickLook.Plugin.PDFViewer",
        "QuickLook.Plugin.CsvViewer",
        "QuickLook.Plugin.HtmlViewer",
        "QuickLook.Plugin.ArchiveViewer",
        "QuickLook.Plugin.FontViewer",
    ];

    public void TheDefaultIsTheBalancedThreeFamilies()
    {
        Assert.Equal(3, PreviewWarmUp.DefaultFamilyCount, "the default is the balanced count");

        var chosen = PreviewWarmUp.ChooseFamilies(PreviewWarmUp.DefaultFamilyCount);

        Assert.Equal(PreviewWarmUp.DefaultFamilyCount, chosen.Count, "the default number is prepared");
        Assert.Equal(PreviewWarmUp.DefaultFamilyCount,
            chosen.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "each family appears once");

        Assert.True(chosen.All(f => Families.Contains(f, StringComparer.OrdinalIgnoreCase)),
            "every chosen family is known");
    }

    /// <summary>
    /// The two families the common-format order does not name (.zip, .ttf) are the ones a
    /// top-up loop that only walked that order used to leave out.
    /// </summary>
    public void AFamilyOutsideTheCommonOrderIsStillChosen()
    {
        var chosen = PreviewWarmUp.ChooseFamilies(PreviewWarmUp.AllFamilies);

        Assert.True(chosen.Contains("QuickLook.Plugin.ArchiveViewer", StringComparer.OrdinalIgnoreCase),
            "the archive family is chosen");
        Assert.True(chosen.Contains("QuickLook.Plugin.FontViewer", StringComparer.OrdinalIgnoreCase),
            "so is the font family");
    }

    public void TheChosenFamiliesAreTheSameOnEveryRun()
    {
        var first = PreviewWarmUp.ChooseFamilies(PreviewWarmUp.AllFamilies);
        var second = PreviewWarmUp.ChooseFamilies(PreviewWarmUp.AllFamilies);

        Assert.True(first.SequenceEqual(second, StringComparer.OrdinalIgnoreCase),
            "the warm-up order does not depend on dictionary enumeration order");
    }

    public void ASmallerCountStillPreparesThatMany()
    {
        var chosen = PreviewWarmUp.ChooseFamilies(2);

        Assert.Equal(2, chosen.Count, "two families");
        Assert.True(chosen.All(f => Families.Contains(f, StringComparer.OrdinalIgnoreCase)),
            "and both of them are families the warm-up knows");

        Assert.Equal(0, PreviewWarmUp.ChooseFamilies(0).Count, "a count of zero prepares nothing");
    }

    /// <summary>
    /// A normal launch gets the head start regardless of the release policy. Only the
    /// cold-idle replacement process waits for a real preview.
    /// </summary>
    public void EveryNormalLaunchPreparesAtStartupAndColdIdleDoesNot()
    {
        StartupWarmUp.Set(true);
        Assert.True(PreviewWarmUp.ShouldStart(afterFirstPreview: false),
            "the default timed policy still prepares at startup");
        Assert.True(PreviewWarmUp.ShouldStart(afterFirstPreview: true),
            "the preview path remains valid");

        StartupWarmUp.Set(false);
        Assert.True(PreviewWarmUp.ShouldStart(afterFirstPreview: false),
            "off also prepares the families at startup");

        StartupWarmUp.SetColdIdle(true);
        Assert.False(PreviewWarmUp.ShouldStart(afterFirstPreview: false),
            "the release restart stays cold");

        StartupWarmUp.SetColdIdle(false);
        Assert.True(PreviewWarmUp.ShouldStart(afterFirstPreview: true),
            "a real preview leaves cold idle and starts the session");
    }

    /// <summary>
    /// A single start, whichever call gets there first: the loop must not run twice.
    /// </summary>
    public void StartingTwiceIsStillOneWarmUp()
    {
        StartupWarmUp.Set(true);

        PreviewWarmUp.Start(afterFirstPreview: false);      // startup
        Assert.True(PreviewWarmUp.HasStarted, "startup starts it");

        PreviewWarmUp.Start(afterFirstPreview: true);       // the first preview
        Assert.True(PreviewWarmUp.HasStarted, "and a later call is still the same warm-up");
    }
}
