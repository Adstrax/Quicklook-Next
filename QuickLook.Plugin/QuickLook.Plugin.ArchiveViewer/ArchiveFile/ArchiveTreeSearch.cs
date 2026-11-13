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

using System;
using System.Collections.Generic;
using System.Threading;

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

internal static class ArchiveTreeSearch
{
    public static List<ArchiveFileEntry> Filter(
        IEnumerable<ArchiveFileEntry> entries,
        string query,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ArchiveFileEntry>();
        var terms = ParseTerms(query);
        if (terms.Length == 0)
            return results;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var branch = BuildBranch(entry, terms, cancellationToken);
            if (branch != null)
                results.Add(Materialize(branch, null, query));
        }

        return results;
    }

    internal static string[] ParseTerms(string query)
    {
        return string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries);
    }

    private static SearchBranch BuildBranch(
        ArchiveFileEntry entry,
        IReadOnlyList<string> terms,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<SearchBranch> children = null;
        foreach (var child in entry.Children.Keys)
        {
            var childBranch = BuildBranch(child, terms, cancellationToken);
            if (childBranch == null)
                continue;

            children ??= [];
            children.Add(childBranch);
        }

        var isMatch = Matches(entry, terms);
        if (!isMatch && children == null)
            return null;

        return new SearchBranch(entry, children ?? [], isMatch);
    }

    private static bool Matches(ArchiveFileEntry entry, IReadOnlyList<string> terms)
    {
        var name = entry.Name ?? string.Empty;
        var path = (entry.ArchivePath ?? string.Empty).Replace('/', '\\');

        foreach (var term in terms)
        {
            var normalizedTerm = term.Replace('/', '\\');
            if (name.IndexOf(normalizedTerm, StringComparison.OrdinalIgnoreCase) < 0 &&
                path.IndexOf(normalizedTerm, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static ArchiveFileEntry Materialize(
        SearchBranch branch,
        ArchiveFileEntry parent,
        string query)
    {
        var source = branch.Source;
        var entry = new ArchiveFileEntry(
            source.Name,
            source.IsFolder,
            parent,
            source.ArchivePath)
        {
            Encrypted = source.Encrypted,
            Size = source.Size,
            ModifiedDate = source.ModifiedDate,
            IsExpanded = source.IsFolder && branch.Children.Count > 0,
            IsSearchMatch = branch.IsMatch,
            SearchQuery = query
        };

        foreach (var child in branch.Children)
            _ = Materialize(child, entry, query);

        return entry;
    }

    public static List<ArchiveFileEntry> GetNavigationMatches(
        IEnumerable<ArchiveFileEntry> results)
    {
        var files = new List<ArchiveFileEntry>();
        var folders = new List<ArchiveFileEntry>();

        foreach (var entry in results)
            CollectMatches(entry, files, folders);

        return files.Count > 0 ? files : folders;
    }

    private static void CollectMatches(
        ArchiveFileEntry entry,
        List<ArchiveFileEntry> files,
        List<ArchiveFileEntry> folders)
    {
        if (entry.IsSearchMatch)
        {
            if (entry.IsFolder)
                folders.Add(entry);
            else
                files.Add(entry);
        }

        foreach (var child in entry.Children.Keys)
            CollectMatches(child, files, folders);
    }

    private sealed record SearchBranch(
        ArchiveFileEntry Source,
        List<SearchBranch> Children,
        bool IsMatch);
}
