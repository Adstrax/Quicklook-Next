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
/// v3.15.0: converts a .pptx (OOXML) deck into styled HTML for the
/// self-rendered preview. Each slide is rendered as a positioned canvas:
/// text boxes, shapes and pictures are laid out from their EMU coordinates.
/// </summary>
internal static class PptxToHtml
{
    private const int MaxSlides = 60;
    private const double EmuPerInch = 914400d;
    private const double Dpi = 96d;

    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    internal static string Convert(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var presentation = ReadXml(archive, "ppt/presentation.xml");
            if (presentation?.Root is null)
                return ErrorHtml("Not a valid PowerPoint presentation (ppt/presentation.xml is missing).");

            var presRels = ReadRels(archive, "ppt/_rels/presentation.xml.rels");
            var sldSz = presentation.Root.Element(P + "sldSz");
            var slideW = EmuToPx((long?)sldSz?.Attribute("cx") ?? 12192000L);
            var slideH = EmuToPx((long?)sldSz?.Attribute("cy") ?? 6858000L);

            var slides = new StringBuilder();
            var count = 0;
            var sldIdLst = presentation.Root.Element(P + "sldIdLst");
            if (sldIdLst is not null)
            {
                foreach (var sldId in sldIdLst.Elements(P + "sldId"))
                {
                    if (++count > MaxSlides)
                        break;

                    var rid = (string)sldId.Attribute(R + "id");
                    if (rid is null || !presRels.TryGetValue(rid, out var target))
                        continue;

                    var slidePath = "ppt/" + target.TrimStart('/');
                    slides.Append(SlideToHtml(archive, slidePath, slideW, slideH, count));
                }
            }

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>");
            AppendCss(sb);
            sb.Append("</style></head><body><div class=\"deck\">")
              .Append(slides)
              .Append("</div></body></html>");
            return sb.ToString();
        }
        catch (Exception e)
        {
            return ErrorHtml("This PowerPoint presentation cannot be read: " + WebUtility.HtmlEncode(e.Message));
        }
    }

    private static string SlideToHtml(
        ZipArchive archive, string slidePath, double slideW, double slideH, int index)
    {
        var slide = ReadXml(archive, slidePath);
        if (slide?.Root is null)
            return string.Empty;

        var relsPath = Path.GetDirectoryName(slidePath) + "\\_rels\\" +
            Path.GetFileName(slidePath) + ".rels";
        var rels = ReadRels(archive, relsPath);

        var cSld = slide.Root.Element(P + "cSld");
        var spTree = cSld?.Element(P + "spTree");
        var background = SlideBackground(cSld);

        var shapes = new StringBuilder();
        if (spTree is not null)
        {
            foreach (var shape in spTree.Elements())
            {
                if (shape.Name == P + "sp")
                    shapes.Append(ShapeToHtml(shape, rels, archive));
                else if (shape.Name == P + "pic")
                    shapes.Append(PictureToHtml(shape, rels, archive));
            }
        }

        return $"""
            <div class="slide" style="width:{slideW:0}px;height:{slideH:0}px;background:{background}">
              {shapes}
              <div class="slide-no">{index}</div>
            </div>
            """;
    }

    private static string ShapeToHtml(
        XElement sp, Dictionary<string, string> rels, ZipArchive archive)
    {
        var pos = GetPosition(sp);
        if (pos is null)
            return string.Empty;

        var txBody = sp.Element(P + "txBody");
        var content = new StringBuilder();
        if (txBody is not null)
        {
            foreach (var para in txBody.Elements(A + "p"))
                content.Append(ParagraphToHtml(para));
        }

        if (content.Length == 0)
        {
            // Non-text shapes render as an empty box with a faint outline.
            content.Append("<div class=\"empty-shape\"></div>");
        }

        return $"""
            <div class="shape" style="left:{pos.Value.Left:0}px;top:{pos.Value.Top:0}px;
                 width:{pos.Value.Width:0}px;height:{pos.Value.Height:0}px">{content}</div>
            """;
    }

    private static string PictureToHtml(
        XElement pic, Dictionary<string, string> rels, ZipArchive archive)
    {
        var pos = GetPosition(pic);
        if (pos is null)
            return string.Empty;

        var embed = pic.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed")?.Value;
        if (embed is null || !rels.TryGetValue(embed, out var target))
            return string.Empty;

        var mediaPath = "ppt/" + target.TrimStart('/');
        var entry = archive.GetEntry(mediaPath.Replace('/', '\\'));
        if (entry is null)
            return string.Empty;

        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        if (bytes.Length == 0)
            return string.Empty;

        var mime = MimeFromBytes(bytes);
        return $"""
            <img src="data:{mime};base64,{System.Convert.ToBase64String(bytes)}"
                 style="left:{pos.Value.Left:0}px;top:{pos.Value.Top:0}px;
                        width:{pos.Value.Width:0}px;height:{pos.Value.Height:0}px">
            """;
    }

    private static string ParagraphToHtml(XElement para)
    {
        var sb = new StringBuilder();
        var pPr = para.Element(A + "pPr");
        var align = pPr?.Attribute("algn")?.Value switch
        {
            "ctr" => "center",
            "r" => "right",
            "just" => "justify",
            _ => "left",
        };

        foreach (var node in para.Nodes())
        {
            if (node is XElement el && el.Name == A + "r")
            {
                var rPr = el.Element(A + "rPr");
                var css = new List<string>();
                if (rPr is not null)
                {
                    if (rPr.Attribute("b")?.Value == "1")
                        css.Add("font-weight:bold");
                    if (rPr.Attribute("i")?.Value == "1")
                        css.Add("font-style:italic");
                    if (rPr.Attribute("u")?.Value is "1" or "sng")
                        css.Add("text-decoration:underline");
                    var sz = rPr.Attribute("sz")?.Value;
                    if (double.TryParse(sz, out var hundredths) && hundredths > 0)
                        css.Add($"font-size:{hundredths / 100d:0.#}pt");
                    var color = SolidFillColor(rPr);
                    if (color is not null)
                        css.Add($"color:{color}");
                    var face = rPr.Element(A + "latin")?.Attribute("typeface")?.Value;
                    if (!string.IsNullOrEmpty(face))
                        css.Add($"font-family:'{WebUtility.HtmlEncode(face)}'");
                }

                var text = new StringBuilder();
                foreach (var t in el.Elements(A + "t"))
                    text.Append(WebUtility.HtmlEncode(t.Value));
                foreach (var br in el.Elements(A + "br"))
                    text.Append("<br>");

                if (text.Length == 0)
                    continue;

                var style = css.Count > 0 ? $" style=\"{string.Join(";", css)}\"" : string.Empty;
                sb.Append("<span").Append(style).Append('>').Append(text).Append("</span>");
            }
            else if (node is XElement el2 && el2.Name == A + "br")
            {
                sb.Append("<br>");
            }
        }

        if (sb.Length == 0)
            return string.Empty;

        return $"<p style=\"text-align:{align}\">{sb}</p>";
    }

    private static string SolidFillColor(XElement rPr)
    {
        var fill = rPr.Element(A + "solidFill");
        var rgb = fill?.Element(A + "srgbClr")?.Attribute("val")?.Value;
        if (!string.IsNullOrEmpty(rgb) && rgb.Length >= 6)
            return "#" + rgb[..6];
        return null;
    }

    private static string SlideBackground(XElement cSld)
    {
        var rgb = cSld?.Element(P + "bg")?.Descendants(A + "srgbClr")
            .FirstOrDefault()?.Attribute("val")?.Value;
        if (!string.IsNullOrEmpty(rgb) && rgb.Length >= 6)
            return "#" + rgb[..6];
        return "#FFFFFF";
    }

    private static (double Left, double Top, double Width, double Height)? GetPosition(XElement shape)
    {
        // p:spPr / p:picPr contain the a:xfrm (drawingml namespace).
        var xfrm = shape.Element(P + "spPr")?.Element(A + "xfrm")
                ?? shape.Element(P + "picPr")?.Element(A + "xfrm");
        var off = xfrm?.Element(A + "off");
        var ext = xfrm?.Element(A + "ext");
        if (off is null || ext is null)
            return null;

        var left = EmuToPx((long?)off.Attribute("x") ?? 0L);
        var top = EmuToPx((long?)off.Attribute("y") ?? 0L);
        var width = EmuToPx((long?)ext.Attribute("cx") ?? 0L);
        var height = EmuToPx((long?)ext.Attribute("cy") ?? 0L);
        if (width <= 0 || height <= 0)
            return null;

        return (left, top, width, height);
    }

    private static double EmuToPx(long emu) => emu / EmuPerInch * Dpi;

    private static string MimeFromBytes(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 4 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return "image/gif";
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D) return "image/bmp";
        return "image/png";
    }

    private static XDocument ReadXml(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null)
            return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
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

    private static void AppendCss(StringBuilder sb)
    {
        var scrollThumb = "rgba(0,0,0,.28)";
        var scrollHover = "rgba(0,0,0,.45)";

        // v3.17.0 fix: opaque light backdrop - transparent pages can composite
        // to black on some window setups.
        sb.Append("html,body{height:100%}body{margin:0;background:#F3F3F3;" +
            "font-family:'Segoe UI',Helvetica,Arial,sans-serif}");
        sb.Append("::-webkit-scrollbar{width:8px;height:8px}");
        sb.Append("::-webkit-scrollbar-track{background:transparent}");
        sb.Append($"::-webkit-scrollbar-thumb{{background:{scrollThumb};border-radius:4px}}");
        sb.Append($"::-webkit-scrollbar-thumb:hover{{background:{scrollHover}}}");
        sb.Append("::-webkit-scrollbar-corner{background:transparent}");
        sb.Append(".deck{padding:24px 32px;display:flex;flex-direction:column;align-items:center;gap:28px}");
        sb.Append(".slide{position:relative;border-radius:6px;box-shadow:0 3px 16px rgba(0,0,0,.28);" +
            "flex-shrink:0;overflow:hidden}");
        sb.Append(".shape{position:absolute;overflow:hidden;box-sizing:border-box}");
        sb.Append(".shape p{margin:0}");
        sb.Append(".empty-shape{width:100%;height:100%;border:1px dashed rgba(128,128,128,.4);" +
            "border-radius:4px;box-sizing:border-box}");
        sb.Append(".slide img{position:absolute;object-fit:contain}");
        sb.Append(".slide-no{position:absolute;right:10px;bottom:8px;font-size:12px;" +
            "color:rgba(0,0,0,.45);" +
            "font-weight:600;pointer-events:none}");
        sb.Append(".error{color:#C42B1C;padding:24px;font-size:14px}");
    }

    private static string ErrorHtml(string message)
    {
        return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>body{{margin:0;color:#1A1A1A;" +
            "background:#F3F3F3;font-family:'Segoe UI',sans-serif}}</style></head>" +
            $"<body><div class=\"error\" style=\"padding:24px\">{message}</div></body></html>";
    }
}
