// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLookNext;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Documents;

namespace QuickLook.Tests;

internal class UpdateReleaseTests
{
    public void ReleaseNotesMetadataIsReadFromTheGitHubResponse()
    {
        var info = UpdateReleaseInfo.Create(
            "6.8.0",
            "QuickLook-Next 6.8.0",
            "## Changes\n\n- Faster previews",
            "https://github.com/Adstrax/Quicklook-Next/releases/tag/6.8.0",
            "2026-10-29T10:20:30Z");

        Assert.Equal("6.8.0", info.Version, "the version comes from the release tag");
        Assert.Equal("QuickLook-Next 6.8.0", info.Title, "the release title is kept");
        Assert.True(info.Notes.Contains("Faster previews", StringComparison.Ordinal),
            "the release body is available to the dialog");
        Assert.Equal(
            "https://github.com/Adstrax/Quicklook-Next/releases/tag/6.8.0",
            info.ReleaseNotesUrl,
            "the GitHub fallback link is preserved");
        Assert.True(info.PublishedAt is not null, "the release date is parsed");
    }

    public void MarkdownReleaseNotesBecomeReadableDesktopContent()
    {
        var opened = string.Empty;
        var document = ReleaseNotesFormatter.BuildDocument(
            """
            ## What's new

            - **Faster** image preview
            - `PDF` rendering is sharper
            - [Full release](https://github.com/Adstrax/Quicklook-Next/releases/tag/6.8.0)
            """,
            isDark: false,
            url => opened = url);

        var text = new TextRange(document.ContentStart, document.ContentEnd).Text;
        Assert.True(text.Contains("What's new", StringComparison.Ordinal),
            "headings are visible");
        Assert.True(text.Contains("Faster image preview", StringComparison.Ordinal),
            "bullet formatting and emphasis are flattened into readable text");
        Assert.True(text.Contains("PDF rendering is sharper", StringComparison.Ordinal),
            "inline code is readable");

        var link = AllHyperlinks(document).FirstOrDefault();
        Assert.NotNull(link, "release-note links become clickable desktop links");
        Assert.Equal(
            "https://github.com/Adstrax/Quicklook-Next/releases/tag/6.8.0",
            link.NavigateUri?.AbsoluteUri,
            "the link keeps its destination");
        link.RaiseEvent(new System.Windows.RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.Equal(
            "https://github.com/Adstrax/Quicklook-Next/releases/tag/6.8.0",
            opened,
            "clicking the link hands the URL to the browser opener");
    }

    public void EmptyReleaseNotesStillExplainTheMissingDescription()
    {
        var document = ReleaseNotesFormatter.BuildDocument(string.Empty, false, _ => { });
        var text = new TextRange(document.ContentStart, document.ContentEnd).Text;

        Assert.True(text.Contains("No release notes were provided", StringComparison.Ordinal),
            "an empty body produces a clear message");
    }

    private static IEnumerable<Hyperlink> AllHyperlinks(FlowDocument document)
    {
        foreach (var block in document.Blocks)
        {
            foreach (var hyperlink in HyperlinksIn(block))
                yield return hyperlink;
        }
    }

    private static IEnumerable<Hyperlink> HyperlinksIn(Block block)
    {
        if (block is Paragraph paragraph)
        {
            foreach (var hyperlink in paragraph.Inlines.OfType<Hyperlink>())
                yield return hyperlink;
        }
        else if (block is System.Windows.Documents.List list)
        {
            foreach (var item in list.ListItems)
            {
                foreach (var child in item.Blocks)
                {
                    foreach (var hyperlink in HyperlinksIn(child))
                        yield return hyperlink;
                }
            }
        }
    }
}
