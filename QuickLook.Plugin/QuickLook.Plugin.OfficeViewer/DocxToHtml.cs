using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v3.14.0: converts a .docx (OOXML) document into styled HTML for the
/// self-rendered preview. Covers the common content - headings, formatted
/// runs, lists, tables and embedded images - without depending on Office or
/// the Windows system preview component.
/// </summary>
internal static class DocxToHtml
{
    private const int MaxParagraphs = 4000;
    private const int MaxImages = 64;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    internal static string Convert(string path)
    {
        var sb = new StringBuilder();
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var document = ReadXml(archive, "word/document.xml");
            var styles = ReadXml(archive, "word/styles.xml");
            var numbering = ReadXml(archive, "word/numbering.xml");
            var rels = ReadRels(archive, "word/_rels/document.xml.rels");
            if (document is null)
                return ErrorHtml("Not a valid Word document (word/document.xml is missing).");

            var styleNames = BuildStyleMap(styles);
            var numberingFormats = BuildNumberingFormats(numbering);

            var body = document.Root?.Element(W + "body");
            var html = new StringBuilder();
            var paragraphCount = 0;
            var imageCount = 0;

            if (body is not null)
            {
                var listBuffer = new List<string>();

                foreach (var node in body.Elements())
                {
                    if (node.Name == W + "p")
                    {
                        if (++paragraphCount > MaxParagraphs)
                            break;

                        var item = ParagraphToHtml(node, styleNames, numberingFormats, rels, archive, ref imageCount);
                        if (item is null)
                            continue;

                        if (item.IsListItem)
                        {
                            listBuffer.Add(item.Html);
                            continue;
                        }

                        if (listBuffer.Count > 0)
                        {
                            FlushList(html, listBuffer, numberingFormats);
                            listBuffer.Clear();
                        }

                        html.Append(item.Html);
                    }
                    else if (node.Name == W + "tbl")
                    {
                        if (listBuffer.Count > 0)
                        {
                            FlushList(html, listBuffer, numberingFormats);
                            listBuffer.Clear();
                        }

                        html.Append(TableToHtml(node, rels, archive, ref imageCount));
                    }
                }

                if (listBuffer.Count > 0)
                    FlushList(html, listBuffer, numberingFormats);
            }

            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>");
            AppendCss(sb);
            sb.Append("</style></head><body><div class=\"doc\">")
              .Append(html)
              .Append("</div></body></html>");
        }
        catch (Exception e)
        {
            return ErrorHtml("This Word document cannot be read: " + WebUtility.HtmlEncode(e.Message));
        }

        return sb.ToString();
    }

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
            var name = style.Element(W + "name")?.Attribute(W + "val")?.Value;
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                map[id] = name;
        }

        return map;
    }

    private static Dictionary<string, string> BuildNumberingFormats(XDocument numbering)
    {
        // abstractNumId -> numFmt (decimal/bullet/...)
        var abstractFormats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (numbering?.Root is not null)
        {
            foreach (var abs in numbering.Root.Elements(W + "abstractNum"))
            {
                var id = (string)abs.Attribute(W + "abstractNumId");
                var fmt = abs.Descendants(W + "numFmt").FirstOrDefault()?.Attribute(W + "val")?.Value;
                if (id is not null && fmt is not null)
                    abstractFormats[id] = fmt;
            }
        }

        // numId -> abstractNumId (indirection via num -> abstractNumId)
        var formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (numbering?.Root is not null)
        {
            foreach (var num in numbering.Root.Elements(W + "num"))
            {
                var id = (string)num.Attribute(W + "numId");
                var absId = num.Element(W + "abstractNumId")?.Attribute(W + "val")?.Value;
                if (id is not null && absId is not null &&
                    abstractFormats.TryGetValue(absId, out var fmt))
                {
                    formats[id] = fmt;
                }
            }
        }

        return formats;
    }

    private sealed record ParagraphResult(string Html, bool IsListItem);

    private static ParagraphResult ParagraphToHtml(
        XElement paragraph,
        Dictionary<string, string> styleNames,
        Dictionary<string, string> numberingFormats,
        Dictionary<string, string> rels,
        ZipArchive archive,
        ref int imageCount)
    {
        var pPr = paragraph.Element(W + "pPr");
        var styleId = pPr?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
        var styleName = styleId is not null && styleNames.TryGetValue(styleId, out var n) ? n : null;
        var headingLevel = HeadingLevel(styleId, styleName);

        var numId = pPr?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val")?.Value;
        var isListItem = numId is not null;

        var sb = new StringBuilder();
        var runs = paragraph.Elements(W + "r").ToList();
        foreach (var run in runs)
            AppendRun(sb, run, rels, archive, ref imageCount);

        // Empty paragraphs still produce a blank line.
        if (sb.Length == 0)
            sb.Append("<br>");

        var alignment = Alignment(pPr);
        var styleAttr = alignment is null ? string.Empty : $" style=\"text-align:{alignment}\"";

        if (isListItem)
        {
            var fmt = numId is not null && numberingFormats.TryGetValue(numId, out var f) ? f : null;
            var ordered = fmt is "decimal" or "lowerLetter" or "upperLetter" or "lowerRoman" or "upperRoman";
            return new ParagraphResult($"<li data-ordered=\"{ordered}\">{sb}</li>", true);
        }

        if (headingLevel > 0)
        {
            var h = Math.Min(headingLevel, 6);
            return new ParagraphResult($"<h{h}{styleAttr}>{sb}</h{h}>", false);
        }

        return new ParagraphResult($"<p{styleAttr}>{sb}</p>", false);
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
        ref int imageCount)
    {
        var rPr = run.Element(W + "rPr");
        var tag = string.Empty;
        var close = string.Empty;
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
                    var img = ExtractImage(el, rels, archive);
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

    private static string ExtractImage(XElement drawing, Dictionary<string, string> rels, ZipArchive archive)
    {
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

        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        if (bytes.Length == 0)
            return null;

        var mime = MimeFromBytes(bytes);
        return $"<img src=\"data:{mime};base64,{System.Convert.ToBase64String(bytes)}\" alt=\"\">";
    }

    private static string MimeFromBytes(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 4 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return "image/gif";
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D) return "image/bmp";
        return "image/png";
    }

    private static string TableToHtml(
        XElement table,
        Dictionary<string, string> rels,
        ZipArchive archive,
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
                        archive, ref imageCount);
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

    private static void FlushList(StringBuilder html, List<string> items, Dictionary<string, string> numberingFormats)
    {
        var ordered = items.Any(i => i.Contains("data-ordered=\"True\"", StringComparison.OrdinalIgnoreCase));
        var tag = ordered ? "ol" : "ul";
        html.Append('<').Append(tag).Append('>');
        foreach (var item in items)
        {
            var clean = item.Replace(" data-ordered=\"True\"", string.Empty, StringComparison.OrdinalIgnoreCase)
                            .Replace(" data-ordered=\"False\"", string.Empty, StringComparison.OrdinalIgnoreCase);
            html.Append(clean);
        }
        html.Append("</").Append(tag).Append('>');
    }

    private static string HighlightColor(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "yellow" => "#FFFF00",
            "green" => "#00FF00",
            "cyan" => "#00FFFF",
            "magenta" => "#FF00FF",
            "red" => "#FF0000",
            "blue" => "#0000FF",
            "darkblue" => "#00008B",
            "darkcyan" => "#008B8B",
            "darkgreen" => "#006400",
            "darkmagenta" => "#8B008B",
            "darkred" => "#8B0000",
            "darkyellow" => "#808000",
            "gray" or "grey" => "#808080",
            "lightgray" or "lightgrey" => "#D3D3D3",
            "black" => "#000000",
            "white" => "#FFFFFF",
            _ => "#FFFF00",
        };
    }

    private static void AppendCss(StringBuilder sb)
    {
        // v3.17.0: fixed light paper rendering, independent of the app theme.
        var fg = "#1A1A1A";
        var border = "rgba(0,0,0,.12)";
        var scrollThumb = "rgba(0,0,0,.28)";
        var scrollHover = "rgba(0,0,0,.45)";
        var headerBg = "rgba(0,0,0,.04)";

        // v3.17.0 fix: opaque white paper - transparent pages can composite to
        // black on some window setups.
        sb.Append($"html,body{{height:100%}}body{{margin:0;color:{fg};background:#FFFFFF;" +
            "font-family:'Segoe UI',Helvetica,Arial,sans-serif;font-size:14px;line-height:1.6}");
        sb.Append("::-webkit-scrollbar{width:8px;height:8px}");
        sb.Append("::-webkit-scrollbar-track{background:transparent}");
        sb.Append($"::-webkit-scrollbar-thumb{{background:{scrollThumb};border-radius:4px}}");
        sb.Append($"::-webkit-scrollbar-thumb:hover{{background:{scrollHover}}}");
        sb.Append("::-webkit-scrollbar-corner{background:transparent}");
        sb.Append(".doc{padding:28px 36px;max-width:860px;margin:0 auto;overflow-wrap:break-word}");
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
    }

    private static string ErrorHtml(string message)
    {
        return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>body{{margin:0;color:#1A1A1A;" +
            "background:#FFFFFF;font-family:'Segoe UI',sans-serif}}</style></head>" +
            $"<body><div class=\"error\" style=\"padding:24px\">{message}</div></body></html>";
    }
}
