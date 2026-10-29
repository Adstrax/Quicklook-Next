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
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace QuickLookNext;

/// <summary>
/// A deliberately small Markdown renderer for GitHub release notes. It covers
/// headings, paragraphs, lists, quotes, fenced code and links without pulling a
/// web rendering stack into the update window.
/// </summary>
internal static class ReleaseNotesFormatter
{
    private static readonly Regex HeadingPattern =
        new(@"^\s{0,3}(#{1,6})\s+(.+?)\s*$", RegexOptions.Compiled);

    private static readonly Regex BulletPattern =
        new(@"^\s*[-*+]\s+(.+?)\s*$", RegexOptions.Compiled);

    private static readonly Regex NumberedPattern =
        new(@"^\s*\d+[.)]\s+(.+?)\s*$", RegexOptions.Compiled);

    private static readonly Regex RulePattern =
        new(@"^\s*([-*_])(?:\s*\1){2,}\s*$", RegexOptions.Compiled);

    private static readonly Regex HtmlTagPattern =
        new(@"<[^>]+>", RegexOptions.Compiled);

    internal static FlowDocument BuildDocument(string markdown, bool isDark, Action<string> openUrl)
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

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        List list = null;
        TextMarkerStyle marker = TextMarkerStyle.Disc;

        void FinishList()
        {
            list = null;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FinishList();
                var code = new StringBuilder();
                while (++i < lines.Length &&
                       !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
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

            var heading = HeadingPattern.Match(line);
            if (heading.Success)
            {
                FinishList();
                var level = heading.Groups[1].Value.Length;
                var paragraph = new Paragraph
                {
                    FontSize = level <= 2 ? 15d : 13.5d,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, level <= 2 ? 8 : 5, 0, 5),
                };
                AddInlines(paragraph, heading.Groups[2].Value, isDark, accent, secondary, openUrl);
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

            var bullet = BulletPattern.Match(line);
            var numbered = NumberedPattern.Match(line);
            if (bullet.Success || numbered.Success)
            {
                var desiredMarker = numbered.Success ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                if (list is null || marker != desiredMarker)
                {
                    list = new List
                    {
                        MarkerStyle = desiredMarker,
                        Margin = new Thickness(0, 0, 0, 8),
                        Padding = new Thickness(22, 0, 0, 0),
                    };
                    marker = desiredMarker;
                    document.Blocks.Add(list);
                }

                var itemParagraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                AddInlines(
                    itemParagraph,
                    bullet.Success ? bullet.Groups[1].Value : numbered.Groups[1].Value,
                    isDark,
                    accent,
                    secondary,
                    openUrl);
                list.ListItems.Add(new ListItem(itemParagraph));
                continue;
            }

            FinishList();
            if (trimmed.StartsWith('>'))
            {
                var quoteParagraph = new Paragraph
                {
                    Foreground = secondary,
                    FontStyle = FontStyles.Italic,
                    Padding = new Thickness(10, 2, 0, 2),
                    Margin = new Thickness(0, 2, 0, 8),
                };
                AddInlines(
                    quoteParagraph,
                    trimmed.TrimStart('>', ' '),
                    isDark,
                    accent,
                    secondary,
                    openUrl);
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
                    next.TrimStart().StartsWith("```", StringComparison.Ordinal) ||
                    next.TrimStart().StartsWith('>'))
                {
                    break;
                }

                paragraphLines.Append(' ').Append(next.Trim());
                i++;
            }

            var bodyParagraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AddInlines(bodyParagraph, paragraphLines.ToString(), isDark, accent, secondary, openUrl);
            document.Blocks.Add(bodyParagraph);
        }

        return document;
    }

    private static void AddInlines(
        Paragraph paragraph,
        string source,
        bool isDark,
        Brush accent,
        Brush secondary,
        Action<string> openUrl)
    {
        var text = WebUtility.HtmlDecode(HtmlTagPattern.Replace(source, string.Empty));
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0)
                return;

            paragraph.Inlines.Add(new Run(plain.ToString()));
            plain.Clear();
        }

        for (var i = 0; i < text.Length;)
        {
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushPlain();
                    paragraph.Inlines.Add(new Run(text[(i + 2)..end])
                    {
                        FontWeight = FontWeights.SemiBold,
                    });
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    FlushPlain();
                    paragraph.Inlines.Add(new Run(text[(i + 1)..end])
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 12,
                        Foreground = accent,
                    });
                    i = end + 1;
                    continue;
                }
            }

            if (text[i] == '[')
            {
                var labelEnd = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                var urlEnd = labelEnd > i ? text.IndexOf(')', labelEnd + 2) : -1;
                if (labelEnd > i && urlEnd > labelEnd + 2)
                {
                    var label = text[(i + 1)..labelEnd];
                    var url = text[(labelEnd + 2)..urlEnd];
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                        uri.Scheme is "http" or "https")
                    {
                        FlushPlain();
                        paragraph.Inlines.Add(CreateHyperlink(label, uri, accent, openUrl));
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }

            var startsWithHttps = i + 8 <= text.Length &&
                text.AsSpan(i, 8).Equals("https://", StringComparison.OrdinalIgnoreCase);
            var startsWithHttp = i + 7 <= text.Length &&
                text.AsSpan(i, 7).Equals("http://", StringComparison.OrdinalIgnoreCase);
            if (startsWithHttps || startsWithHttp)
            {
                var end = i;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) &&
                       text[end] is not ')' and not ']' and not '>')
                {
                    end++;
                }

                var url = text[i..end].TrimEnd('.', ',', ';', ':');
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https")
                {
                    FlushPlain();
                    paragraph.Inlines.Add(CreateHyperlink(url, uri, accent, openUrl));
                    i = end;
                    continue;
                }
            }

            plain.Append(text[i++]);
        }

        FlushPlain();
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
