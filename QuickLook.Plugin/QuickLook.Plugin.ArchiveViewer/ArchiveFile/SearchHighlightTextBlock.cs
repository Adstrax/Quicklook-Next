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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

public sealed class SearchHighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty ContentProperty =
        DependencyProperty.Register(
            nameof(Content),
            typeof(string),
            typeof(SearchHighlightTextBlock),
            new FrameworkPropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public static readonly DependencyProperty SearchQueryProperty =
        DependencyProperty.Register(
            nameof(SearchQuery),
            typeof(string),
            typeof(SearchHighlightTextBlock),
            new FrameworkPropertyMetadata(string.Empty, OnDisplayPropertyChanged));

    public string Content
    {
        get => (string)GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public string SearchQuery
    {
        get => (string)GetValue(SearchQueryProperty);
        set => SetValue(SearchQueryProperty, value);
    }

    private static void OnDisplayPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        ((SearchHighlightTextBlock)dependencyObject).RebuildInlines();
    }

    private void RebuildInlines()
    {
        Inlines.Clear();

        var text = Content ?? string.Empty;
        if (text.Length == 0)
            return;

        var ranges = FindMatchRanges(text, ArchiveTreeSearch.ParseTerms(SearchQuery));
        if (ranges.Count == 0)
        {
            Inlines.Add(new Run(text));
            return;
        }

        var offset = 0;
        foreach (var range in ranges)
        {
            if (range.Start > offset)
                Inlines.Add(new Run(text[offset..range.Start]));

            Inlines.Add(new Run(text.Substring(range.Start, range.Length))
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Gold
            });
            offset = range.Start + range.Length;
        }

        if (offset < text.Length)
            Inlines.Add(new Run(text[offset..]));
    }

    private static List<MatchRange> FindMatchRanges(string text, IReadOnlyList<string> terms)
    {
        var ranges = new List<MatchRange>();
        foreach (var term in terms)
        {
            if (term.Length == 0)
                continue;

            var offset = 0;
            while (offset < text.Length)
            {
                var index = text.IndexOf(
                    term,
                    offset,
                    StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    break;

                ranges.Add(new MatchRange(index, term.Length));
                offset = index + term.Length;
            }
        }

        if (ranges.Count < 2)
            return ranges;

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<MatchRange>(ranges.Count);
        foreach (var range in ranges)
        {
            if (merged.Count == 0)
            {
                merged.Add(range);
                continue;
            }

            var previous = merged[^1];
            if (range.Start <= previous.Start + previous.Length)
            {
                var end = Math.Max(
                    previous.Start + previous.Length,
                    range.Start + range.Length);
                merged[^1] = new MatchRange(previous.Start, end - previous.Start);
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private readonly record struct MatchRange(int Start, int Length);
}
