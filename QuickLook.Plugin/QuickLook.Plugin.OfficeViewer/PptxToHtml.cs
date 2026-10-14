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
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Media;
using System.Xml.Linq;

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v3.12.0: renders a .pptx slide by slide, so the preview shows the deck in the app's own window.
/// <para>
/// v5.6.19: three things used to make templates come out nearly empty, and all three are handled now:
/// </para>
/// <list type="bullet">
/// <item><description><c>p:grpSp</c> - a design template keeps almost everything inside grouped shapes, and
/// the renderer only looked at <c>p:sp</c> and <c>p:pic</c>. Groups are walked recursively now, with the
/// group transform (<c>a:off</c>/<c>a:ext</c> against <c>a:chOff</c>/<c>a:chExt</c>) composed into the
/// child coordinates;</description></item>
/// <item><description>the slide master and its layout - the background, the design frames and the theme
/// colours live there, so they are rendered as a layer under the slide and the theme's colour scheme is
/// resolved for both text and fills. Tables (<c>p:graphicFrame</c>) are drawn instead of being skipped, and
/// charts get a labelled placeholder;</description></item>
/// <item><description>pictures: they are written to a temp folder at the size the slide draws them and served
/// over a virtual host. Inlining them as base64 turned one slide of a real template into tens of megabytes of
/// HTML, which WebView2 could not get through.</description></item>
/// </list>
/// </summary>
internal static class PptxToHtml
{
    private const int MaxSlides = 60;
    private const int MaxImages = 80;
    private const double EmuPerInch = 914400d;
    private const double Dpi = 96d;

    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    internal static OfficePanelContent Convert(string path, string imageFolder, string imageHost)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var presentation = ReadXml(archive, "ppt/presentation.xml");
            if (presentation?.Root is null)
                return Failure("Not a valid PowerPoint presentation (ppt/presentation.xml is missing).");

            var presRels = ReadRels(archive, "ppt/_rels/presentation.xml.rels");
            var sldSz = presentation.Root.Element(P + "sldSz");
            var slideW = EmuToPx((double?)sldSz?.Attribute("cx") ?? 12192000d);
            var slideH = EmuToPx((double?)sldSz?.Attribute("cy") ?? 6858000d);

            var theme = ReadTheme(archive);
            var context = new RenderContext(archive, imageFolder, imageHost, theme);
            var outline = new List<OfficeNavEntry>();
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
                    if (rid is null || !presRels.TryGetValue(rid, out var entry))
                        continue;

                    var slidePath = "ppt/" + Part(entry).TrimStart('/');
                    var title = SlideToHtml(context, slidePath, slideW, slideH, count, slides);
                    outline.Add(new OfficeNavEntry(
                        string.IsNullOrEmpty(title) ? $"Slide {count}" : $"{count}  {title}",
                        $"slide-{count}"));
                }
            }

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>");
            AppendCss(sb);
            sb.Append("</style></head><body><div class=\"deck\">")
              .Append(slides)
              .Append("</div>");
            sb.Append("<script>window._slides=document.querySelectorAll('.slide');" +
                "function showSlide(i){i=Math.max(0,Math.min(window._slides.length-1,i));" +
                "window._slides.forEach((s,n)=>{s.style.display=(n===i)?'flex':'none';});" +
                "window._qlCurrent=i;}" +
                "showSlide(0);</script>");
            sb.Append("</body></html>");

            return new OfficePanelContent
            {
                Html = sb.ToString(),
                Navigation = outline,
                ImageFolder = imageFolder,
            };
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return Failure("This PowerPoint presentation cannot be read: " + WebUtility.HtmlEncode(e.Message));
        }
    }

    private static OfficePanelContent Failure(string html) => new() { Html = ErrorHtml(html) };

    /// <summary>
    /// One slide: the master and layout come first (background and design), then the slide's own shapes.
    /// Returns the slide's title for the left rail.
    /// </summary>
    private static string SlideToHtml(
        RenderContext context, string slidePath, double slideW, double slideH, int index, StringBuilder all)
    {
        var archive = context.Archive;
        var slide = ReadXml(archive, slidePath);
        if (slide?.Root is null)
            return null;

        var cSld = slide.Root.Element(P + "cSld");
        var rels = ReadRels(archive, RelsPathFor(slidePath));

        var layoutPath = TargetOf(rels, "slideLayout");
        var layout = layoutPath is null ? null : ReadXml(archive, layoutPath);
        var layoutRels = layoutPath is null
            ? new Dictionary<string, string>()
            : ReadRels(archive, RelsPathFor(layoutPath));
        var masterPath = TargetOf(layoutRels, "slideMaster");
        var master = masterPath is null ? null : ReadXml(archive, masterPath);
        var masterRels = masterPath is null
            ? new Dictionary<string, string>()
            : ReadRels(archive, RelsPathFor(masterPath));

        var background = BackgroundCss(cSld, rels, context)
            ?? BackgroundCss(layout?.Root?.Element(P + "cSld"), layoutRels, context)
            ?? BackgroundCss(master?.Root?.Element(P + "cSld"), masterRels, context)
            ?? "background:#FFFFFF";

        var shapes = new StringBuilder();

        var designLayers = new[]
        {
            (Root: master?.Root?.Element(P + "cSld"), Rels: masterRels),
            (Root: layout?.Root?.Element(P + "cSld"), Rels: layoutRels),
        };

        foreach (var layer in designLayers)
        {
            var tree = layer.Root?.Element(P + "spTree");
            if (tree is not null)
                WalkShapes(context, tree, layer.Rels, Xform.Identity, shapes, true);
        }

        var slideTree = cSld?.Element(P + "spTree");
        if (slideTree is not null)
            WalkShapes(context, slideTree, new Dictionary<string, string>(), Xform.Identity, shapes, false);

        var title = FirstText(slide.Root) ?? FirstText(layout?.Root);

        all.Append($"<section class=\"slide\" id=\"slide-{index}\" style=\"width:{slideW:0}px;" +
            $"height:{slideH:0}px;{background}\">{shapes}<div class=\"slide-no\">{index}</div></section>");

        return Truncate(title, 48);
    }

    private static string RelsPathFor(string partPath)
    {
        var directory = (Path.GetDirectoryName(partPath) ?? string.Empty).Replace('\\', '/');
        var file = Path.GetFileName(partPath);
        return $"{directory}/_rels/{file}.rels";
    }

    /// <summary>The part name of a relationship value, which also carries its type after a NUL.</summary>
    private static string Part(string entry)
    {
        var separator = entry.IndexOf('\u0000');
        return separator < 0 ? entry : entry[..separator];
    }

    private static string TargetOf(Dictionary<string, string> rels, string typeFragment)
    {
        foreach (var entry in rels.Values)
        {
            var separator = entry.IndexOf('\u0000');
            if (separator > 0 && entry[(separator + 1)..]
                    .Contains(typeFragment, StringComparison.OrdinalIgnoreCase))
                return entry[..separator];
        }

        return null;
    }

    private static void WalkShapes(
        RenderContext context,
        XElement container,
        Dictionary<string, string> rels,
        Xform transform,
        StringBuilder sb,
        bool designLayer)
    {
        foreach (var node in container.Elements())
        {
            if (node.Name == P + "sp")
                sb.Append(ShapeToHtml(context, node, rels, transform, designLayer));
            else if (node.Name == P + "pic")
                sb.Append(PictureToHtml(context, node, rels, transform));
            else if (node.Name == P + "graphicFrame")
                sb.Append(GraphicFrameToHtml(context, node, rels, transform));
            else if (node.Name == P + "grpSp")
                WalkShapes(context, node, rels, transform.Compose(node.Element(P + "grpSpPr")), sb, designLayer);
        }
    }

    private static string ShapeToHtml(
        RenderContext context, XElement sp, Dictionary<string, string> rels, Xform transform, bool designLayer)
    {
        var properties = sp.Element(P + "spPr");
        var position = transform.Map(Transform(properties));
        var txBody = sp.Element(P + "txBody");
        var content = new StringBuilder();
        if (txBody is not null)
        {
            foreach (var para in txBody.Elements(A + "p"))
                content.Append(ParagraphToHtml(context, para));
        }

        var isPlaceholder = sp.Descendants(P + "ph").Any();

        // A placeholder the slide leaves empty is a prompt, not content: drawing it would litter the design
        // layer with empty boxes.
        if (content.Length == 0 && (isPlaceholder || designLayer))
            return string.Empty;

        if (position is null)
            return string.Empty;

        var fill = FillCss(properties, rels, context);
        var style = $"left:{position.Value.Left:0}px;top:{position.Value.Top:0}px;" +
            $"width:{position.Value.Width:0}px;height:{position.Value.Height:0}px;{fill}";

        if (content.Length == 0)
            content.Append("<div class=\"empty-shape\"></div>");

        return $"<div class=\"shape\" style=\"{style}\">{content}</div>";
    }

    private static string GraphicFrameToHtml(
        RenderContext context, XElement frame, Dictionary<string, string> rels, Xform transform)
    {
        var position = transform.Map(Transform(frame.Element(P + "xfrm")));
        if (position is null)
            return string.Empty;

        var style = $"left:{position.Value.Left:0}px;top:{position.Value.Top:0}px;" +
            $"width:{position.Value.Width:0}px;height:{position.Value.Height:0}px";

        var table = frame.Descendants(A + "tbl").FirstOrDefault();
        if (table is not null)
            return $"<div class=\"shape table-shape\" style=\"{style}\">{TableToHtml(context, table)}</div>";

        var uri = (string)frame.Descendants(A + "graphicData").FirstOrDefault()?.Attribute("uri") ?? string.Empty;
        var label = uri.Contains("chart", StringComparison.OrdinalIgnoreCase) ? "Chart"
            : uri.Contains("diagram", StringComparison.OrdinalIgnoreCase) ? "SmartArt"
            : "Object";

        return $"<div class=\"shape placeholder-shape\" style=\"{style}\">{label}</div>";
    }

    private static string TableToHtml(RenderContext context, XElement table)
    {
        var sb = new StringBuilder("<table class=\"ppt-table\">");
        foreach (var row in table.Elements(A + "tr"))
        {
            sb.Append("<tr>");
            foreach (var cell in row.Elements(A + "tc"))
            {
                var span = (string)cell.Element(A + "tcPr")?.Attribute("gridSpan");
                var body = new StringBuilder();
                foreach (var para in cell.Element(A + "txBody")?.Elements(A + "p") ?? [])
                    body.Append(ParagraphToHtml(context, para));

                sb.Append("<td");
                if (int.TryParse(span, out var spanValue) && spanValue > 1)
                    sb.Append(" colspan=\"").Append(spanValue).Append('"');
                sb.Append('>').Append(body).Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    private static string PictureToHtml(
        RenderContext context, XElement pic, Dictionary<string, string> rels, Xform transform)
    {
        var position = transform.Map(Transform(pic.Element(P + "spPr")));
        if (position is null)
            return string.Empty;

        var url = ImageUrl(context, rels, ImageTarget(pic), position.Value.Width);
        if (url is null)
            return string.Empty;

        return $"<img class=\"pic\" src=\"{url}\" style=\"left:{position.Value.Left:0}px;" +
            $"top:{position.Value.Top:0}px;width:{position.Value.Width:0}px;height:{position.Value.Height:0}px\">";
    }

    private static string ImageTarget(XElement element) =>
        (string)element.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");

    private static string ImageUrl(RenderContext context, Dictionary<string, string> rels, string embed, double widthPx)
    {
        if (embed is null || string.IsNullOrEmpty(context.ImageFolder) ||
            context.ImageCount >= MaxImages || !rels.TryGetValue(embed, out var entry))
            return null;

        if (context.Cached(embed) is { } cached)
            return cached;

        var mediaPath = "ppt/" + Part(entry).TrimStart('/');
        var media = context.Archive.GetEntry(mediaPath);
        if (media is null)
            return null;

        byte[] bytes;
        using (var stream = media.Open())
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            bytes = ms.ToArray();
        }

        var name = OfficeImageStore.Save(context.ImageFolder, bytes, widthPx, $"img{context.ImageCount}");
        if (name is null)
            return null;

        context.ImageCount++;
        var url = $"https://{context.ImageHost}/{name}";
        context.Cache(embed, url);
        return url;
    }

    private static string ParagraphToHtml(RenderContext context, XElement para)
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

        foreach (var el in para.Elements(A + "r"))
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
                if (double.TryParse(sz, NumberStyles.Float, CultureInfo.InvariantCulture, out var hundredths) &&
                    hundredths > 0)
                    css.Add($"font-size:{hundredths / 100d:0.#}pt");
                var color = ColorOf(rPr.Element(A + "solidFill"), context);
                if (color is not null)
                    css.Add($"color:{color}");
                var face = rPr.Element(A + "latin")?.Attribute("typeface")?.Value;
                if (!string.IsNullOrEmpty(face) && !face.StartsWith('+'))
                    css.Add($"font-family:'{WebUtility.HtmlEncode(face)}'");
            }

            var text = new StringBuilder();
            foreach (var t in el.Elements(A + "t"))
                text.Append(WebUtility.HtmlEncode(t.Value));
            foreach (var _ in el.Elements(A + "br"))
                text.Append("<br>");

            if (text.Length == 0)
                continue;

            var style = css.Count > 0 ? $" style=\"{string.Join(";", css)}\"" : string.Empty;
            sb.Append("<span").Append(style).Append('>').Append(text).Append("</span>");
        }

        if (sb.Length == 0)
            return string.Empty;

        return $"<p style=\"text-align:{align}\">{sb}</p>";
    }

    /// <summary>
    /// v5.6.19: a fill as CSS - an explicit colour, a theme colour (with its luminance/shade/tint/alpha
    /// transforms) or a picture fill. Templates colour almost everything through the theme, which is why the
    /// slides used to come out with wrong or invisible colours.
    /// </summary>
    private static string FillCss(XElement properties, Dictionary<string, string> rels, RenderContext context)
    {
        if (properties is null)
            return string.Empty;

        var color = ColorOf(properties.Element(A + "solidFill"), context);
        if (color is not null)
            return $"background:{color};";

        var blip = properties.Element(A + "blipFill");
        if (blip is not null)
        {
            var url = ImageUrl(context, rels, ImageTarget(blip), 0);
            if (url is not null)
                return $"background-image:url('{url}');background-size:cover;background-position:center;";
        }

        return string.Empty;
    }

    private static string ColorOf(XElement fill, RenderContext context)
    {
        if (fill is null)
            return null;

        var srgb = fill.Element(A + "srgbClr");
        if (srgb is not null)
            return Transformed("#" + Normalize((string)srgb.Attribute("val")), srgb, context);

        var scheme = fill.Element(A + "schemeClr");
        if (scheme is not null)
            return Transformed(context.Theme.Color((string)scheme.Attribute("val")), scheme, context);

        var sys = fill.Element(A + "sysClr");
        if (sys is not null)
            return Transformed("#" + Normalize((string)sys.Attribute("lastClr")), sys, context);

        var preset = fill.Element(A + "prstClr");
        if (preset is not null)
            return PresetColors.GetValueOrDefault(Normalize((string)preset.Attribute("val")));

        return null;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "000000";

        return value.Length >= 6 ? value[..6] : value.PadRight(6, '0');
    }

    private static string Transformed(string hex, XElement owner, RenderContext context)
    {
        if (string.IsNullOrEmpty(hex))
            return hex;

        Color color;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            return hex;
        }

        var lumMod = Percentage(owner.Element(A + "lumMod")?.Attribute("val")?.Value);
        var lumOff = Percentage(owner.Element(A + "lumOff")?.Attribute("val")?.Value);
        var shade = Percentage(owner.Element(A + "shade")?.Attribute("val")?.Value);
        var tint = Percentage(owner.Element(A + "tint")?.Attribute("val")?.Value);
        var alpha = Percentage(owner.Element(A + "alpha")?.Attribute("val")?.Value);

        if (lumMod is not null || lumOff is not null)
        {
            var hsl = ToHsl(color);
            color = FromHsl(hsl.H, hsl.S, Math.Clamp(hsl.L * (lumMod ?? 1d) + (lumOff ?? 0d), 0d, 1d));
        }

        if (shade is not null)
        {
            color = Color.FromRgb(
                (byte)(color.R * shade), (byte)(color.G * shade), (byte)(color.B * shade));
        }

        if (tint is not null)
        {
            color = Color.FromRgb(
                (byte)(color.R + (255 - color.R) * (1 - tint)),
                (byte)(color.G + (255 - color.G) * (1 - tint)),
                (byte)(color.B + (255 - color.B) * (1 - tint)));
        }

        return alpha is null
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"rgba({color.R},{color.G},{color.B},{alpha.Value.ToString("0.##", CultureInfo.InvariantCulture)})";
    }

    private static double? Percentage(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number / 100000d
            : null;

    private static (double H, double S, double L) ToHsl(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2d;
        if (Math.Abs(max - min) < 0.0001)
            return (0d, 0d, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2d - max - min) : d / (max + min);
        var h = Math.Abs(max - r) < 0.0001 ? (g - b) / d + (g < b ? 6d : 0d)
            : Math.Abs(max - g) < 0.0001 ? (b - r) / d + 2d
            : (r - g) / d + 4d;
        return (h / 6d, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0d)
        {
            var grey = (byte)Math.Round(l * 255d);
            return Color.FromRgb(grey, grey, grey);
        }

        var q = l < 0.5 ? l * (1d + s) : l + s - l * s;
        var p = 2d * l - q;
        return Color.FromRgb(
            (byte)Math.Round(Hue(p, q, h + 1d / 3d) * 255d),
            (byte)Math.Round(Hue(p, q, h) * 255d),
            (byte)Math.Round(Hue(p, q, h - 1d / 3d) * 255d));
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0d) t += 1d;
        if (t > 1d) t -= 1d;
        if (t < 1d / 6d) return p + (q - p) * 6d * t;
        if (t < 1d / 2d) return q;
        if (t < 2d / 3d) return p + (q - p) * (2d / 3d - t) * 6d;
        return p;
    }

    private static readonly Dictionary<string, string> PresetColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#000000",
        ["white"] = "#FFFFFF",
        ["red"] = "#FF0000",
        ["green"] = "#008000",
        ["blue"] = "#0000FF",
        ["yellow"] = "#FFFF00",
        ["gray"] = "#808080",
        ["grey"] = "#808080",
        ["orange"] = "#FFA500",
        ["purple"] = "#800080",
        ["brown"] = "#A52A2A",
        ["darkGray"] = "#A9A9A9",
        ["lightGray"] = "#D3D3D3",
        ["darkBlue"] = "#00008B",
        ["darkRed"] = "#8B0000",
        ["darkGreen"] = "#006400",
    };

    private static string BackgroundCss(
        XElement cSld, Dictionary<string, string> rels, RenderContext context)
    {
        var background = cSld?.Element(P + "bg");
        if (background is null)
            return null;

        var fill = background.Element(P + "bgPr");
        if (fill is not null)
        {
            var color = ColorOf(fill.Element(A + "solidFill"), context);
            if (color is not null)
                return $"background:{color}";

            var blip = fill.Element(A + "blipFill");
            if (blip is not null)
            {
                var url = ImageUrl(context, rels, ImageTarget(blip), 0);
                if (url is not null)
                    return $"background-image:url('{url}');background-size:cover;background-position:center";
            }

            var gradient = fill.Element(A + "gradFill");
            if (gradient is not null)
            {
                var stops = gradient.Descendants(A + "gs")
                    .Select(gs => ColorOf(gs.Element(A + "solidFill"), context) ?? ColorOf(gs, context))
                    .OfType<string>()
                    .ToList();
                if (stops.Count >= 2)
                    return $"background:linear-gradient(135deg,{stops[0]},{stops[^1]})";
            }
        }

        var reference = background.Element(P + "bgRef");
        if (reference is not null)
        {
            var color = ColorOf(reference, context);
            if (color is not null)
                return $"background:{color}";
        }

        return null;
    }

    private static ThemeColors ReadTheme(ZipArchive archive)
    {
        var theme = ReadXml(archive, "ppt/theme/theme1.xml");
        var scheme = theme?.Root?.Element(A + "themeElements")?.Element(A + "clrScheme");
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (scheme is not null)
        {
            foreach (var entry in scheme.Elements())
            {
                var value = entry.Elements().FirstOrDefault();
                var hex = (string)value?.Attribute("val") ?? (string)value?.Attribute("lastClr");
                if (!string.IsNullOrEmpty(hex))
                    colors[entry.Name.LocalName] = "#" + Normalize(hex);
            }
        }

        return new ThemeColors(colors);
    }

    private static Xform Transform(XElement properties)
    {
        var xfrm = properties?.Element(A + "xfrm");
        if (xfrm is null)
            return Xform.Empty;

        var off = xfrm.Element(A + "off");
        var ext = xfrm.Element(A + "ext");
        return new Xform(
            1d, 1d,
            (double?)off?.Attribute("x") ?? 0d,
            (double?)off?.Attribute("y") ?? 0d,
            (double?)ext?.Attribute("cx") ?? 0d,
            (double?)ext?.Attribute("cy") ?? 0d);
    }

    private static string FirstText(XElement root) =>
        root?.Descendants(A + "t").Select(t => t.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string Truncate(string value, int length)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        value = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return value.Length <= length ? value : value[..length] + "…";
    }

    private static double EmuToPx(double emu) => emu / EmuPerInch * Dpi;

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
        foreach (var rel in rels.Root?.Elements(Rel + "Relationship") ?? [])
        {
            var id = (string)rel.Attribute("Id");
            var target = (string)rel.Attribute("Target");
            var type = (string)rel.Attribute("Type");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target))
                continue;

            // The layout/master lookups key on the relationship *type*, so it travels with the target.
            map[id] = type is null ? target : $"{target}\u0000{type.Split('/')[^1]}";
        }

        return map;
    }

    private static string ErrorHtml(string message) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>body{margin:0;color:#C42B1C;" +
        "background:#FFFFFF;font-family:'Segoe UI',sans-serif;padding:24px;font-size:14px}" +
        "</style></head><body><div>" + message + "</div></body></html>";

    private static void AppendCss(StringBuilder sb)
    {
        // v3.17.0: fixed light canvas, independent of the app theme.
        sb.Append("body{margin:0;background:#F3F3F3;display:flex;flex-direction:column;" +
            "align-items:center;min-height:100%;font-family:'Segoe UI',Helvetica,Arial,sans-serif;color:#1A1A1A}");
        sb.Append(".deck{display:contents}");
        sb.Append("::-webkit-scrollbar{width:8px;height:8px}");
        sb.Append("::-webkit-scrollbar-thumb{background:rgba(0,0,0,.28);border-radius:4px}");
        sb.Append(".slide{position:relative;flex:0 0 auto;margin:0 auto 0;box-shadow:0 2px 12px rgba(0,0,0,.18);" +
            "overflow:hidden}");
        sb.Append(".slide-no{position:absolute;right:10px;bottom:6px;font-size:11px;color:rgba(0,0,0,.35)}");
        sb.Append(".shape{position:absolute;overflow:hidden}");
        sb.Append(".shape p{margin:0}");
        sb.Append(".shape span{white-space:pre-wrap}");
        sb.Append(".empty-shape{width:100%;height:100%;outline:1px dashed rgba(0,0,0,.18)}");
        sb.Append(".pic{position:absolute;object-fit:fill}");
        sb.Append(".placeholder-shape{border:1px dashed rgba(0,0,0,.3);display:flex;align-items:center;" +
            "justify-content:center;color:rgba(0,0,0,.45);font-size:12px;box-sizing:border-box}");
        sb.Append(".table-shape{box-sizing:border-box}");
        sb.Append(".ppt-table{border-collapse:collapse;width:100%;height:100%;font-size:12px}");
        sb.Append(".ppt-table td{border:1px solid rgba(0,0,0,.25);padding:3px 6px;vertical-align:top}");
    }

    /// <summary>What one conversion needs to keep around: the archive, the picture folder and the theme.</summary>
    private sealed class RenderContext(
        ZipArchive archive, string imageFolder, string imageHost, ThemeColors theme)
    {
        private readonly Dictionary<string, string> _imageCache = new(StringComparer.OrdinalIgnoreCase);

        internal ZipArchive Archive { get; } = archive;

        internal string ImageFolder { get; } = imageFolder;

        internal string ImageHost { get; } = imageHost;

        internal ThemeColors Theme { get; } = theme;

        internal int ImageCount { get; set; }

        internal string Cached(string target) => _imageCache.GetValueOrDefault(target);

        internal void Cache(string target, string url) => _imageCache[target] = url;
    }

    /// <summary>The theme's colour scheme, with the aliases the PresentationML uses for it.</summary>
    private sealed class ThemeColors(Dictionary<string, string> colors)
    {
        internal string Color(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "#000000";

            var key = name switch
            {
                "tx1" => "dk1",
                "bg1" => "lt1",
                "tx2" => "dk2",
                "bg2" => "lt2",
                _ => name,
            };

            return colors.GetValueOrDefault(key)
                ?? colors.GetValueOrDefault(name)
                ?? (key is "dk1" ? "#000000" : "#FFFFFF");
        }
    }

    /// <summary>
    /// v5.6.19: a shape's box in slide pixels. Groups carry their own placement and their own coordinate
    /// space, so a child inside a group has to be mapped through it before it means anything on the slide.
    /// </summary>
    private readonly record struct Xform(
        double ScaleX, double ScaleY, double OffsetX, double OffsetY, double Width, double Height)
    {
        internal static readonly Xform Identity = new(1, 1, 0, 0, 0, 0);

        internal static readonly Xform Empty = new(0, 0, 0, 0, 0, 0);

        internal (double Left, double Top, double Width, double Height)? Map(Xform child)
        {
            if (child.ScaleX == 0 || child.ScaleY == 0 || child.Width <= 0 || child.Height <= 0)
                return null;

            return (
                EmuToPx(OffsetX + child.OffsetX * ScaleX),
                EmuToPx(OffsetY + child.OffsetY * ScaleY),
                EmuToPx(child.Width * ScaleX),
                EmuToPx(child.Height * ScaleY));
        }

        /// <summary>Composes this transform with the group placement of <paramref name="groupProperties"/>.</summary>
        internal Xform Compose(XElement groupProperties)
        {
            var xfrm = groupProperties?.Element(A + "xfrm");
            var off = xfrm?.Element(A + "off");
            var ext = xfrm?.Element(A + "ext");
            var chOff = xfrm?.Element(A + "chOff");
            var chExt = xfrm?.Element(A + "chExt");
            if (off is null || ext is null || chOff is null || chExt is null)
                return this;

            var chW = (double?)chExt.Attribute("cx") ?? 0d;
            var chH = (double?)chExt.Attribute("cy") ?? 0d;
            if (chW <= 0 || chH <= 0)
                return this;

            var sx = ((double?)ext.Attribute("cx") ?? 0d) / chW;
            var sy = ((double?)ext.Attribute("cy") ?? 0d) / chH;
            var ox = (double?)off.Attribute("x") ?? 0d;
            var oy = (double?)off.Attribute("y") ?? 0d;
            var cox = (double?)chOff.Attribute("x") ?? 0d;
            var coy = (double?)chOff.Attribute("y") ?? 0d;

            return new Xform(
                ScaleX * sx, ScaleY * sy,
                OffsetX + (ox - cox * sx) * ScaleX,
                OffsetY + (oy - coy * sy) * ScaleY,
                Width, Height);
        }
    }
}
