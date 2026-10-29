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
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DocumentList = System.Windows.Documents.List;

namespace QuickLookNext;

/// <summary>
/// A desktop Markdown renderer for GitHub release notes. It covers common
/// GitHub-Flavored Markdown and the small HTML subset used by generated release
/// bodies without pulling a web rendering stack into the update window.
/// </summary>
internal static class ReleaseNotesFormatter
{
    private static readonly Regex HeadingPattern =
        new(@"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);

    private static readonly Regex BulletPattern =
        new(@"^\s*[-*+]\s+(.+?)\s*$", RegexOptions.Compiled);

    private static readonly Regex ListPattern =
        new(@"^(?<indent> *)(?<marker>[-*+]|\d+[.)])\s+(?<text>.*)$", RegexOptions.Compiled);

    private static readonly Regex NumberedPattern =
        new(@"^\s*\d+[.)]\s+(.+?)\s*$", RegexOptions.Compiled);

    private static readonly Regex TaskPattern =
        new(@"^\[(?<state>[ xX])\]\s*(?<text>.*)$", RegexOptions.Compiled);

    private static readonly Regex RulePattern =
        new(@"^\s*([-*_])(?:\s*\1){2,}\s*$", RegexOptions.Compiled);

    private static readonly Regex SetextPattern =
        new(@"^\s{0,3}(?<marker>=+|-+)\s*$", RegexOptions.Compiled);

    private static readonly Regex FencePattern =
        new(@"^\s{0,3}(?<fence>`{3,}|~{3,})(?<info>.*)$", RegexOptions.Compiled);

    private static readonly Regex ReferenceDefinitionPattern =
        new(@"^\s{0,3}\[(?<label>[^\]]+)\]:\s*<?(?<url>[^\s>]+)>?(?:\s+.*)?$",
            RegexOptions.Compiled);

    private static readonly Regex HtmlTagPattern =
        new(@"<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex HtmlCommentPattern =
        new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex HtmlBreakPattern =
        new(@"<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HtmlAutoLinkPattern =
        new(@"<(?<url>https?://[^>\s]+)>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HtmlAnchorPattern =
        new(@"<a\b(?<attributes>[^>]*)>(?<label>.*?)</a\s*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HtmlImagePattern =
        new(@"<img\b(?<attributes>[^>]*)/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HtmlHeadingOpenPattern =
        new(@"<h(?<level>[1-6])\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HtmlHeadingClosePattern =
        new(@"</h[1-6]\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HtmlAttributePattern =
        new(@"\b(?<name>href|src|alt)\s*=\s*(?<quote>[""'])(?<value>.*?)\k<quote>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    internal static FlowDocument BuildDocument(
        string markdown,
        bool isDark,
        Action<string> openUrl,
        Uri linkBase = null)
    {
        var text = ThemePalette.Text(isDark);
        var secondary = ThemePalette.SecondaryText(isDark);
        var accent = ThemePalette.Accent(isDark);
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = text,
            LineHeight = 20,
        };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            document.Blocks.Add(new Paragraph(new Run("No release notes were provided for this version."))
            {
                Foreground = secondary,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0),
            });
            return document;
        }

        var lines = NormalizeMarkdown(markdown);
        var references = CollectReferenceDefinitions(lines);
        var listStack = new List<ListContext>();

        void FinishList()
        {
            listStack.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            var fence = FencePattern.Match(line);
            if (fence.Success)
            {
                FinishList();
                var fenceToken = fence.Groups["fence"].Value;
                var code = new StringBuilder();
                while (++i < lines.Length && !IsFenceClose(lines[i], fenceToken))
                {
                    if (code.Length > 0)
                        code.AppendLine();
                    code.Append(lines[i]);
                }

                var codeParagraph = new Paragraph(new Run(code.ToString()))
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12,
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 4, 0, 10),
                    Background = ThemePalette.ButtonBg(isDark),
                };
                document.Blocks.Add(codeParagraph);
                continue;
            }

            if (trimmed.Length == 0)
            {
                FinishList();
                continue;
            }

            if (ReferenceDefinitionPattern.IsMatch(line))
            {
                FinishList();
                continue;
            }

            if (TryAddTable(
                    lines,
                    ref i,
                    document,
                    isDark,
                    accent,
                    secondary,
                    openUrl,
                    references,
                    linkBase))
            {
                FinishList();
                continue;
            }

            if (i + 1 < lines.Length &&
                trimmed.Length > 0 &&
                !trimmed.StartsWith('|') &&
                !ListPattern.IsMatch(line) &&
                !RulePattern.IsMatch(line) &&
                SetextPattern.IsMatch(lines[i + 1]))
            {
                FinishList();
                var level = lines[i + 1].TrimStart().StartsWith('=')
                    ? 1
                    : 2;
                var paragraph = CreateHeading(level);
                AddInlines(
                    paragraph,
                    trimmed,
                    isDark,
                    accent,
                    secondary,
                    openUrl,
                    references,
                    linkBase);
                document.Blocks.Add(paragraph);
                i++;
                continue;
            }

            var heading = HeadingPattern.Match(line);
            if (heading.Success)
            {
                FinishList();
                var level = heading.Groups[1].Value.Length;
                var paragraph = CreateHeading(level);
                AddInlines(
                    paragraph,
                    heading.Groups[2].Value,
                    isDark,
                    accent,
                    secondary,
                    openUrl,
                    references,
                    linkBase);
                document.Blocks.Add(paragraph);
                continue;
            }

            if (RulePattern.IsMatch(line))
            {
                FinishList();
                document.Blocks.Add(new BlockUIContainer(new Border
                {
                    Height = 1,
                    Background = ThemePalette.Border(isDark),
                    Margin = new Thickness(0, 8, 0, 10),
                }));
                continue;
            }

            var listItem = ListPattern.Match(line);
            if (listItem.Success)
            {
                var indent = listItem.Groups["indent"].Value.Length;
                var markerText = listItem.Groups["marker"].Value;
                var desiredMarker = markerText[0] is >= '0' and <= '9'
                    ? TextMarkerStyle.Decimal
                    : TextMarkerStyle.Disc;

                while (listStack.Count > 0)
                {
                    var current = listStack[^1];
                    if (current.Indent < indent ||
                        current.Indent == indent && current.Marker == desiredMarker)
                    {
                        break;
                    }

                    listStack.RemoveAt(listStack.Count - 1);
                }

                ListContext context;
                if (listStack.Count > 0 && listStack[^1].Indent == indent)
                {
                    context = listStack[^1];
                }
                else
                {
                    BlockCollection parent;
                    if (listStack.Count == 0)
                    {
                        parent = document.Blocks;
                    }
                    else
                    {
                        parent = listStack[^1].LastItem.Blocks;
                    }

                    var list = new DocumentList
                    {
                        MarkerStyle = desiredMarker,
                        Margin = new Thickness(0, 0, 0, 8),
                        Padding = new Thickness(22, 0, 0, 0),
                    };
                    parent.Add(list);
                    context = new ListContext(indent, desiredMarker, list);
                    listStack.Add(context);
                }

                var itemText = listItem.Groups["text"].Value;
                var task = TaskPattern.Match(itemText);
                if (task.Success)
                {
                    var checkedMark = task.Groups["state"].Value is "x" or "X"
                        ? "☑ "
                        : "☐ ";
                    itemText = checkedMark + task.Groups["text"].Value;
                }

                var itemParagraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                AddInlines(
                    itemParagraph,
                    itemText,
                    isDark,
                    accent,
                    secondary,
                    openUrl,
                    references,
                    linkBase);
                context.AddItem(itemParagraph);
                continue;
            }

            if (listStack.Count > 0 &&
                line.Length - line.TrimStart().Length > listStack[^1].Indent)
            {
                var continuation = listStack[^1].LastParagraph;
                continuation.Inlines.Add(new Run(" "));
                AddInlines(
                    continuation,
                    trimmed,
                    isDark,
                    accent,
                    secondary,
                    openUrl,
                    references,
                    linkBase);
                continue;
            }

            FinishList();
            if (trimmed.StartsWith('>'))
            {
                var quoteLines = new List<string>();
                while (true)
                {
                    var quoteText = lines[i].TrimStart();
                    quoteLines.Add(quoteText.Length > 1
                        ? quoteText[1..].TrimStart()
                        : string.Empty);
                    if (i + 1 >= lines.Length ||
                        !lines[i + 1].TrimStart().StartsWith('>'))
                    {
                        break;
                    }

                    i++;
                }

                var quote = NormalizeGitHubAlert(string.Join("\n", quoteLines));
                var quoteParagraph = new Paragraph
                {
                    Foreground = secondary,
                    FontStyle = FontStyles.Italic,
                    BorderBrush = ThemePalette.Border(isDark),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(10, 2, 0, 2),
                    Margin = new Thickness(0, 2, 0, 8),
                };
                AddInlines(
                    quoteParagraph,
                    quote,
                    isDark,
                    accent,
                    secondary,
                    openUrl,
                    references,
                    linkBase);
                document.Blocks.Add(quoteParagraph);
                continue;
            }

            var paragraphLines = new StringBuilder(trimmed);
            while (i + 1 < lines.Length)
            {
                var next = lines[i + 1];
                if (string.IsNullOrWhiteSpace(next) ||
                    HeadingPattern.IsMatch(next) ||
                    BulletPattern.IsMatch(next) ||
                    NumberedPattern.IsMatch(next) ||
                    RulePattern.IsMatch(next) ||
                    FencePattern.IsMatch(next) ||
                    ReferenceDefinitionPattern.IsMatch(next) ||
                    next.TrimStart().StartsWith('>'))
                {
                    break;
                }

                paragraphLines.Append(EndsWithHardBreak(lines[i]) ? '\n' : ' ');
                paragraphLines.Append(next.Trim());
                i++;
            }

            var bodyParagraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AddInlines(
                bodyParagraph,
                paragraphLines.ToString(),
                isDark,
                accent,
                secondary,
                openUrl,
                references,
                linkBase);
            document.Blocks.Add(bodyParagraph);
        }

        return document;
    }

    private sealed class ListContext
    {
        internal ListContext(int indent, TextMarkerStyle marker, DocumentList list)
        {
            Indent = indent;
            Marker = marker;
            List = list;
        }

        internal int Indent { get; }

        internal TextMarkerStyle Marker { get; }

        internal DocumentList List { get; }

        internal Paragraph LastParagraph { get; private set; }

        internal ListItem LastItem { get; private set; }

        internal void AddItem(Paragraph paragraph)
        {
            LastItem = new ListItem(paragraph);
            List.ListItems.Add(LastItem);
            LastParagraph = paragraph;
        }
    }

    private static Paragraph CreateHeading(int level)
    {
        return new Paragraph
        {
            FontSize = level <= 2 ? 15d : 13.5d,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, level <= 2 ? 8 : 5, 0, 5),
        };
    }

    private static string[] NormalizeMarkdown(string markdown)
    {
        var source = markdown
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace("\t", "    ")
            .Split('\n');
        var output = new List<string>(source.Length);
        var inFence = false;
        string fenceToken = null;
        var inHtmlComment = false;

        foreach (var rawLine in source)
        {
            var fence = FencePattern.Match(rawLine);
            if (inFence)
            {
                output.Add(rawLine);
                if (fence.Success && IsFenceClose(rawLine, fenceToken))
                    inFence = false;
                continue;
            }

            if (fence.Success)
            {
                inFence = true;
                fenceToken = fence.Groups["fence"].Value;
                output.Add(rawLine);
                continue;
            }

            var line = RemoveHtmlComments(rawLine, ref inHtmlComment);
            line = NormalizeHtmlLine(line);
            output.AddRange(line.Split('\n'));
        }

        return output.ToArray();
    }

    private static string RemoveHtmlComments(string line, ref bool inComment)
    {
        var result = new StringBuilder();
        var offset = 0;
        while (offset < line.Length)
        {
            if (inComment)
            {
                var end = line.IndexOf("-->", offset, StringComparison.Ordinal);
                if (end < 0)
                    return result.ToString();

                inComment = false;
                offset = end + 3;
                continue;
            }

            var start = line.IndexOf("<!--", offset, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(line, offset, line.Length - offset);
                break;
            }

            result.Append(line, offset, start - offset);
            inComment = true;
            offset = start + 4;
        }

        return result.ToString();
    }

    private static string NormalizeHtmlLine(string line)
    {
        line = HtmlAutoLinkPattern.Replace(
            line,
            match => $"[{match.Groups["url"].Value}]({match.Groups["url"].Value})");
        line = HtmlBreakPattern.Replace(line, "  \n");
        line = HtmlAnchorPattern.Replace(
            line,
            match =>
            {
                var href = ExtractHtmlAttribute(match.Groups["attributes"].Value, "href");
                var label = HtmlTagPattern.Replace(match.Groups["label"].Value, string.Empty).Trim();
                return href.Length > 0 && label.Length > 0
                    ? $"[{label.Replace("]", "\\]")}]({href})"
                    : label;
            });
        line = HtmlImagePattern.Replace(
            line,
            match =>
            {
                var attributes = match.Groups["attributes"].Value;
                var source = ExtractHtmlAttribute(attributes, "src");
                var alt = ExtractHtmlAttribute(attributes, "alt");
                if (source.Length == 0)
                    return alt;

                alt = string.IsNullOrWhiteSpace(alt) ? source : alt;
                return $"![{alt.Replace("]", "\\]")}]({source})";
            });
        line = HtmlHeadingOpenPattern.Replace(
            line,
            match =>
            {
                var level = int.Parse(match.Groups["level"].Value);
                return "\n" + new string('#', level) + " ";
            });
        line = HtmlHeadingClosePattern.Replace(line, "\n");
        line = Regex.Replace(
            line,
            @"<summary\b[^>]*>",
            "\n**",
            RegexOptions.IgnoreCase);
        line = Regex.Replace(
            line,
            @"</summary\s*>",
            "**\n",
            RegexOptions.IgnoreCase);
        line = Regex.Replace(
            line,
            @"<li\b[^>]*>",
            "\n- ",
            RegexOptions.IgnoreCase);
        line = Regex.Replace(
            line,
            @"</(?:p|div|section|article|header|footer|details|summary|ul|ol|li|h[1-6]|table|thead|tbody|tfoot|tr|blockquote)\s*>",
            "\n",
            RegexOptions.IgnoreCase);
        line = Regex.Replace(
            line,
            @"<(?:p|div|section|article|header|footer|details|ul|ol|table|thead|tbody|tfoot|tr|blockquote)\b[^>]*>",
            "\n",
            RegexOptions.IgnoreCase);
        line = Regex.Replace(
            line,
            @"<(?:br)\b[^>]*>",
            "  \n",
            RegexOptions.IgnoreCase);
        line = Regex.Replace(
            line,
            @"</?(?:strong|b)\b[^>]*>",
            "**",
            RegexOptions.IgnoreCase);
        line = Regex.Replace(
            line,
            @"</?(?:em|i)\b[^>]*>",
            "*",
            RegexOptions.IgnoreCase);
        line = Regex.Replace(
            line,
            @"</?(?:del|s|strike)\b[^>]*>",
            "~~",
            RegexOptions.IgnoreCase);
        line = Regex.Replace(
            line,
            @"</?code\b[^>]*>",
            "`",
            RegexOptions.IgnoreCase);

        return HtmlTagPattern.Replace(line, string.Empty);
    }

    private static string ExtractHtmlAttribute(string attributes, string name)
    {
        foreach (Match match in HtmlAttributePattern.Matches(attributes))
        {
            if (match.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                return WebUtility.HtmlDecode(match.Groups["value"].Value);
        }

        return string.Empty;
    }

    private static Dictionary<string, string> CollectReferenceDefinitions(string[] lines)
    {
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inFence = false;
        string fenceToken = null;
        foreach (var line in lines)
        {
            var fence = FencePattern.Match(line);
            if (inFence)
            {
                if (fence.Success && IsFenceClose(line, fenceToken))
                    inFence = false;
                continue;
            }

            if (fence.Success)
            {
                inFence = true;
                fenceToken = fence.Groups["fence"].Value;
                continue;
            }

            var definition = ReferenceDefinitionPattern.Match(line);
            if (definition.Success)
            {
                references[NormalizeReferenceId(definition.Groups["label"].Value)] =
                    definition.Groups["url"].Value;
            }
        }

        return references;
    }

    private static string NormalizeReferenceId(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ").ToLowerInvariant();
    }

    private static bool TryAddTable(
        string[] lines,
        ref int index,
        FlowDocument document,
        bool isDark,
        Brush accent,
        Brush secondary,
        Action<string> openUrl,
        IReadOnlyDictionary<string, string> references,
        Uri linkBase)
    {
        if (index + 1 >= lines.Length ||
            !lines[index].Contains('|') &&
            !lines[index + 1].Contains('|'))
        {
            return false;
        }

        var headers = SplitTableRow(lines[index]);
        if (headers.Count == 0 || !IsTableDelimiter(lines[index + 1], headers.Count))
            return false;

        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, 10),
            BorderBrush = ThemePalette.Border(isDark),
            BorderThickness = new Thickness(1),
        };
        foreach (var _ in headers)
            table.Columns.Add(new TableColumn());

        var rows = new TableRowGroup();
        table.RowGroups.Add(rows);
        AddTableRow(
            rows,
            headers,
            headers.Count,
            header: true,
            isDark,
            accent,
            secondary,
            openUrl,
            references,
            linkBase);

        index++;
        while (index + 1 < lines.Length)
        {
            var next = lines[index + 1];
            if (string.IsNullOrWhiteSpace(next) ||
                !next.Contains('|') ||
                FencePattern.IsMatch(next) ||
                HeadingPattern.IsMatch(next) ||
                ListPattern.IsMatch(next) ||
                RulePattern.IsMatch(next))
            {
                break;
            }

            index++;
            AddTableRow(
                rows,
                SplitTableRow(lines[index]),
                headers.Count,
                header: false,
                isDark,
                accent,
                secondary,
                openUrl,
                references,
                linkBase);
        }

        document.Blocks.Add(table);
        return true;
    }

    private static List<string> SplitTableRow(string line)
    {
        var value = line.Trim();
        if (value.StartsWith('|'))
            value = value[1..];
        if (value.EndsWith('|'))
            value = value[..^1];

        var cells = new List<string>();
        foreach (var cell in Regex.Split(value, @"(?<!\\)\|"))
            cells.Add(cell.Replace("\\|", "|").Trim());
        return cells;
    }

    private static bool IsTableDelimiter(string line, int expectedCells)
    {
        var cells = SplitTableRow(line);
        if (cells.Count != expectedCells)
            return false;

        foreach (var cell in cells)
        {
            if (!Regex.IsMatch(cell, @"^:?-{3,}:?$"))
                return false;
        }

        return true;
    }

    private static void AddTableRow(
        TableRowGroup rows,
        IReadOnlyList<string> cells,
        int columnCount,
        bool header,
        bool isDark,
        Brush accent,
        Brush secondary,
        Action<string> openUrl,
        IReadOnlyDictionary<string, string> references,
        Uri linkBase)
    {
        var row = new TableRow();
        if (header)
            row.Background = ThemePalette.ButtonBg(isDark);

        for (var i = 0; i < columnCount; i++)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            if (header)
                paragraph.FontWeight = FontWeights.SemiBold;
            AddInlines(
                paragraph,
                i < cells.Count ? cells[i] : string.Empty,
                isDark,
                accent,
                secondary,
                openUrl,
                references,
                linkBase);
            row.Cells.Add(new TableCell(paragraph)
            {
                Padding = new Thickness(7, 4, 7, 4),
                BorderBrush = ThemePalette.Border(isDark),
                BorderThickness = new Thickness(0, 0, 1, 1),
            });
        }

        rows.Rows.Add(row);
    }

    private static bool IsFenceClose(string line, string openingFence)
    {
        if (string.IsNullOrEmpty(openingFence))
            return false;

        var trimmed = line.TrimStart();
        var fenceCharacter = openingFence[0];
        var count = 0;
        while (count < trimmed.Length && trimmed[count] == fenceCharacter)
            count++;

        return count >= openingFence.Length &&
               trimmed[count..].Trim().Length == 0;
    }

    private static bool EndsWithHardBreak(string line)
    {
        return line.EndsWith("  ", StringComparison.Ordinal) ||
               line.EndsWith('\\');
    }

    private static string NormalizeGitHubAlert(string value)
    {
        return Regex.Replace(
            value,
            @"^\[!(?<kind>NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*",
            match =>
            {
                var kind = match.Groups["kind"].Value;
                return char.ToUpperInvariant(kind[0]) + kind[1..].ToLowerInvariant() + ": ";
            },
            RegexOptions.IgnoreCase);
    }

    private static void AddInlines(
        Paragraph paragraph,
        string source,
        bool isDark,
        Brush accent,
        Brush secondary,
        Action<string> openUrl,
        IReadOnlyDictionary<string, string> references = null,
        Uri linkBase = null)
    {
        AddInlines(
            paragraph.Inlines,
            source,
            isDark,
            accent,
            secondary,
            openUrl,
            references,
            linkBase);
    }

    private static void AddInlines(
        InlineCollection inlines,
        string source,
        bool isDark,
        Brush accent,
        Brush secondary,
        Action<string> openUrl,
        IReadOnlyDictionary<string, string> references = null,
        Uri linkBase = null)
    {
        var text = WebUtility.HtmlDecode(HtmlTagPattern.Replace(source ?? string.Empty, string.Empty))
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0)
                return;

            inlines.Add(new Run(plain.ToString()));
            plain.Clear();
        }

        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '\n')
            {
                FlushPlain();
                inlines.Add(new LineBreak());
                i++;
                continue;
            }

            if (text[i] == '\\' &&
                i + 1 < text.Length &&
                char.IsPunctuation(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (i + 2 < text.Length &&
                (text.AsSpan(i, 3).SequenceEqual("***") || text.AsSpan(i, 3).SequenceEqual("___")))
            {
                var marker = text.Substring(i, 3);
                var end = text.IndexOf(marker, i + 3, StringComparison.Ordinal);
                if (end > i + 3)
                {
                    FlushPlain();
                    var span = new Span
                    {
                        FontWeight = FontWeights.SemiBold,
                        FontStyle = FontStyles.Italic,
                    };
                    AddInlines(
                        span.Inlines,
                        text[(i + 3)..end],
                        isDark,
                        accent,
                        secondary,
                        openUrl,
                        references,
                        linkBase);
                    inlines.Add(span);
                    i = end + 3;
                    continue;
                }
            }

            if (i + 1 < text.Length &&
                (text.AsSpan(i, 2).SequenceEqual("**") || text.AsSpan(i, 2).SequenceEqual("__")))
            {
                var marker = text.Substring(i, 2);
                var end = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushPlain();
                    var span = new Span { FontWeight = FontWeights.SemiBold };
                    AddInlines(
                        span.Inlines,
                        text[(i + 2)..end],
                        isDark,
                        accent,
                        secondary,
                        openUrl,
                        references,
                        linkBase);
                    inlines.Add(span);
                    i = end + 2;
                    continue;
                }
            }

            if (i + 1 < text.Length &&
                text[i] == '~' &&
                text[i + 1] == '~')
            {
                var end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushPlain();
                    var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                    AddInlines(
                        span.Inlines,
                        text[(i + 2)..end],
                        isDark,
                        accent,
                        secondary,
                        openUrl,
                        references,
                        linkBase);
                    inlines.Add(span);
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] is '*' or '_')
            {
                var marker = text[i];
                var canUseUnderscore = marker != '_' ||
                    i == 0 ||
                    !char.IsLetterOrDigit(text[i - 1]);
                var end = canUseUnderscore
                    ? text.IndexOf(marker, i + 1)
                    : -1;
                if (end > i + 1 && !char.IsWhiteSpace(text[i + 1]))
                {
                    FlushPlain();
                    var span = new Span { FontStyle = FontStyles.Italic };
                    AddInlines(
                        span.Inlines,
                        text[(i + 1)..end],
                        isDark,
                        accent,
                        secondary,
                        openUrl,
                        references,
                        linkBase);
                    inlines.Add(span);
                    i = end + 1;
                    continue;
                }
            }

            if (text[i] == '`')
            {
                var tickCount = 1;
                while (i + tickCount < text.Length && text[i + tickCount] == '`')
                    tickCount++;

                var marker = new string('`', tickCount);
                var end = text.IndexOf(marker, i + tickCount, StringComparison.Ordinal);
                if (end > i + tickCount)
                {
                    FlushPlain();
                    var code = text[(i + tickCount)..end].Trim();
                    inlines.Add(new Run(code)
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 12,
                        Foreground = accent,
                    });
                    i = end + tickCount;
                    continue;
                }
            }

            if (TryParseMarkdownLink(
                    text,
                    i,
                    references,
                    out var isImage,
                    out var label,
                    out var target,
                    out var consumed))
            {
                if (ResolveHttpUri(target, linkBase, out var uri))
                {
                    FlushPlain();
                    var cleanLabel = UnescapeMarkdownLabel(label);
                    if (string.IsNullOrWhiteSpace(cleanLabel))
                        cleanLabel = uri.AbsoluteUri;
                    if (isImage)
                        cleanLabel = "Image: " + cleanLabel;
                    inlines.Add(CreateHyperlink(cleanLabel, uri, accent, openUrl));
                    i = consumed;
                    continue;
                }
            }

            if (TryReadBareUrl(text, i, out var bareUrl))
            {
                if (ResolveHttpUri(bareUrl, linkBase, out var uri))
                {
                    FlushPlain();
                    inlines.Add(CreateHyperlink(bareUrl, uri, accent, openUrl));
                    i += bareUrl.Length;
                    continue;
                }
            }

            plain.Append(text[i++]);
        }

        FlushPlain();
    }

    private static bool TryParseMarkdownLink(
        string text,
        int start,
        IReadOnlyDictionary<string, string> references,
        out bool isImage,
        out string label,
        out string target,
        out int consumed)
    {
        isImage = start + 1 < text.Length &&
            text[start] == '!' &&
            text[start + 1] == '[';
        label = string.Empty;
        target = string.Empty;
        consumed = start;

        var bracket = isImage ? start + 1 : start;
        if (bracket >= text.Length || text[bracket] != '[')
            return false;

        var labelEnd = text.IndexOf(']', bracket + 1);
        if (labelEnd < 0)
            return false;

        label = text[(bracket + 1)..labelEnd];
        var tail = labelEnd + 1;
        if (tail < text.Length && text[tail] == '(')
        {
            var destinationEnd = FindClosingParenthesis(text, tail + 1);
            if (destinationEnd <= tail + 1)
                return false;

            target = ExtractLinkDestination(text[(tail + 1)..destinationEnd]);
            consumed = destinationEnd + 1;
            return target.Length > 0;
        }

        if (tail < text.Length && text[tail] == '[')
        {
            var referenceEnd = text.IndexOf(']', tail + 1);
            if (referenceEnd < 0)
                return false;

            var referenceId = text[(tail + 1)..referenceEnd];
            if (referenceId.Length == 0)
                referenceId = label;
            if (references is not null &&
                references.TryGetValue(NormalizeReferenceId(referenceId), out target))
            {
                consumed = referenceEnd + 1;
                return true;
            }
        }

        if (references is not null &&
            references.TryGetValue(NormalizeReferenceId(label), out target))
        {
            consumed = tail;
            return true;
        }

        return false;
    }

    private static int FindClosingParenthesis(string text, int start)
    {
        var depth = 1;
        var inAngleDestination = false;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '<')
                inAngleDestination = true;
            else if (text[i] == '>')
                inAngleDestination = false;
            else if (!inAngleDestination && text[i] == '(')
                depth++;
            else if (!inAngleDestination && text[i] == ')' && --depth == 0)
                return i;
        }

        return -1;
    }

    private static string ExtractLinkDestination(string value)
    {
        value = value.Trim();
        if (value.StartsWith('<'))
        {
            var end = value.IndexOf('>');
            return end > 0 ? value[1..end].Trim() : string.Empty;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
                return value[..i].Trim();
        }

        return value;
    }

    private static bool TryReadBareUrl(string text, int start, out string url)
    {
        url = string.Empty;
        var https = start + 8 <= text.Length &&
            text.AsSpan(start, 8).Equals("https://", StringComparison.OrdinalIgnoreCase);
        var http = start + 7 <= text.Length &&
            text.AsSpan(start, 7).Equals("http://", StringComparison.OrdinalIgnoreCase);
        if (!https && !http)
            return false;

        var end = start;
        while (end < text.Length &&
               !char.IsWhiteSpace(text[end]) &&
               text[end] is not ')' and not ']' and not '>' and not '\n')
        {
            end++;
        }

        url = text[start..end].TrimEnd('.', ',', ';', ':');
        return url.Length > 0;
    }

    private static bool ResolveHttpUri(string value, Uri baseUri, out Uri uri)
    {
        uri = null;
        value = value?.Trim().Trim('<', '>');
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme is not ("http" or "https"))
                return false;

            uri = absolute;
            return true;
        }

        if (baseUri is not null &&
            Uri.TryCreate(baseUri, value, out var relative) &&
            relative.Scheme is "http" or "https")
        {
            uri = relative;
            return true;
        }

        return false;
    }

    private static string UnescapeMarkdownLabel(string value)
    {
        value = WebUtility.HtmlDecode(HtmlTagPattern.Replace(value ?? string.Empty, string.Empty));
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' &&
                i + 1 < value.Length &&
                char.IsPunctuation(value[i + 1]))
            {
                result.Append(value[++i]);
            }
            else if (value[i] is not ('*' or '_' or '~' or '`'))
            {
                result.Append(value[i]);
            }
        }

        return result.ToString().Trim();
    }

    private static Hyperlink CreateHyperlink(
        string label,
        Uri uri,
        Brush accent,
        Action<string> openUrl)
    {
        var link = new Hyperlink(new Run(label))
        {
            NavigateUri = uri,
            Foreground = accent,
            TextDecorations = TextDecorations.Underline,
            ToolTip = uri.AbsoluteUri,
        };
        link.Click += (_, _) => openUrl?.Invoke(uri.AbsoluteUri);
        return link;
    }
}
