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

using QuickLook.Plugin.ArchiveViewer.ArchiveFile;
using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Documents;

namespace QuickLook.Tests;

internal class ArchiveTreeSearchTests
{
    public void KeepsOnlyMatchingBranchesAndTheirAncestors()
    {
        var docs = new ArchiveFileEntry("docs", true);
        var nested = new ArchiveFileEntry("nested", true, docs);
        var report = new ArchiveFileEntry("report.pdf", false, nested, @"docs\nested\report.pdf");
        _ = new ArchiveFileEntry("notes.txt", false, nested, @"docs\nested\notes.txt");
        _ = new ArchiveFileEntry("other", true);

        var results = ArchiveTreeSearch.Filter([docs], "report");

        Assert.Equal(1, results.Count, "one top-level branch remains");
        Assert.Equal("docs", results[0].Name, "the ancestor is kept");
        Assert.Equal("nested", results[0].Children.Keys[0].Name, "the intermediate folder is kept");
        Assert.Equal("report.pdf",
            results[0].Children.Keys[0].Children.Keys[0].Name,
            "only the matching file remains");
        Assert.True(results[0].IsExpanded,
            "matching directories open automatically");
        Assert.False(results[0].IsSearchMatch,
            "an ancestor retained for context is not counted as a match");
        Assert.Equal("report",
            results[0].Children.Keys[0].Children.Keys[0].SearchQuery,
            "the query reaches the highlighted filename");
    }

    public void EverySearchTermMustMatchTheNameOrPath()
    {
        var folder = new ArchiveFileEntry("projects", true);
        _ = new ArchiveFileEntry("2026 report.pdf", false, folder,
            @"projects\finance\2026 report.pdf");

        Assert.Equal(1, ArchiveTreeSearch.Filter([folder], "finance report").Count,
            "terms can be spread across the path and name");
        Assert.Equal(0, ArchiveTreeSearch.Filter([folder], "finance invoice").Count,
            "one missing term excludes the entry");
    }

    public void SearchAcceptsForwardSlashesInAnArchivePath()
    {
        var folder = new ArchiveFileEntry("docs", true);
        var nested = new ArchiveFileEntry("nested", true, folder, "docs/nested");
        var report = new ArchiveFileEntry("report.pdf", false, nested, "docs/nested/report.pdf");
        report.ModifiedDate = new DateTime(2026, 1, 2);

        var results = ArchiveTreeSearch.Filter([folder], @"docs\nested");
        var found = results[0].Children.Keys[0].Children.Keys[0];

        Assert.Equal("report.pdf", found.Name, "the matching path is found");
        Assert.Equal(report.ModifiedDate, found.ModifiedDate, "entry metadata is preserved");
    }

    public void CancellationStopsAQueuedSearch()
    {
        var source = new ArchiveFileEntry("document.txt", false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ArchiveTreeSearch.Filter([source], "document", cancellation.Token),
            "a canceled search reports cancellation");
    }

    public void NavigationUsesFilesBeforeFolders()
    {
        var folder = new ArchiveFileEntry("documents", true);
        var report = new ArchiveFileEntry("report.pdf", false, folder, @"documents\report.pdf");
        _ = new ArchiveFileEntry("notes.txt", false, folder, @"documents\notes.txt");

        var results = ArchiveTreeSearch.Filter([folder], "documents");
        var matches = ArchiveTreeSearch.GetNavigationMatches(results);

        Assert.Equal(2, matches.Count, "matching files are navigation targets");
        Assert.Equal("notes.txt", matches[0].Name,
            "navigation follows the visible archive ordering");
        Assert.Equal("documents", matches[1].SearchQuery,
            "navigation entries retain the active highlight query");
    }

    public void MatchPositionIsOneBasedAndHandlesAnEmptySet()
    {
        Assert.Equal("1 / 3",
            ArchiveSearchPresentation.FormatMatchPosition(0, 3),
            "the first zero-based index is displayed as one");
        Assert.Equal("3 / 3",
            ArchiveSearchPresentation.FormatMatchPosition(2, 3),
            "the last result keeps its total");
        Assert.Equal(string.Empty,
            ArchiveSearchPresentation.FormatMatchPosition(0, 0),
            "an empty result set has no position");
    }

    public void MatchNavigationWrapsInBothDirections()
    {
        Assert.Equal(0,
            ArchiveSearchPresentation.GetNextMatchIndex(2, 3, 1),
            "moving forward from the last result wraps to the first");
        Assert.Equal(2,
            ArchiveSearchPresentation.GetNextMatchIndex(0, 3, -1),
            "moving backward from the first result wraps to the last");
        Assert.Equal(0,
            ArchiveSearchPresentation.GetNextMatchIndex(-1, 3, 1),
            "starting without a selection moves to the first result");
    }

    public void DisplayPathNormalizesSeparatorsAndFallsBackToTheName()
    {
        var report = new ArchiveFileEntry(
            "report.pdf",
            false,
            archivePath: "docs/nested/report.pdf");
        var root = new ArchiveFileEntry("archive.zip", true);

        Assert.Equal(@"docs\nested\report.pdf", report.DisplayPath,
            "the complete entry path uses Windows separators");
        Assert.Equal("archive.zip", root.DisplayPath,
            "an entry without an internal path falls back to its name");
    }

    public void FilenameHighlightMarksEveryMatchingSegment()
    {
        var block = new SearchHighlightTextBlock
        {
            Content = "Report report.pdf",
            SearchQuery = "report"
        };

        var runs = block.Inlines.OfType<Run>().ToList();

        Assert.Equal(4, runs.Count, "matching segments and the text between them are separate runs");
        Assert.Equal("Report", runs[0].Text, "the first match is highlighted");
        Assert.Equal(FontWeights.SemiBold, runs[0].FontWeight, "matches use a strong weight");
        Assert.Equal(" ", runs[1].Text, "ordinary text remains readable");
        Assert.Equal("report", runs[2].Text, "all case-insensitive matches are highlighted");
        Assert.Equal(".pdf", runs[3].Text, "the unmatched suffix remains visible");
    }
}
