// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v3.11.0: turns a .docx into the page the Word preview shows - headings, formatted runs, lists, tables and
/// pictures - so the file can be read in the app's own window instead of through the Windows system preview
/// component.
/// <para>
/// v5.6.19: the preview is drawn as the pages the document is made of - cut where Word's own last layout cut
/// them, each on a white sheet of the section's paper size and margins, one page at a time, with the pages
/// listed in the panel's rail. Two pieces Word users notice first are drawn as part of that:
/// </para>
/// <list type="bullet">
/// <item><description>the table of contents. Its entries live inside <c>w:hyperlink</c> elements, and the
/// renderer used to look only at a paragraph's direct <c>w:r</c> children - so every entry came out as an
/// empty paragraph (the "目录 is blank" report). Runs are now collected through the inline wrappers
/// (<c>w:hyperlink</c>, <c>w:ins</c>, <c>w:smartTag</c>, <c>w:sdt</c>), while <c>w:instrText</c> stays out so
/// the field instruction itself cannot leak into the page;</description></item>
/// <item><description>the bookmark targets the TOC links to (<c>w:bookmarkStart w:name="_Toc..."</c>), which
/// are emitted as element ids so both the TOC and the rail can jump to a heading.</description></item>
/// </list>
/// </summary>
internal static class DocxToHtml
{
    private const int MaxParagraphs = 4000;
    private const int MaxImages = 64;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp =
        "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";

    internal static OfficePanelContent Convert(string path, string imageFolder, string imageHost)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var document = ReadXml(archive, "word/document.xml");
            var styles = ReadXml(archive, "word/styles.xml");
            var numbering = ReadXml(archive, "word/numbering.xml");
            var rels = ReadRels(archive, "word/_rels/document.xml.rels");
            if (document is null)
                return Failure("Not a valid Word document (word/document.xml is missing).");

            var styleNames = BuildStyleMap(styles);
            var numberingFormats = BuildNumberingFormats(numbering);

            var body = document.Root?.Element(W + "body");
            var geometry = ReadPageGeometry(document);
            var pages = new List<StringBuilder>();
            var pageLabels = new List<string>();
            var page = new StringBuilder();
            string pageLabel = null;
            var listBuffer = new List<string>();
            var listRun = 0;
            var paragraphCount = 0;
            var imageCount = 0;
            var headingIndex = 0;

            // v5.6.19: the page is the unit of the preview now, as it has always been in the PDF panel. Word
            // writes a w:lastRenderedPageBreak wherever its last layout put a boundary, so the document can be
            // cut where Word itself cut it, and an explicit w:br w:type="page" counts as well. The marker sits
            // at the end of the paragraph that closed a page about as often as at the start of the one that
            // opened the next, so the caller asks FindPageBreak which side of the marker the new page is on.
            void FlushPage()
            {
                if (page.Length == 0)
                    return;

                pages.Add(page);
                pageLabels.Add(pageLabel);
                page = new StringBuilder();
                pageLabel = null;
            }

            // Emits the pending list items into the page being built. listRun counts the items already
            // emitted for the list run in progress, so a numbered list that crosses a page boundary
            // continues its count in the next <ol> instead of starting over at one.
            void FlushListBuffer()
            {
                if (listBuffer.Count == 0)
                    return;

                FlushList(page, listBuffer, numberingFormats, listRun + 1);
                listRun += listBuffer.Count;
                listBuffer.Clear();
            }

            // A block that is not a list item ends the run, so the next list counts from one again.
            void CloseListRun()
            {
                FlushListBuffer();
                listRun = 0;
            }

            void EndPage()
            {
                FlushListBuffer();
                FlushPage();
            }

            // v5.6.19: a cheap first pass over the body's own paragraphs. Word's table of contents stores
            // bookmark anchors, and they go stale the moment the document is edited without updating the
            // field - the reported file points at _Toc228813823 while its body carries _Toc224322068 - so a
            // row has to be matched to the paragraph it *names* instead of trusted. The pass also gives every
            // paragraph an index, which is what the rendered ids and the rail's targets use.
            var paragraphs = body?.Elements(W + "p").ToList() ?? [];
            var byText = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tocRows = new List<(int Index, OfficeTocRow Row)>();
            for (var i = 0; i < paragraphs.Count; i++)
            {
                var styleId = paragraphs[i].Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
                var styleName = styleId is not null && styleNames.TryGetValue(styleId, out var n) ? n : null;
                var level = TocLevel(styleId, styleName);
                if (level > 0)
                {
                    var row = ReadTocRow(paragraphs[i], level);
                    if (row is not null)
                        tocRows.Add((i, row));
                    continue;
                }

                var text = Normalize(Flatten(paragraphs[i]));
                if (text.Length > 0 && !byText.ContainsKey(text))
                    byText[text] = i;
            }

            // The heading each page opens with, taken from the table of contents: a thesis like the one this
            // was written against carries no heading styles at all (its body headings are ordinary paragraphs
            // with a bold run), so the TOC is the only place that says what a section is called - and a TOC row
            // pointing at a paragraph is exactly the mapping needed.
            var tocTargets = new Dictionary<int, int>();
            var targetLabels = new Dictionary<int, string>();
            foreach (var (index, row) in tocRows)
            {
                if (byText.TryGetValue(row.Text, out var target))
                {
                    tocTargets[index] = target;
                    if (row.Text.Length > 0 && !targetLabels.ContainsKey(target))
                        targetLabels[target] = row.Text;
                }
            }

            var navTargets = new HashSet<int>(tocTargets.Values);
            var tocByIndex = tocRows.ToDictionary(t => t.Index, t => t.Row);

            void RenderBlock(XElement node, int index)
            {
                if (node.Name == W + "p")
                {
                    if (++paragraphCount > MaxParagraphs)
                        return;

                    if (pageLabel is null && targetLabels.TryGetValue(index, out var label))
                        pageLabel = label;

                    var item = ParagraphToHtml(node, styleNames, numberingFormats, rels, archive,
                        imageFolder, imageHost, ref imageCount, ref headingIndex,
                        index, tocByIndex.TryGetValue(index, out var row) ? row : null,
                        tocTargets.TryGetValue(index, out var target) ? target : (int?)null,
                        navTargets);
                    if (item is null)
                        return;

                    if (item.IsListItem)
                    {
                        listBuffer.Add(item.Html);
                        return;
                    }

                    CloseListRun();
                    page.Append(item.Html);
                }
                else if (node.Name == W + "tbl")
                {
                    CloseListRun();
                    page.Append(TableToHtml(node, rels, archive, imageFolder, imageHost, ref imageCount));
                }
                else if (node.Name == W + "sdt")
                {
                    // v5.6.19: Word wraps whole blocks (a TOC, cover-page fields) in a structured document
                    // tag; its content is what the reader is meant to see.
                    foreach (var inner in node.Descendants(W + "sdtContent").SelectMany(c => c.Elements()))
                        RenderBlock(inner, -1);
                }
            }

            if (body is not null)
            {
                var nodes = body.Elements().ToList();
                foreach (var node in nodes)
                {
                    var broke = FindPageBreak(node, out var continues);
                    if (broke && continues)
                        EndPage();

                    RenderBlock(node, node.Name == W + "p" ? paragraphs.IndexOf(node) : -1);

                    if (broke && !continues)
                        EndPage();
                }

                CloseListRun();
                FlushPage();
            }

            if (pages.Count == 0)
            {
                pages.Add(page);
                pageLabels.Add(pageLabel);
            }

            var sb = new StringBuilder();
            var navigation = new List<OfficeNavEntry>();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>");
            AppendCss(sb, geometry);
            sb.Append("</style></head><body><div class=\"pages\">");

            for (var i = 0; i < pages.Count; i++)
            {
                var content = pages[i].ToString();
                sb.Append("<div class=\"page").Append(i == 0 ? " active" : string.Empty)
                  .Append("\" id=\"page-").Append(i + 1).Append("\">").Append(content).Append("</div>");

                // The rail reads as an outline of the pages: the number, then the heading the page opens
                // with, so a reader can go to "第二章" without stepping through thumbnails. The heading comes
                // from the TOC where there is one, and from the document's own heading elements otherwise.
                var heading = i < pageLabels.Count ? pageLabels[i] : null;
                heading ??= FirstHeading(content);
                navigation.Add(new OfficeNavEntry(
                    heading is null ? $"Page {i + 1}" : $"{i + 1} · {heading}", $"page-{i + 1}"));
            }

            sb.Append("</div>").Append(PageScript).Append("</body></html>");

            return new OfficePanelContent
            {
                Html = sb.ToString(),
                Navigation = navigation,
                ImageFolder = imageFolder,
            };
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return Failure("This Word document cannot be read: " + WebUtility.HtmlEncode(e.Message));
        }
    }

    private static OfficePanelContent Failure(string html) =>
        new() { Html = ErrorHtml(html) };

    private static XDocument ReadXml(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null)
            return null;

        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static Dictionary<string, string> ReadRels(ZipArchive archive, string entryName)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entry = archive.GetEntry(entryName);
        if (entry is null)
            return map;

        using var stream = entry.Open();
        var rels = XDocument.Load(stream);
        const string relNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        foreach (var rel in rels.Root?.Elements(XName.Get("Relationship", relNs)) ?? [])
        {
            var id = (string)rel.Attribute("Id");
            var target = (string)rel.Attribute("Target");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
                map[id] = target;
        }

        return map;
    }

    private static Dictionary<string, string> BuildStyleMap(XDocument styles)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (styles?.Root is null)
            return map;

        foreach (var style in styles.Root.Elements(W + "style"))
        {
            var id = (string)style.Attribute(W + "styleId");
            var name = (string)style.Element(W + "name")?.Attribute(W + "val");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                map[id] = name;
        }

        return map;
    }

    private static Dictionary<string, string> BuildNumberingFormats(XDocument numbering)
    {
        var formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (numbering?.Root is null)
            return formats;

        var abstractFormats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var abs in numbering.Root.Elements(W + "abstractNum"))
        {
            var id = (string)abs.Attribute(W + "abstractNumId");
            var level = abs.Elements(W + "lvl").FirstOrDefault();
            var fmt = (string)level?.Element(W + "numFmt")?.Attribute(W + "val");
            if (id is not null && fmt is not null)
                abstractFormats[id] = fmt;
        }

        foreach (var num in numbering.Root.Elements(W + "num"))
        {
            var id = (string)num.Attribute(W + "numId");
            var absId = (string)num.Element(W + "abstractNumId")?.Attribute(W + "val");
            if (id is not null && absId is not null && abstractFormats.TryGetValue(absId, out var fmt))
                formats[id] = fmt;
        }

        return formats;
    }

    private sealed record ParagraphResult(string Html, bool IsListItem);

    /// <summary>v5.6.19: one entry of a Word table of contents, read before the page is rendered.</summary>
    private sealed record OfficeTocRow(int Level, string Text, string Page, string Anchor, bool Bold);

    /// <summary>
    /// v5.6.19: the text and the page number of a TOC entry, which Word separates with a tab, plus the
    /// bookmark its hyperlink points at (which may be stale) and its emphasis.
    /// </summary>
    private static OfficeTocRow ReadTocRow(XElement paragraph, int level)
    {
        var text = new StringBuilder();
        var page = new StringBuilder();
        var bold = false;
        var inPageNumber = false;

        foreach (var run in RunsOf(paragraph))
        {
            if (run.Element(W + "rPr")?.Element(W + "b") is not null)
                bold = true;

            foreach (var node in run.Nodes())
            {
                if (node is not XElement el)
                    continue;

                if (el.Name == W + "t")
                    (inPageNumber ? page : text).Append(el.Value);
                else if (el.Name == W + "tab")
                    inPageNumber = true;
                else if (el.Name == W + "br")
                    text.Append(' ');
            }
        }

        var entry = Normalize(text.ToString());
        var pageText = page.ToString().Trim();
        if (entry.Length == 0 && pageText.Length == 0)
            return null;

        var anchor = paragraph.Descendants(W + "hyperlink")
            .Select(h => (string)h.Attribute(W + "anchor"))
            .FirstOrDefault(a => !string.IsNullOrEmpty(a));

        return new OfficeTocRow(level, entry, pageText, anchor, bold);
    }

    /// <summary>Whitespace collapsed, so a TOC entry matches the heading it names.</summary>
    private static string Normalize(string value) =>
        string.Join(' ', value.Split([' ', '\t', '\u00a0', '\u3000'], StringSplitOptions.RemoveEmptyEntries));

    private static ParagraphResult ParagraphToHtml(
        XElement paragraph,
        Dictionary<string, string> styleNames,
        Dictionary<string, string> numberingFormats,
        Dictionary<string, string> rels,
        ZipArchive archive,
        string imageFolder,
        string imageHost,
        ref int imageCount,
        ref int headingIndex,
        int index,
        OfficeTocRow tocRow,
        int? tocTarget,
        HashSet<int> navTargets)
    {
        var pPr = paragraph.Element(W + "pPr");
        var styleId = pPr?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
        var styleName = styleId is not null && styleNames.TryGetValue(styleId, out var n) ? n : null;
        var headingLevel = HeadingLevel(styleId, styleName);
        var tocLevel = TocLevel(styleId, styleName);
        var anchor = AnchorOf(paragraph);

        var numId = pPr?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val")?.Value;
        var isListItem = numId is not null;

        if (tocLevel > 0)
            return new ParagraphResult(TocRowToHtml(tocRow, tocLevel, tocTarget), false);

        var sb = new StringBuilder();
        AppendInline(sb, paragraph, rels, archive, imageFolder, imageHost, ref imageCount);

        // Empty paragraphs still produce a blank line.
        if (sb.Length == 0)
            sb.Append("<br>");

        var alignment = Alignment(pPr);
        var styleAttr = alignment is null ? string.Empty : $" style=\"text-align:{alignment}\"";
        // v5.6.19: a paragraph a TOC row resolves to gets an id of its own; a bookmark keeps its name.
        var navId = index >= 0 && navTargets.Contains(index) ? $"nav-{index}" : null;
        var idAttr = navId is not null || anchor is not null
            ? $" id=\"{WebUtility.HtmlEncode(navId ?? anchor)}\""
            : string.Empty;

        if (isListItem)
        {
            var fmt = numId is not null && numberingFormats.TryGetValue(numId, out var f) ? f : null;
            var ordered = fmt is "decimal" or "lowerLetter" or "upperLetter" or "lowerRoman" or "upperRoman";
            return new ParagraphResult($"<li{idAttr} data-ordered=\"{ordered}\">{sb}</li>", true);
        }

        if (headingLevel > 0)
        {
            var h = Math.Min(headingLevel, 6);
            var id = navId ?? anchor ?? $"h{++headingIndex}";

            return new ParagraphResult($"<h{h}{idAttr}{styleAttr}>{sb}</h{h}>", false);
        }

        return new ParagraphResult($"<p{idAttr}{styleAttr}>{sb}</p>", false);
    }

    /// <summary>
    /// v5.6.19: one row of the table of contents - the entry, a dotted leader and the page number, linked to
    /// the paragraph the entry names. When the field is stale the link uses the paragraph's own id; when the
    /// entry cannot be matched at all the row still reads as text rather than pointing nowhere.
    /// </summary>
    private static string TocRowToHtml(OfficeTocRow row, int level, int? targetIndex)
    {
        if (row is null)
            return string.Empty;

        var target = targetIndex is { } index
            ? $"nav-{index}"
            : row.Anchor;

        var style = row.Bold ? " class=\"toc-text toc-bold\"" : " class=\"toc-text\"";
        var body =
            $"<span{style}>{WebUtility.HtmlEncode(row.Text)}</span>" +
            "<span class=\"toc-dots\"></span>" +
            $"<span class=\"toc-page\">{WebUtility.HtmlEncode(row.Page)}</span>";

        var content = string.IsNullOrEmpty(target)
            ? body
            : $"<a href=\"#{WebUtility.HtmlEncode(target)}\">{body}</a>";

        return $"<div class=\"toc-row toc-l{Math.Min(level, 9)}\">{content}</div>";
    }

    /// <summary>
    /// v5.6.19: the runs of a paragraph, in document order, seen through the inline wrappers Word puts them
    /// in (<c>w:hyperlink</c> for a TOC entry or a link, <c>w:ins</c>, <c>w:smartTag</c>, <c>w:sdt</c>).
    /// </summary>
    private static IEnumerable<XElement> RunsOf(XElement container)
    {
        foreach (var node in container.Elements())
        {
            if (node.Name == W + "r")
                yield return node;
            else if (node.Name != W + "del" &&
                     (node.Name == W + "hyperlink" || node.Name == W + "ins" ||
                      node.Name == W + "smartTag" || node.Name == W + "sdt" ||
                      node.Name == W + "sdtContent"))
            {
                foreach (var run in RunsOf(node))
                    yield return run;
            }
        }
    }

    /// <summary>The plain text of a paragraph, wrappers included - used for outline labels.</summary>
    private static string Flatten(XElement paragraph) =>
        string.Concat(RunsOf(paragraph).SelectMany(r => r.Elements(W + "t")).Select(t => t.Value));

    /// <summary>
    /// v5.6.19: emits a paragraph's content, keeping hyperlinks as links. Only the wrappers that can hold
    /// visible content are entered; <c>w:del</c> (deleted text) and <c>w:instrText</c> (field instructions)
    /// are left out.
    /// </summary>
    private static void AppendInline(
        StringBuilder sb,
        XElement container,
        Dictionary<string, string> rels,
        ZipArchive archive,
        string imageFolder,
        string imageHost,
        ref int imageCount)
    {
        foreach (var node in container.Elements())
        {
            if (node.Name == W + "r")
            {
                AppendRun(sb, node, rels, archive, imageFolder, imageHost, ref imageCount);
            }
            else if (node.Name == W + "hyperlink")
            {
                var target = (string)node.Attribute(W + "anchor");
                if (!string.IsNullOrEmpty(target))
                    sb.Append("<a href=\"#").Append(WebUtility.HtmlEncode(target)).Append("\">");

                AppendInline(sb, node, rels, archive, imageFolder, imageHost, ref imageCount);

                if (!string.IsNullOrEmpty(target))
                    sb.Append("</a>");
            }
            else if (node.Name == W + "ins" || node.Name == W + "smartTag" ||
                     node.Name == W + "sdt" || node.Name == W + "sdtContent")
            {
                AppendInline(sb, node, rels, archive, imageFolder, imageHost, ref imageCount);
            }
        }
    }

    private static string AnchorOf(XElement paragraph) =>
        paragraph.Descendants(W + "bookmarkStart")
            .Select(b => (string)b.Attribute(W + "name"))
            .FirstOrDefault(name => !string.IsNullOrEmpty(name) && name != "_GoBack");

    private static int TocLevel(string styleId, string styleName)
    {
        var probe = !string.IsNullOrEmpty(styleId) ? styleId : styleName;
        if (string.IsNullOrEmpty(probe) || !probe.StartsWith("toc", StringComparison.OrdinalIgnoreCase))
            return 0;

        var digits = new string(probe.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var level) && level is >= 1 and <= 9 ? level : 0;
    }

    private static int HeadingLevel(string styleId, string styleName)
    {
        var probe = string.Empty;
        if (!string.IsNullOrEmpty(styleName))
            probe = styleName.ToLowerInvariant();
        else if (!string.IsNullOrEmpty(styleId))
            probe = styleId.ToLowerInvariant();

        if (probe.Contains("heading") || probe.Contains("标题") || probe.Contains("titre") ||
            probe.Contains("rubrik") || probe.Contains("überschrift"))
        {
            for (var i = 1; i <= 6; i++)
            {
                if (probe.EndsWith(i.ToString()) || probe.EndsWith($" {i}") ||
                    (probe.Contains("标题") && probe.Contains(i.ToString())))
                {
                    return i;
                }
            }

            return 1;
        }

        return 0;
    }

    private static string Alignment(XElement pPr)
    {
        var jc = pPr?.Element(W + "jc")?.Attribute(W + "val")?.Value;
        return jc switch
        {
            "center" => "center",
            "right" => "right",
            "both" or "distribute" => "justify",
            _ => null,
        };
    }

    private static void AppendRun(
        StringBuilder sb,
        XElement run,
        Dictionary<string, string> rels,
        ZipArchive archive,
        string imageFolder,
        string imageHost,
        ref int imageCount)
    {
        var rPr = run.Element(W + "rPr");
        var extra = string.Empty;

        if (rPr is not null)
        {
            var css = new List<string>();
            if (rPr.Element(W + "b") is not null)
                css.Add("font-weight:bold");
            if (rPr.Element(W + "i") is not null)
                css.Add("font-style:italic");
            if (rPr.Element(W + "strike") is not null)
                css.Add("text-decoration:line-through");
            if (rPr.Element(W + "u") is not null)
                css.Add("text-decoration:underline");
            var color = rPr.Element(W + "color")?.Attribute(W + "val")?.Value;
            if (!string.IsNullOrEmpty(color) && color.Length >= 6)
                css.Add($"color:#{color[..6]}");
            var size = rPr.Element(W + "sz")?.Attribute(W + "val")?.Value;
            if (int.TryParse(size, out var halfPoints) && halfPoints > 0)
                css.Add($"font-size:{halfPoints / 2f}pt");
            var highlight = rPr.Element(W + "highlight")?.Attribute(W + "val")?.Value;
            if (!string.IsNullOrEmpty(highlight))
                css.Add($"background:{HighlightColor(highlight)}");
            var vertAlign = rPr.Element(W + "vertAlign")?.Attribute(W + "val")?.Value;
            if (vertAlign == "superscript")
                css.Add("vertical-align:super;font-size:smaller");
            else if (vertAlign == "subscript")
                css.Add("vertical-align:sub;font-size:smaller");

            if (css.Count > 0)
                extra = $" style=\"{string.Join(";", css)}\"";
        }

        var content = new StringBuilder();
        foreach (var node in run.Nodes())
        {
            if (node is XElement el)
            {
                if (el.Name == W + "t")
                {
                    content.Append(WebUtility.HtmlEncode(el.Value));
                }
                else if (el.Name == W + "br")
                {
                    content.Append("<br>");
                }
                else if (el.Name == W + "tab")
                {
                    content.Append("&emsp;");
                }
                else if (el.Name == W + "drawing" && imageCount < MaxImages)
                {
                    var img = ExtractImage(el, rels, archive, imageFolder, imageHost, imageCount);
                    if (img is not null)
                    {
                        content.Append(img);
                        imageCount++;
                    }
                }
            }
            else if (node is XText text)
            {
                content.Append(WebUtility.HtmlEncode(text.Value));
            }
        }

        if (content.Length == 0)
            return;

        sb.Append("<span").Append(extra).Append('>').Append(content).Append("</span>");
    }

    /// <summary>
    /// v5.6.19: writes the picture next to the page instead of inlining it, scaled to the size the page
    /// draws it at (see <see cref="OfficeImageStore"/>).
    /// </summary>
    private static string ExtractImage(
        XElement drawing,
        Dictionary<string, string> rels,
        ZipArchive archive,
        string imageFolder,
        string imageHost,
        int imageCount)
    {
        if (string.IsNullOrEmpty(imageFolder))
            return null;

        var embed = drawing.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed")?.Value;
        if (embed is null || !rels.TryGetValue(embed, out var target))
            return null;

        var mediaPath = target.Replace('/', '\\');
        if (!mediaPath.StartsWith("word\\", StringComparison.OrdinalIgnoreCase) &&
            !mediaPath.StartsWith("/word/", StringComparison.OrdinalIgnoreCase))
        {
            mediaPath = "word\\" + mediaPath.TrimStart('/');
        }

        var entry = archive.GetEntry(mediaPath);
        if (entry is null)
            return null;

        byte[] bytes;
        using (var stream = entry.Open())
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            bytes = ms.ToArray();
        }

        if (bytes.Length == 0)
            return null;

        // The drawing's extent is in EMU; the page draws it at 96 dpi.
        var extent = drawing.Descendants(Wp + "extent").FirstOrDefault();
        var displayWidth = 0d;
        if (extent is not null && long.TryParse((string)extent.Attribute("cx"), out var cx) && cx > 0)
            displayWidth = cx / 9525d;

        var name = OfficeImageStore.Save(imageFolder, bytes, displayWidth, $"img{imageCount}");
        if (name is null)
            return null;

        return $"<img src=\"https://{imageHost}/{name}\" alt=\"\">";
    }

    private static string TableToHtml(
        XElement table,
        Dictionary<string, string> rels,
        ZipArchive archive,
        string imageFolder,
        string imageHost,
        ref int imageCount)
    {
        var sb = new StringBuilder("<table><tbody>");
        foreach (var row in table.Elements(W + "tr"))
        {
            sb.Append("<tr>");
            foreach (var cell in row.Elements(W + "tc"))
            {
                var cellHtml = new StringBuilder();
                foreach (var p in cell.Elements(W + "p"))
                {
                    var para = ParagraphToHtml(p, new Dictionary<string, string>(),
                        new Dictionary<string, string>(), rels,
                        archive, imageFolder, imageHost, ref imageCount,
                        ref UnusedHeadingIndex,
                        -1, null, null, EmptyNavTargets);
                    cellHtml.Append(para.IsListItem ? "<div>" + para.Html + "</div>" : para.Html);
                }

                var colspan = cell.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val")?.Value;
                sb.Append("<td");
                if (int.TryParse(colspan, out var span) && span > 1)
                    sb.Append(" colspan=\"").Append(span).Append('"');
                sb.Append('>').Append(cellHtml).Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    /// <summary>Headings inside table cells are not part of the outline, so their counter is thrown away.</summary>
    private static int UnusedHeadingIndex;

    private static readonly HashSet<int> EmptyNavTargets = [];

    private static void FlushList(
        StringBuilder html, List<string> items, Dictionary<string, string> numberingFormats, int start)
    {
        var ordered = items.Any(i => i.Contains("data-ordered=\"True\"", StringComparison.OrdinalIgnoreCase));
        var tag = ordered ? "ol" : "ul";
        html.Append('<').Append(tag);

        // v5.6.19: the count a list continues with when a page ended in the middle of it.
        if (ordered && start > 1)
            html.Append(" start=\"").Append(start).Append('"');

        html.Append('>');
        foreach (var item in items)
        {
            var clean = item.Replace(" data-ordered=\"True\"", string.Empty, StringComparison.OrdinalIgnoreCase)
                            .Replace(" data-ordered=\"False\"", string.Empty, StringComparison.OrdinalIgnoreCase);
            html.Append(clean);
        }
        html.Append("</").Append(tag).Append('>');
    }

    private static void AppendCss(StringBuilder sb, PageGeometry geometry)
    {
        // v3.17.0: fixed light paper rendering, independent of the app theme.
        var fg = "#1A1A1A";
        var border = "rgba(0,0,0,.12)";
        var scrollThumb = "rgba(0,0,0,.28)";
        var scrollHover = "rgba(0,0,0,.45)";
        var headerBg = "rgba(0,0,0,.04)";
        var link = "#005FB8";
        // v5.6.19: what the sheets sit on - the page beside it is white, so the backdrop is a neutral grey
        // rather than the app's own backdrop (a transparent WebView2 page composites to black).
        var backdrop = "#E9E9E9";

        // v3.17.0 fix: opaque white paper - transparent pages can composite to
        // black on some window setups.
        sb.Append($"html,body{{height:100%}}body{{margin:0;color:{fg};background:{backdrop};" +
            "font-family:'Segoe UI',Helvetica,Arial,sans-serif;font-size:14px;line-height:1.6}");
        sb.Append("html{scroll-behavior:smooth}");
        sb.Append("::-webkit-scrollbar{width:8px;height:8px}");
        sb.Append("::-webkit-scrollbar-track{background:transparent}");
        sb.Append($"::-webkit-scrollbar-thumb{{background:{scrollThumb};border-radius:4px}}");
        sb.Append($"::-webkit-scrollbar-thumb:hover{{background:{scrollHover}}}");
        sb.Append("::-webkit-scrollbar-corner{background:transparent}");

        // v5.6.19: the document is drawn as the pages it is made of - a white sheet of the section's own
        // paper size, with the section's own margins as its padding, one page at a time (showPage()).
        sb.Append($".page{{display:none;box-sizing:border-box;width:{geometry.Width}px;" +
            $"min-height:{geometry.Height}px;" +
            $"padding:{geometry.Top}px {geometry.Right}px {geometry.Bottom}px {geometry.Left}px;" +
            "margin:22px auto;background:#FFFFFF;overflow-wrap:break-word;" +
            "box-shadow:0 1px 2px rgba(0,0,0,.18),0 0 0 1px rgba(0,0,0,.06)}");
        sb.Append(".page.active{display:block}");
        sb.Append($"h1,h2,h3,h4,h5,h6{{margin:1.1em 0 .5em;line-height:1.3}}");
        sb.Append($"h1{{font-size:24px}}h2{{font-size:20px}}h3{{font-size:17px}}h4{{font-size:15px}}");
        sb.Append($"p{{margin:.55em 0}}");
        sb.Append($"ul,ol{{margin:.5em 0;padding-left:2em}}");
        sb.Append($"li{{margin:.18em 0}}");
        sb.Append($"table{{border-collapse:collapse;margin:.8em 0;font-size:13px}}");
        sb.Append($"td,th{{border:1px solid {border};padding:5px 10px}}");
        sb.Append($"thead th,table tr:first-child td{{background:{headerBg};font-weight:600}}");
        sb.Append($"img{{max-width:100%;height:auto;border-radius:6px}}");
        sb.Append($".error{{color:#C42B1C;padding:24px;font-size:14px}}");

        // v5.6.19: the table of contents. Word stores its entries as hyperlinks to the heading bookmarks,
        // and the page number follows a tab - so the row is a flex line with a dotted leader between.
        sb.Append($"a{{color:{link};text-decoration:none}}a:hover{{text-decoration:underline}}");
        sb.Append(".toc-row{margin:.15em 0;max-width:760px}");
        sb.Append(".toc-row a{display:flex;align-items:baseline;color:inherit}");
        sb.Append(".toc-row a:hover{text-decoration:none}");
        sb.Append(".toc-row a:hover .toc-text{text-decoration:underline}");
        sb.Append(".toc-text{flex:0 1 auto;text-decoration:none}");
        sb.Append(".toc-bold{font-weight:600}");
        sb.Append(".toc-dots{flex:1 1 auto;border-bottom:1px dotted #B4B4B4;margin:0 6px;transform:translateY(-4px)}");
        sb.Append(".toc-page{flex:0 0 auto;font-variant-numeric:tabular-nums}");
        for (var level = 1; level <= 4; level++)
            sb.Append($".toc-l{level}{{padding-left:{(level - 1) * 18}px}}");
    }

    /// <summary>
    /// v5.6.19: page switching, plus the document's own table of contents. A TOC row points at a paragraph
    /// that may live on another page, and every page but the current one is display:none - so the click has
    /// to bring the row's page up first, or the browser scrolls to a hidden element and nothing appears to
    /// happen.
    /// </summary>
    private const string PageScript =
        "<script>window._pages=[].slice.call(document.querySelectorAll('.page'));" +
        "function showPage(i){i=Math.max(0,Math.min(window._pages.length-1,i));" +
        "for(var n=0;n<window._pages.length;n++)window._pages[n].className=(n===i?'page active':'page');" +
        "window.scrollTo(0,0);}" +
        "document.addEventListener('click',function(e){" +
        "var a=e.target&&e.target.closest?e.target.closest('a[href^=\"#\"]'):null;if(!a)return;" +
        "var el=document.getElementById(decodeURIComponent(a.getAttribute('href').slice(1)));" +
        "if(!el)return;e.preventDefault();" +
        "var p=el.closest('.page');if(p)showPage(window._pages.indexOf(p));" +
        "setTimeout(function(){el.scrollIntoView({behavior:'smooth',block:'start'});},40);});</script>";

    /// <summary>
    /// v5.6.19: where Word last laid a page out. <c>w:lastRenderedPageBreak</c> records the boundary from the
    /// last save and <c>w:br w:type="page"</c> is one the author typed; both can sit at the end of the
    /// paragraph a page closed with or at the start of the one that opened the next. <paramref name="continues"/>
    /// says which: true when visible content still follows the marker inside the same block, which means the
    /// new page begins with that block.
    /// </summary>
    private static bool FindPageBreak(XElement block, out bool continues)
    {
        continues = false;

        var elements = block.DescendantsAndSelf().ToList();
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            var isBreak = element.Name == W + "lastRenderedPageBreak" ||
                          (element.Name == W + "br" && (string)element.Attribute(W + "type") == "page");
            if (!isBreak)
                continue;

            for (var j = i + 1; j < elements.Count; j++)
            {
                var after = elements[j];
                if ((after.Name == W + "t" && !string.IsNullOrWhiteSpace(after.Value)) ||
                    after.Name == W + "drawing" || after.Name == W + "pict")
                {
                    continues = true;
                    break;
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// v5.6.19: the heading a page opens with, which is what the rail labels the page with - the heading is
    /// already in the page's own HTML, so there is nothing to collect while rendering.
    /// </summary>
    private static string FirstHeading(string html)
    {
        var match = Regex.Match(html, "<h[1-6][^>]*>(.*?)</h[1-6]>", RegexOptions.Singleline);
        if (!match.Success)
            return null;

        var text = Regex.Replace(match.Groups[1].Value, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(text).Trim() is { Length: > 0 } heading ? heading : null;
    }

    /// <summary>v5.6.19: the white sheet a page is drawn as, in CSS pixels.</summary>
    private sealed record PageGeometry(double Width, double Height, double Top, double Right, double Bottom, double Left);

    /// <summary>
    /// v5.6.19: the section's own paper, so a Word page in the preview is the size it would print at. A twip
    /// is a twentieth of a point and a CSS pixel is 1/96 inch, so a pixel is fifteen twips. A document that
    /// says nothing usable falls back to A4 with one-inch margins.
    /// </summary>
    private static PageGeometry ReadPageGeometry(XDocument document)
    {
        var fallback = new PageGeometry(794, 1123, 96, 96, 96, 96);
        try
        {
            var body = document?.Root?.Element(W + "body");
            var section = body?.Elements(W + "sectPr").LastOrDefault() ??
                          body?.Descendants(W + "pPr").Elements(W + "sectPr").LastOrDefault();
            var size = section?.Element(W + "pgSz");
            if (size is null)
                return fallback;

            var width = Pixels((double?)size.Attribute(W + "w") ?? 0);
            var height = Pixels((double?)size.Attribute(W + "h") ?? 0);
            if (width < 200 || width > 4000 || height < 200 || height > 4000)
                return fallback;

            var margin = section.Element(W + "pgMar");
            var top = Pixels((double?)margin?.Attribute(W + "top") ?? 0);
            var right = Pixels((double?)margin?.Attribute(W + "right") ?? 0);
            var bottom = Pixels((double?)margin?.Attribute(W + "bottom") ?? 0);
            var left = Pixels((double?)margin?.Attribute(W + "left") ?? 0);

            return new PageGeometry(
                width, height,
                top > 0 ? top : 96, right > 0 ? right : 96,
                bottom > 0 ? bottom : 96, left > 0 ? left : 96);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            return fallback;
        }
    }

    private static double Pixels(double twips) => Math.Round(twips / 15d, 2);

    private static string ErrorHtml(string message)
    {
        return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>body{{margin:0;color:#1A1A1A;" +
            "background:#FFFFFF;font-family:'Segoe UI',sans-serif}}</style></head>" +
            $"<body><div class=\"error\" style=\"padding:24px\">{message}</div></body></html>";
    }

    private static string HighlightColor(string name) => name switch
    {
        "yellow" => "#FFFF00",
        "green" => "#00FF00",
        "cyan" => "#00FFFF",
        "magenta" => "#FF00FF",
        "blue" => "#0000FF",
        "red" => "#FF0000",
        "darkBlue" => "#000080",
        "darkCyan" => "#008080",
        "darkGreen" => "#008000",
        "darkMagenta" => "#800080",
        "darkRed" => "#800000",
        "darkYellow" => "#808000",
        "darkGray" => "#808080",
        "lightGray" => "#C0C0C0",
        "black" => "#000000",
        _ => "transparent",
    };
}
