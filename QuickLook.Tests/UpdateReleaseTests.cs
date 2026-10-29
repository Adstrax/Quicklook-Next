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

    public void GitHubFlavoredMarkdownVariantsRemainReadable()
    {
        var document = ReleaseNotesFormatter.BuildDocument(
            """
            ## Highlights

            - [x] **Faster** startup
            - [ ] Pending item
              - Nested *item*

            > [!WARNING]
            > Check compatibility before upgrading.

            Name | State
            --- | ---
            Preview | **Ready**

            <details>
            <summary>More details</summary>
            First line<br>Second line
            </details>

            ![Screenshot](https://example.com/shot.png)

            [Pull request][pr]

            [pr]: /Adstrax/Quicklook-Next/pull/42
            """,
            isDark: false,
            _ => { },
            new Uri("https://github.com/Adstrax/Quicklook-Next/releases/tag/6.8.0"));

        var text = new TextRange(document.ContentStart, document.ContentEnd).Text;
        Assert.True(text.Contains("Highlights", StringComparison.Ordinal), "headings remain visible");
        Assert.True(text.Contains("☑ Faster startup", StringComparison.Ordinal),
            "completed task-list items become readable checkboxes");
        Assert.True(text.Contains("☐ Pending item", StringComparison.Ordinal),
            "open task-list items become readable checkboxes");
        Assert.True(text.Contains("Nested item", StringComparison.Ordinal),
            "nested list content is retained");
        Assert.True(text.Contains("Warning: Check compatibility", StringComparison.Ordinal),
            "GitHub alert blocks are rendered as readable notes");
        Assert.True(text.Contains("Name", StringComparison.Ordinal) &&
                    text.Contains("Preview", StringComparison.Ordinal) &&
                    text.Contains("Ready", StringComparison.Ordinal),
            "table headers and cells are retained");
        Assert.True(text.Contains("More details", StringComparison.Ordinal) &&
                    text.Contains("First line", StringComparison.Ordinal) &&
                    text.Contains("Second line", StringComparison.Ordinal),
            "details, summary and HTML line breaks remain readable");
        Assert.True(text.Contains("Image: Screenshot", StringComparison.Ordinal),
            "embedded images become descriptive links instead of raw syntax");

        var relativeLink = AllHyperlinks(document)
            .FirstOrDefault(link => link.NavigateUri?.AbsolutePath.EndsWith("/pull/42", StringComparison.Ordinal) == true);
        Assert.NotNull(relativeLink, "reference-style relative links are resolved against the release page");
        Assert.Equal(
            "https://github.com/Adstrax/Quicklook-Next/pull/42",
            relativeLink.NavigateUri?.AbsoluteUri,
            "the resolved link keeps the GitHub destination");
    }

    public void HtmlReleaseNotesDoNotExposeMarkupOrJoiningText()
    {
        var document = ReleaseNotesFormatter.BuildDocument(
            """
            <h2>Fixes</h2>
            <p>Use the <strong>new</strong> viewer.<br>Ready to update.</p>
            <ul><li>First fix</li><li>Second fix</li></ul>
            <a target="_blank" href="https://example.com/notes">Release details</a>
            """,
            isDark: true,
            _ => { });

        var text = new TextRange(document.ContentStart, document.ContentEnd).Text;
        Assert.True(text.Contains("Fixes", StringComparison.Ordinal), "HTML headings are retained");
        Assert.True(text.Contains("Use the new viewer.", StringComparison.Ordinal),
            "HTML emphasis is flattened without markup");
        Assert.True(text.Contains("First fix", StringComparison.Ordinal) &&
                    text.Contains("Second fix", StringComparison.Ordinal),
            "HTML lists are retained");
        Assert.True(!text.Contains("<h2>", StringComparison.Ordinal) &&
                    !text.Contains("<strong>", StringComparison.Ordinal),
            "raw HTML tags never reach the dialog");
        Assert.Equal(
            "https://example.com/notes",
            AllHyperlinks(document).Single().NavigateUri?.AbsoluteUri,
            "HTML anchors become normal desktop links");
    }

    public void MarkdownInsideCodeFencesIsNotInterpretedAsHtml()
    {
        var document = ReleaseNotesFormatter.BuildDocument(
            """
            ```xml
            <strong>literal</strong>
            **literal markdown**
            ```
            """,
            isDark: false,
            _ => { });
        var text = new TextRange(document.ContentStart, document.ContentEnd).Text;

        Assert.True(text.Contains("<strong>literal</strong>", StringComparison.Ordinal),
            "HTML examples inside code fences keep their tags");
        Assert.True(text.Contains("**literal markdown**", StringComparison.Ordinal),
            "Markdown examples inside code fences keep their markers");
        Assert.Equal(0, AllHyperlinks(document).Count(), "code examples do not become links");
    }

    public void ReleaseMetadataNormalizesTagAndRejectsUnsafeLinks()
    {
        var info = UpdateReleaseInfo.Create(
            "v6.8.0",
            string.Empty,
            null,
            "javascript:alert(1)",
            "2026-10-29T10:20:30Z");

        Assert.Equal("6.8.0", info.Version, "a conventional v-prefixed tag is displayed without the prefix");
        Assert.Equal("QuickLook-Next 6.8.0", info.Title, "a missing title gets a useful fallback");
        Assert.Equal(string.Empty, info.Notes, "a null body is handled");
        Assert.Equal(string.Empty, info.ReleaseNotesUrl, "non-HTTP release links are rejected");
        Assert.True(info.PublishedAt is not null, "the release date is parsed");
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
            foreach (var hyperlink in HyperlinksIn(paragraph.Inlines))
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
        else if (block is Table table)
        {
            foreach (var rowGroup in table.RowGroups)
            {
                foreach (var row in rowGroup.Rows)
                {
                    foreach (var cell in row.Cells)
                    {
                        foreach (var child in cell.Blocks)
                        {
                            foreach (var hyperlink in HyperlinksIn(child))
                                yield return hyperlink;
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<Hyperlink> HyperlinksIn(System.Windows.Documents.InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Hyperlink hyperlink)
            {
                yield return hyperlink;
            }
            else if (inline is Span span)
            {
                foreach (var child in HyperlinksIn(span.Inlines))
                    yield return child;
            }
        }
    }
}
