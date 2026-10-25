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
using System.Threading;
using System.Windows.Media;
using System.Xml.Linq;

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v3.12.0: renders a .pptx slide by slide, so the preview shows the deck in the app's own window.
/// <para>
/// v5.7.0: three things used to make templates come out nearly empty, and all three are handled now:
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
    private static long _svgPaintId;

    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace Dsp = "http://schemas.microsoft.com/office/drawing/2008/diagram";
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

                    var slidePath = Part(entry).TrimStart('/');
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
            sb.Append("<script>" +
                "window._slides=Array.from(document.querySelectorAll('.slide'));" +
                "window._frames=Array.from(document.querySelectorAll('.slide-frame'));" +
                "function fitSlides(){" +
                "const width=Math.max(160,document.documentElement.clientWidth-48);" +
                "window._slides.forEach((slide,n)=>{" +
                "const naturalWidth=Number(slide.dataset.width);" +
                "const naturalHeight=Number(slide.dataset.height);" +
                "if(!(naturalWidth>0&&naturalHeight>0))return;" +
                "const scale=width/naturalWidth;" +
                "const frame=window._frames[n];" +
                "frame.style.width=(naturalWidth*scale)+'px';" +
                "frame.style.height=(naturalHeight*scale)+'px';" +
                "slide.style.transform='scale('+scale+')';});}" +
                "window.scrollSlide=function(i){" +
                "i=Math.max(0,Math.min(window._frames.length-1,i));" +
                "window._frames[i]?.scrollIntoView({behavior:'smooth',block:'start'});};" +
                "let current=-1;" +
                "const observer=new IntersectionObserver(entries=>{" +
                "const visible=entries.filter(e=>e.isIntersecting)" +
                ".sort((a,b)=>b.intersectionRatio-a.intersectionRatio)[0];" +
                "if(!visible)return;" +
                "const index=window._frames.indexOf(visible.target);" +
                "if(index<0||index===current)return;" +
                "current=index;" +
                "window.chrome?.webview?.postMessage(JSON.stringify({type:'slide',index:index}));" +
                "},{rootMargin:'-20% 0px -55% 0px',threshold:[0,.15,.5,1]});" +
                "window._frames.forEach(frame=>observer.observe(frame));" +
                "window.addEventListener('resize',fitSlides);" +
                "window.addEventListener('load',fitSlides);" +
                "fitSlides();</script>");
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
        var layoutPlaceholders = PlaceholderShapes(layout?.Root?.Element(P + "cSld")?.Element(P + "spTree"));
        var masterPlaceholders = PlaceholderShapes(master?.Root?.Element(P + "cSld")?.Element(P + "spTree"));
        var slidePlaceholders = layoutPlaceholders.Concat(masterPlaceholders).ToArray();

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
                WalkShapes(
                    context, tree, layer.Rels, Xform.Identity, shapes, true,
                    layer.Root == layout?.Root?.Element(P + "cSld") ? masterPlaceholders : null,
                    master?.Root);
        }

        var slideTree = cSld?.Element(P + "spTree");
        if (slideTree is not null)
            WalkShapes(
                context, slideTree, rels, Xform.Identity, shapes, false, slidePlaceholders, master?.Root);

        var title = FirstText(slide.Root) ?? FirstText(layout?.Root);

        all.Append($"<article class=\"slide-frame\" id=\"slide-{index}\" " +
            $"data-width=\"{slideW:0}\" data-height=\"{slideH:0}\">" +
            $"<section class=\"slide\" data-width=\"{slideW:0}\" data-height=\"{slideH:0}\" " +
            $"style=\"width:{slideW:0}px;height:{slideH:0}px;{background}\">" +
            $"{shapes}<div class=\"slide-no\">{index}</div></section></article>");

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
        bool designLayer,
        IReadOnlyList<XElement> inheritedPlaceholders = null,
        XElement masterRoot = null,
        XElement groupProperties = null)
    {
        foreach (var node in container.Elements())
        {
            if (node.Name == P + "sp")
                sb.Append(ShapeToHtml(
                    context, node, rels, transform, designLayer, inheritedPlaceholders,
                    masterRoot, groupProperties));
            else if (node.Name == P + "cxnSp")
                sb.Append(ConnectorToHtml(context, node, rels, transform));
            else if (node.Name == P + "pic")
                sb.Append(PictureToHtml(context, node, rels, transform));
            else if (node.Name == P + "graphicFrame")
                sb.Append(GraphicFrameToHtml(context, node, rels, transform));
            else if (node.Name == P + "grpSp")
            {
                var ownProperties = node.Element(P + "grpSpPr");
                var effectiveGroupProperties = HasFill(ownProperties)
                    ? ownProperties
                    : groupProperties;

                // A group carries a second coordinate space for its children and
                // may rotate or mirror the whole group. Render the children in a
                // local box, then apply the group transform to that box. Applying
                // only the child-coordinate scale, as the old renderer did, put
                // flipped decorations on the wrong side of many slides.
                var groupContent = new StringBuilder();
                WalkShapes(
                    context, node, rels, transform.ForGroupChildren(ownProperties), groupContent,
                    designLayer, inheritedPlaceholders, masterRoot, effectiveGroupProperties);

                var position = transform.Map(Transform(ownProperties));
                if (position is null)
                {
                    sb.Append(groupContent);
                    continue;
                }

                var groupStyle =
                    $"left:{position.Value.Left:0.###}px;top:{position.Value.Top:0.###}px;" +
                    $"width:{position.Value.Width:0.###}px;height:{position.Value.Height:0.###}px;" +
                    TransformCss(ownProperties);
                sb.Append($"<div class=\"group\" style=\"{groupStyle}\">{groupContent}</div>");
            }
        }
    }

    private static string ShapeToHtml(
        RenderContext context,
        XElement sp,
        Dictionary<string, string> rels,
        Xform transform,
        bool designLayer,
        IReadOnlyList<XElement> inheritedPlaceholders,
        XElement masterRoot,
        XElement groupProperties)
    {
        var properties = sp.Element(P + "spPr");
        var isPlaceholder = PlaceholderOf(sp) is not null;
        var inherited = isPlaceholder
            ? FindInheritedPlaceholder(sp, inheritedPlaceholders)
            : null;
        var ownTransform = Transform(properties);
        var effectiveTransform = HasGeometry(ownTransform)
            ? ownTransform
            : Transform(inherited?.Element(P + "spPr"));
        var presetGeometry = properties?.Element(A + "prstGeom") ??
            inherited?.Element(P + "spPr")?.Element(A + "prstGeom");
        var geometry = (string)presetGeometry?.Attribute("prst");
        var customGeometry = properties?.Element(A + "custGeom") ??
            inherited?.Element(P + "spPr")?.Element(A + "custGeom");
        var position = transform.Map(
            effectiveTransform,
            allowZeroDimension: geometry is "line" or "straightConnector1");
        var txBody = sp.Element(P + "txBody");
        var content = new StringBuilder();
        if (txBody is not null)
        {
            foreach (var para in txBody.Elements(A + "p"))
            {
                var level = int.TryParse(
                    (string)para.Element(A + "pPr")?.Attribute("lvl"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var paragraphLevel)
                    ? Math.Clamp(paragraphLevel, 0, 8)
                    : 0;
                var inheritedLevel = isPlaceholder
                    ? FindInheritedLevelStyle(
                        sp, inheritedPlaceholders, masterRoot, level)
                    : null;
                var shapeLevel = txBody.Element(A + "lstStyle")
                    ?.Element(A + $"lvl{level + 1}pPr");
                content.Append(ParagraphToHtml(context, para, inheritedLevel, shapeLevel));
            }
        }

        if (isPlaceholder && designLayer)
            content.Clear();

        if (position is null)
            return string.Empty;

        var inheritedProperties = inherited?.Element(P + "spPr");
        var shapeStyle = sp.Element(P + "style") ?? inherited?.Element(P + "style");
        var fillSource = properties?.Element(A + "grpFill") is not null
            ? groupProperties
            : inheritedProperties;
        var fill = FillCss(properties, rels, context, shapeStyle, fillSource);
        var outline = OutlineCss(properties, context, shapeStyle, inheritedProperties);
        var hasVisual = fill.Length > 0 || outline.Length > 0 ||
            HasEffects(properties) || HasEffects(inheritedProperties) ||
            shapeStyle?.Element(A + "fillRef") is not null ||
            shapeStyle?.Element(A + "lnRef") is not null;

        // A master/layout placeholder is a prompt, not slide text. Its geometry and
        // paint still matter: many templates implement cards and coloured panels as
        // placeholders whose fill lives on the layout.
        if (isPlaceholder && content.Length == 0 && !hasVisual)
            return string.Empty;

        if (geometry is "line" or "straightConnector1")
            return LineToHtml(context, properties, inheritedProperties, shapeStyle, position.Value);

        if (geometry == "arc" && content.Length == 0)
            return ArcToHtml(
                context, properties, inheritedProperties, shapeStyle, presetGeometry, position.Value);

        if (customGeometry is not null && content.Length == 0)
            return CustomShapeToHtml(
                context, properties, fillSource, shapeStyle, customGeometry, position.Value);

        var style = $"left:{position.Value.Left:0}px;top:{position.Value.Top:0}px;" +
            $"width:{position.Value.Width:0}px;height:{Math.Max(1d, position.Value.Height):0}px;" +
            $"{fill}{outline}{GeometryCss(geometry)}{EffectCss(properties, context)}" +
            $"{TransformCss(properties ?? inheritedProperties)}";

        if (content.Length > 0)
        {
            var textStyle = TextBodyCss(txBody?.Element(A + "bodyPr"));
            content.Insert(0, $"<div class=\"shape-text\" style=\"{textStyle}\">")
                .Append("</div>");
        }

        return $"<div class=\"shape\" style=\"{style}\">{content}</div>";
    }

    private static string CustomShapeToHtml(
        RenderContext context,
        XElement properties,
        XElement inheritedProperties,
        XElement shapeStyle,
        XElement geometry,
        (double Left, double Top, double Width, double Height) position)
    {
        var fill = ShapeFillValue(properties, context, shapeStyle, inheritedProperties);
        var line = ReadLine(properties, context, shapeStyle, inheritedProperties);
        var svg = new StringBuilder();
        var hasPath = false;

        foreach (var path in geometry.Descendants(A + "path"))
        {
            var pathWidth = (double?)path.Attribute("w") ?? position.Width;
            var pathHeight = (double?)path.Attribute("h") ?? position.Height;
            if (pathWidth <= 0 || pathHeight <= 0)
                continue;

            var data = CustomPathData(path, pathWidth, pathHeight);
            if (data.Length == 0)
                continue;

            var fills = (string)path.Attribute("fill") != "none" && fill != "none";
            var stroked = (string)path.Attribute("stroke") != "false" && line is not null;
            var strokeWidth = line is null
                ? 0d
                : line.Value.Width * pathWidth / Math.Max(1d, position.Width);
            svg.Append($"<path d=\"{data}\" fill=\"{(fills ? fill : "none")}\" " +
                $"fill-rule=\"evenodd\" stroke=\"{(stroked ? line?.Color : "none")}\" " +
                $"stroke-width=\"{strokeWidth:0.###}\" stroke-linejoin=\"round\" " +
                $"stroke-linecap=\"round\" transform=\"scale({1000d / pathWidth:0.####}," +
                $"{1000d / pathHeight:0.####})\"/>");
            hasPath = true;
        }

        if (!hasPath)
            return string.Empty;

        var style = $"left:{position.Left:0}px;top:{position.Top:0}px;" +
            $"width:{position.Width:0}px;height:{Math.Max(1d, position.Height):0}px;" +
            $"{TransformCss(properties ?? inheritedProperties)}";
        return $"<div class=\"shape custom-shape\" style=\"{style}\">" +
            "<svg viewBox=\"0 0 1000 1000\" preserveAspectRatio=\"none\" " +
            "width=\"100%\" height=\"100%\" overflow=\"visible\">" +
            svg + "</svg></div>";
    }

    private static string CustomPathData(XElement path, double pathWidth, double pathHeight)
    {
        var guides = CustomGuides(path.Parent?.Parent, pathWidth, pathHeight);
        var sb = new StringBuilder();
        foreach (var command in path.Elements())
        {
            if (command.Name == A + "moveTo")
            {
                var point = command.Element(A + "pt");
                sb.Append("M").Append(CustomPoint(point, guides, pathWidth, pathHeight));
            }
            else if (command.Name == A + "lnTo")
            {
                var point = command.Element(A + "pt");
                sb.Append("L").Append(CustomPoint(point, guides, pathWidth, pathHeight));
            }
            else if (command.Name == A + "cubicBezTo")
            {
                sb.Append('C');
                foreach (var point in command.Elements(A + "pt"))
                    sb.Append(CustomPoint(point, guides, pathWidth, pathHeight));
            }
            else if (command.Name == A + "quadBezTo")
            {
                sb.Append('Q');
                foreach (var point in command.Elements(A + "pt"))
                    sb.Append(CustomPoint(point, guides, pathWidth, pathHeight));
            }
            else if (command.Name == A + "arcTo")
            {
                // DrawingML arcs are rare in presentation logos. Keep the contour
                // connected; the remaining linear/Bezier commands still render.
                continue;
            }
            else if (command.Name == A + "close")
            {
                sb.Append('Z');
            }
        }

        return sb.ToString();
    }

    private static Dictionary<string, double> CustomGuides(
        XElement geometry, double pathWidth, double pathHeight)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["w"] = pathWidth,
            ["h"] = pathHeight,
        };
        foreach (var guide in geometry?.Element(A + "gdLst")?.Elements(A + "gd") ?? [])
        {
            var name = (string)guide.Attribute("name");
            var formula = (string)guide.Attribute("fmla");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(formula))
                continue;

            var parts = formula.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            double Argument(int index) => index < parts.Length &&
                CustomNumber(parts[index], values, pathWidth, pathHeight, 0d) is { } number
                    ? number
                    : 0d;
            var result = parts[0] switch
            {
                // DrawingML formulas apply their operands left to right:
                // */ a b c = a*b/c, +- a b c = a+b-c, +/ a b c = (a+b)/c.
                "*/" when parts.Length >= 4 => Argument(1) * Argument(2) / Math.Max(1d, Argument(3)),
                "+-" when parts.Length >= 4 => Argument(1) + Argument(2) - Argument(3),
                "+/" when parts.Length >= 4 => (Argument(1) + Argument(2)) / Math.Max(1d, Argument(3)),
                "*/" when parts.Length == 2 => Argument(1),
                _ => CustomNumber(parts[^1], values, pathWidth, pathHeight, 0d) ?? 0d,
            };
            values[name] = result;
        }

        return values;
    }

    private static string CustomPoint(
        XElement point,
        IReadOnlyDictionary<string, double> guides,
        double pathWidth,
        double pathHeight)
    {
        var x = CustomNumber((string)point?.Attribute("x"), guides, pathWidth, pathHeight, 0d) ?? 0d;
        var y = CustomNumber((string)point?.Attribute("y"), guides, pathWidth, pathHeight, 0d) ?? 0d;
        return $"{x:0.####},{y:0.####} ";
    }

    private static double? CustomNumber(
        string value,
        IReadOnlyDictionary<string, double> guides,
        double pathWidth,
        double pathHeight,
        double? fallback)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number;
        if (string.Equals(value, "w", StringComparison.Ordinal))
            return pathWidth;
        if (string.Equals(value, "h", StringComparison.Ordinal))
            return pathHeight;
        return value is not null && guides.TryGetValue(value, out var guided) ? guided : fallback;
    }

    private static string ConnectorToHtml(
        RenderContext context,
        XElement connector,
        Dictionary<string, string> rels,
        Xform transform)
    {
        var properties = connector.Element(P + "spPr");
        var position = transform.Map(Transform(properties), allowZeroDimension: true);
        if (position is null)
            return string.Empty;

        return LineToHtml(
            context,
            properties,
            null,
            connector.Element(P + "style"),
            position.Value,
            (string)properties?.Element(A + "prstGeom")?.Attribute("prst"));
    }

    private static string LineToHtml(
        RenderContext context,
        XElement properties,
        XElement inheritedProperties,
        XElement shapeStyle,
        (double Left, double Top, double Width, double Height) position,
        string geometry = null)
    {
        var line = ReadLine(properties, context, shapeStyle, inheritedProperties);
        var width = Math.Max(1d, position.Width);
        var height = Math.Max(1d, position.Height);
        var viewWidth = Math.Max(0.01d, position.Width);
        var viewHeight = Math.Max(0.01d, position.Height);
        var dash = line?.Dash.Length > 0
            ? $" stroke-dasharray=\"{WebUtility.HtmlEncode(line.Value.Dash)}\""
            : string.Empty;
        var transform = TransformCss(properties ?? inheritedProperties);
        var paint = LineSvgPaint(properties, inheritedProperties, line);
        var path = ConnectorPath(geometry, viewWidth, viewHeight);
        var strokeShape = path is null
            ? $"<line x1=\"0\" y1=\"0\" x2=\"{viewWidth:0.###}\" " +
                $"y2=\"{viewHeight:0.###}\" fill=\"none\""
            : $"<path d=\"{path}\" fill=\"none\"";

        return $"<div class=\"connector\" style=\"left:{position.Left:0.###}px;top:{position.Top:0.###}px;" +
            $"width:{width:0.###}px;height:{height:0.###}px;{transform}\">" +
            $"<svg width=\"100%\" height=\"100%\" viewBox=\"0 0 {viewWidth:0.###} {viewHeight:0.###}\" " +
            "preserveAspectRatio=\"none\" overflow=\"visible\" shape-rendering=\"geometricPrecision\" " +
            "aria-hidden=\"true\">" +
            paint.Defs +
            strokeShape +
            $" stroke=\"{paint.Stroke}\" stroke-width=\"{Math.Max(0.75d, line?.Width ?? 1d):0.##}\" " +
            "vector-effect=\"non-scaling-stroke\"" +
            paint.Attributes +
            dash + "/></svg></div>";
    }

    private static string ConnectorPath(string geometry, double width, double height) =>
        geometry switch
        {
            "bentConnector2" => $"M0,0 H{width:0.###} V{height:0.###}",
            "bentConnector3" =>
                $"M0,0 H{width / 2d:0.###} V{height:0.###} H{width:0.###}",
            "bentConnector4" =>
                $"M0,0 H{width / 2d:0.###} V{height / 2d:0.###} " +
                $"H{width:0.###} V{height:0.###}",
            "bentConnector5" =>
                $"M0,0 H{width * 0.35d:0.###} V{height * 0.5d:0.###} " +
                $"H{width * 0.65d:0.###} V{height:0.###} H{width:0.###}",
            "curvedConnector2" =>
                $"M0,0 C{width * 0.5d:0.###},0 " +
                $"{width * 0.5d:0.###},{height:0.###} {width:0.###},{height:0.###}",
            "curvedConnector3" =>
                $"M0,0 H{width / 2d:0.###} " +
                $"C{width / 2d:0.###},{height * 0.5d:0.###} " +
                $"{width / 2d:0.###},{height:0.###} {width:0.###},{height:0.###}",
            "curvedConnector4" or "curvedConnector5" =>
                $"M0,0 C{width * 0.33d:0.###},0 " +
                $"{width * 0.33d:0.###},{height:0.###} {width * 0.5d:0.###},{height * 0.5d:0.###} " +
                $"S{width * 0.67d:0.###},{height:0.###} {width:0.###},{height:0.###}",
            _ => null,
        };

    private static string ArcToHtml(
        RenderContext context,
        XElement properties,
        XElement inheritedProperties,
        XElement shapeStyle,
        XElement geometry,
        (double Left, double Top, double Width, double Height) position)
    {
        var line = ReadLine(properties, context, shapeStyle, inheritedProperties);
        if (line is null)
            return string.Empty;

        var start = PresetGuide(geometry, "adj1", 16200000d) / 60000d;
        var end = PresetGuide(geometry, "adj2", 0d) / 60000d;
        var sweep = (end - start) % 360d;
        if (sweep <= 0.001d)
            sweep += 360d;

        var radiusX = Math.Max(0.01d, position.Width / 2d);
        var radiusY = Math.Max(0.01d, position.Height / 2d);
        var centerX = position.Width / 2d;
        var centerY = position.Height / 2d;
        var startPoint = EllipsePoint(centerX, centerY, radiusX, radiusY, start);
        var endPoint = EllipsePoint(centerX, centerY, radiusX, radiusY, end);
        var largeArc = sweep > 180d ? 1 : 0;
        var path = $"M{startPoint.X:0.###},{startPoint.Y:0.###} " +
            $"A{radiusX:0.###},{radiusY:0.###} 0 {largeArc} 1 " +
            $"{endPoint.X:0.###},{endPoint.Y:0.###}";
        var paint = LineSvgPaint(properties, inheritedProperties, line);
        var dash = line.Value.Dash.Length > 0
            ? $" stroke-dasharray=\"{WebUtility.HtmlEncode(line.Value.Dash)}\""
            : string.Empty;

        return $"<div class=\"connector arc\" style=\"left:{position.Left:0.###}px;" +
            $"top:{position.Top:0.###}px;width:{position.Width:0.###}px;" +
            $"height:{position.Height:0.###}px;{TransformCss(properties ?? inheritedProperties)}\">" +
            $"<svg width=\"100%\" height=\"100%\" viewBox=\"0 0 {position.Width:0.###} " +
            $"{position.Height:0.###}\" preserveAspectRatio=\"none\" overflow=\"visible\" " +
            "shape-rendering=\"geometricPrecision\" aria-hidden=\"true\">" +
            paint.Defs +
            $"<path d=\"{path}\" fill=\"none\" stroke=\"{paint.Stroke}\" " +
            $"stroke-width=\"{Math.Max(0.75d, line.Value.Width):0.##}\" " +
            "vector-effect=\"non-scaling-stroke\"" +
            paint.Attributes + dash + "/></svg></div>";
    }

    private static (double X, double Y) EllipsePoint(
        double centerX, double centerY, double radiusX, double radiusY, double degrees)
    {
        var radians = degrees * Math.PI / 180d;
        return (
            centerX + Math.Cos(radians) * radiusX,
            centerY + Math.Sin(radians) * radiusY);
    }

    private static double PresetGuide(XElement geometry, string name, double fallback)
    {
        var formula = (string)geometry?.Element(A + "avLst")
            ?.Elements(A + "gd")
            .FirstOrDefault(guide => string.Equals(
                (string)guide.Attribute("name"), name, StringComparison.Ordinal))
            ?.Attribute("fmla");
        var value = formula?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;
    }

    private static SvgLinePaint LineSvgPaint(
        XElement properties, XElement inheritedProperties, LinePaint? resolvedLine)
    {
        var lineElement = properties?.Element(A + "ln") ??
            inheritedProperties?.Element(A + "ln");
        var stroke = resolvedLine?.Color ?? "#000000";
        var defs = new StringBuilder();
        var startColor = resolvedLine?.Gradient?.StartColor ?? stroke;
        var endColor = resolvedLine?.Gradient?.EndColor ?? stroke;

        if (resolvedLine?.Gradient is { } gradient)
        {
            var id = $"line-paint-{Interlocked.Increment(ref _svgPaintId)}";
            var radians = gradient.Angle * Math.PI / 180d;
            var dx = Math.Cos(radians) * 50d;
            var dy = Math.Sin(radians) * 50d;
            defs.Append($"<linearGradient id=\"{id}\" x1=\"{50d - dx:0.###}%\" " +
                $"y1=\"{50d - dy:0.###}%\" x2=\"{50d + dx:0.###}%\" " +
                $"y2=\"{50d + dy:0.###}%\">" +
                $"<stop offset=\"0%\" stop-color=\"{gradient.StartColor}\"/>" +
                $"<stop offset=\"100%\" stop-color=\"{gradient.EndColor}\"/>" +
                "</linearGradient>");
            stroke = $"url(#{id})";
        }

        var head = ArrowAttribute(lineElement?.Element(A + "headEnd"), startColor, "start", defs);
        var tail = ArrowAttribute(lineElement?.Element(A + "tailEnd"), endColor, "end", defs);
        var attributes = new StringBuilder();
        if (head.Length > 0)
            attributes.Append(' ').Append(head);
        if (tail.Length > 0)
            attributes.Append(' ').Append(tail);
        var cap = (string)lineElement?.Attribute("cap") switch
        {
            "rnd" => "round",
            "sq" => "square",
            _ => "butt",
        };
        var join = lineElement?.Element(A + "round") is not null ? "round"
            : lineElement?.Element(A + "bevel") is not null ? "bevel"
            : "miter";
        attributes.Append($" stroke-linecap=\"{cap}\" stroke-linejoin=\"{join}\"");

        return new SvgLinePaint(
            defs.Length > 0 ? $"<defs>{defs}</defs>" : string.Empty,
            stroke,
            attributes.ToString());
    }

    private static string ArrowAttribute(
        XElement arrow,
        string color,
        string position,
        StringBuilder defs)
    {
        var type = (string)arrow?.Attribute("type");
        if (string.IsNullOrEmpty(type) || type == "none")
            return string.Empty;

        var id = $"line-arrow-{position}-{Interlocked.Increment(ref _svgPaintId)}";
        var shape = type switch
        {
            "oval" => "<ellipse cx=\"5\" cy=\"5\" rx=\"4.5\" ry=\"4.5\"/>",
            "diamond" => "<path d=\"M0,5 L5,0 L10,5 L5,10 Z\"/>",
            "arrow" => "<path d=\"M0,0 L10,5 L0,10\" fill=\"none\" " +
                $"stroke=\"{color}\" stroke-width=\"1.5\"/>",
            "stealth" => "<path d=\"M0,0 L10,5 L0,10 L3,5 Z\"/>",
            _ => "<path d=\"M0,0 L10,5 L0,10 Z\"/>",
        };
        defs.Append($"<marker id=\"{id}\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" " +
            "markerWidth=\"7\" markerHeight=\"7\" markerUnits=\"strokeWidth\" " +
            "orient=\"auto-start-reverse\" overflow=\"visible\">" +
            $"<g fill=\"{color}\">{shape}</g></marker>");
        return $"marker-{position}=\"url(#{id})\"";
    }

    private static XElement PlaceholderOf(XElement shape) =>
        shape?.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph");

    private static IReadOnlyList<XElement> PlaceholderShapes(XElement tree) =>
        tree?.Elements(P + "sp").Where(sp => PlaceholderOf(sp) is not null).ToArray()
            ?? Array.Empty<XElement>();

    /// <summary>
    /// A slide placeholder commonly omits its own geometry: the coordinates live on
    /// the matching placeholder in the slide layout, or in the master when the layout
    /// also inherits them. Match idx first and type second, as PresentationML defines.
    /// </summary>
    private static XElement FindInheritedPlaceholder(
        XElement shape, IReadOnlyList<XElement> candidates)
    {
        var placeholder = PlaceholderOf(shape);
        if (placeholder is null || candidates is null || candidates.Count == 0)
            return null;

        XElement best = null;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            var score = PlaceholderMatchScore(shape, candidate);

            // Some layouts intentionally leave only the style (not the geometry)
            // to themselves and inherit their boxes from the master. Prefer a
            // matching source that actually carries a usable xfrm.
            if (score > 0 && HasGeometry(Transform(candidate.Element(P + "spPr"))))
                score++;

            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private static int PlaceholderMatchScore(XElement shape, XElement candidate)
    {
        var placeholder = PlaceholderOf(shape);
        var inherited = PlaceholderOf(candidate);
        if (placeholder is null || inherited is null)
            return 0;

        var index = (string)placeholder.Attribute("idx");
        var type = (string)placeholder.Attribute("type");
        var inheritedIndex = (string)inherited.Attribute("idx");
        var inheritedType = (string)inherited.Attribute("type");
        var score = 0;

        if (!string.IsNullOrEmpty(index) &&
            string.Equals(index, inheritedIndex, StringComparison.Ordinal))
            score += 4;

        if (!string.IsNullOrEmpty(type) &&
            string.Equals(type, inheritedType, StringComparison.Ordinal))
            score += 4;

        if (TitleFamily(type) && TitleFamily(inheritedType))
            score++;

        return score;
    }

    private static XElement FindInheritedLevelStyle(
        XElement shape,
        IReadOnlyList<XElement> candidates,
        XElement masterRoot,
        int level)
    {
        var levelName = $"lvl{level + 1}pPr";
        var own = shape.Element(P + "txBody")
            ?.Element(A + "lstStyle")?.Element(A + levelName);
        if (own is not null)
            return own;

        XElement best = null;
        var bestScore = 0;
        foreach (var candidate in candidates ?? [])
        {
            var score = PlaceholderMatchScore(shape, candidate);
            if (score <= bestScore)
                continue;

            var style = candidate.Element(P + "txBody")
                ?.Element(A + "lstStyle")?.Element(A + levelName);
            if (style is null)
                continue;

            best = style;
            bestScore = score;
        }

        if (best is not null)
            return best;

        var type = (string)PlaceholderOf(shape)?.Attribute("type");
        var styleRoot = masterRoot?.Element(P + "txStyles")?.Element(
            TitleFamily(type) ? P + "titleStyle" : P + "bodyStyle");
        return styleRoot?.Element(A + levelName);
    }

    private static bool TitleFamily(string type) =>
        type is "title" or "ctrTitle" or "subTitle";

    private static bool HasGeometry(Xform transform) =>
        transform.ScaleX > 0 && transform.ScaleY > 0 &&
        transform.Width > 0 && transform.Height > 0;

    private static string GraphicFrameToHtml(
        RenderContext context, XElement frame, Dictionary<string, string> rels, Xform transform)
    {
        var position = transform.Map(Transform(frame.Element(P + "xfrm")));
        if (position is null)
            return string.Empty;

        var style = $"left:{position.Value.Left:0.###}px;top:{position.Value.Top:0.###}px;" +
            $"width:{position.Value.Width:0.###}px;height:{position.Value.Height:0.###}px";

        var table = frame.Descendants(A + "tbl").FirstOrDefault();
        if (table is not null)
            return $"<div class=\"shape table-shape\" style=\"{style}\">{TableToHtml(context, table)}</div>";

        var chartId = (string)frame.Descendants(C + "chart").FirstOrDefault()?.Attribute(R + "id");
        if (!string.IsNullOrEmpty(chartId) && rels.TryGetValue(chartId, out var chartEntry))
        {
            var chartPath = Part(chartEntry).TrimStart('/');
            var chart = ReadXml(context.Archive, chartPath);
            var chartSvg = ChartToHtml(context, chart);
            if (chartSvg.Length > 0)
                return $"<div class=\"shape chart-shape\" style=\"{style}\">{chartSvg}</div>";
        }

        var uri = (string)frame.Descendants(A + "graphicData").FirstOrDefault()?.Attribute("uri") ?? string.Empty;
        if (uri.Contains("diagram", StringComparison.OrdinalIgnoreCase))
        {
            var drawingPath = TargetOf(rels, "diagramDrawing");
            var drawing = drawingPath is null ? null : ReadXml(context.Archive, drawingPath);
            if (drawing is not null)
            {
                var drawingRels = ReadRels(context.Archive, RelsPathFor(drawingPath));
                var smartArt = SmartArtToHtml(
                    context, drawing, drawingRels, frame, position.Value);
                if (smartArt.Length > 0)
                    return $"<div class=\"shape smartart\" style=\"{style}\">{smartArt}</div>";
            }
        }

        var label = uri.Contains("chart", StringComparison.OrdinalIgnoreCase) ? "Chart"
            : uri.Contains("diagram", StringComparison.OrdinalIgnoreCase) ? "SmartArt"
            : "Object";

        return $"<div class=\"shape placeholder-shape\" style=\"{style}\">{label}</div>";
    }

    private static string SmartArtToHtml(
        RenderContext context,
        XDocument drawing,
        Dictionary<string, string> rels,
        XElement frame,
        (double Left, double Top, double Width, double Height) position)
    {
        var shapes = drawing.Root?.Descendants(Dsp + "sp").ToArray() ?? [];
        if (shapes.Length == 0)
            return string.Empty;

        var frameTransform = Transform(frame.Element(P + "xfrm"));
        var baseWidth = EmuToPx(frameTransform.Width);
        var baseHeight = EmuToPx(frameTransform.Height);
        var scaleX = baseWidth > 0.01d ? position.Width / baseWidth : 1d;
        var scaleY = baseHeight > 0.01d ? position.Height / baseHeight : 1d;
        var sb = new StringBuilder();

        foreach (var shape in shapes)
        {
            var properties = shape.Element(Dsp + "spPr");
            var shapeTransform = Transform(properties);
            if (!HasGeometry(shapeTransform))
                continue;

            var left = EmuToPx(shapeTransform.OffsetX) * scaleX;
            var top = EmuToPx(shapeTransform.OffsetY) * scaleY;
            var width = EmuToPx(shapeTransform.Width) * scaleX;
            var height = EmuToPx(shapeTransform.Height) * scaleY;
            var geometry = (string)properties?.Element(A + "prstGeom")?.Attribute("prst");
            var shapeStyle = shape.Element(Dsp + "style");
            var fill = FillCss(properties, rels, context, shapeStyle);
            var outline = OutlineCss(properties, context, shapeStyle);
            var style =
                $"left:{left:0.###}px;top:{top:0.###}px;" +
                $"width:{width:0.###}px;height:{height:0.###}px;" +
                fill + outline + GeometryCss(geometry) +
                EffectCss(properties, context) + TransformCss(properties);

            sb.Append($"<div class=\"smartart-shape\" style=\"{style}\">");
            var textBody = shape.Element(Dsp + "txBody");
            var paragraphs = textBody?.Elements(A + "p").ToArray() ?? [];
            if (paragraphs.Length > 0)
            {
                var textTransform = Transform(shape.Element(Dsp + "txXfrm"));
                var hasTextTransform = HasGeometry(textTransform);
                var textLeft = hasTextTransform
                    ? (EmuToPx(textTransform.OffsetX) - EmuToPx(shapeTransform.OffsetX)) * scaleX
                    : 0d;
                var textTop = hasTextTransform
                    ? (EmuToPx(textTransform.OffsetY) - EmuToPx(shapeTransform.OffsetY)) * scaleY
                    : 0d;
                var textWidth = hasTextTransform
                    ? EmuToPx(textTransform.Width) * scaleX
                    : width;
                var textHeight = hasTextTransform
                    ? EmuToPx(textTransform.Height) * scaleY
                    : height;
                var textStyle = MeasuredTextBoxCss(
                    textBody.Element(A + "bodyPr"), textLeft, textTop, textWidth, textHeight);
                sb.Append($"<div class=\"smartart-text\" style=\"{textStyle}\">");
                foreach (var paragraph in paragraphs)
                {
                    var level = int.TryParse(
                        (string)paragraph.Element(A + "pPr")?.Attribute("lvl"),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var paragraphLevel)
                        ? Math.Clamp(paragraphLevel, 0, 8)
                        : 0;
                    var shapeLevel = textBody.Element(A + "lstStyle")
                        ?.Element(A + $"lvl{level + 1}pPr");
                    sb.Append(ParagraphToHtml(context, paragraph, null, shapeLevel));
                }
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }

        return sb.ToString();
    }

    private static string ChartToHtml(RenderContext context, XDocument chart)
    {
        var root = chart?.Root?.Element(C + "chart");
        var plotArea = root?.Element(C + "plotArea");
        var chartNode = plotArea?.Elements().FirstOrDefault(element =>
            element.Name.LocalName.EndsWith("Chart", StringComparison.Ordinal));
        if (chartNode is null)
            return string.Empty;

        var series = new List<ChartSeries>();
        var seriesIndex = 0;
        foreach (var item in chartNode.Elements(C + "ser"))
        {
            var values = ChartValues(item, "val");
            var categories = ChartTextValues(item, "cat");
            if (values.Count == 0)
                continue;

            var name = item.Element(C + "tx")?.Descendants(C + "v")
                .Select(value => value.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var color = ColorOf(
                item.Element(C + "spPr")?.Element(A + "solidFill"), context)
                ?? context.Theme.Color($"accent{seriesIndex % 6 + 1}");
            var showValues = string.Equals(
                (string)item.Element(C + "dLbls")?.Element(C + "showVal")?.Attribute("val"),
                "1",
                StringComparison.Ordinal);
            series.Add(new ChartSeries(
                string.IsNullOrWhiteSpace(name) ? $"Series {seriesIndex + 1}" : name,
                color,
                categories,
                values,
                showValues));
            seriesIndex++;
        }

        if (series.Count == 0)
            return string.Empty;

        var title = root.Element(C + "title")?.Descendants(A + "t")
            .Select(value => value.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var isLine = chartNode.Name.LocalName.Contains("line", StringComparison.OrdinalIgnoreCase);
        var isHorizontal = !isLine &&
            (string)chartNode.Element(C + "barDir")?.Attribute("val") == "bar";
        var hiddenValueAxis = plotArea.Elements(C + "valAx")
            .Any(axis => (string)axis.Element(C + "delete")?.Attribute("val") == "1");
        var legendPosition = (string)root.Element(C + "legend")
            ?.Element(C + "legendPos")?.Attribute("val");
        var legendAtTop = string.Equals(legendPosition, "t", StringComparison.Ordinal);
        var categoryCount = series.Max(item => Math.Max(
            item.Values.Count, item.Categories.Count));
        categoryCount = Math.Max(1, categoryCount);

        const double svgWidth = 960d;
        const double svgHeight = 540d;
        var left = isHorizontal ? 208d : 82d;
        var top = legendAtTop ? 104d : 72d;
        const double right = 30d;
        var bottom = legendAtTop ? 38d : 92d;
        var plotWidth = svgWidth - left - right;
        var plotHeight = svgHeight - top - bottom;
        var allValues = series.SelectMany(item => item.Values).ToArray();
        var maximum = Math.Max(0d, allValues.Max());
        var minimum = Math.Min(0d, allValues.Min());
        if (Math.Abs(maximum - minimum) < 0.0001d)
            maximum = 1d;

        var sb = new StringBuilder(
            "<svg class=\"chart-svg\" viewBox=\"0 0 960 540\" role=\"img\" " +
            "preserveAspectRatio=\"xMidYMid meet\">");
        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append("<text x=\"480\" y=\"32\" text-anchor=\"middle\" " +
                "class=\"chart-title\">")
                .Append(WebUtility.HtmlEncode(title))
                .Append("</text>");
        }

        if (isHorizontal)
        {
            sb.Append($"<line x1=\"{left:0.#}\" y1=\"{top:0.#}\" x2=\"{left:0.#}\" " +
                $"y2=\"{top + plotHeight:0.#}\" class=\"chart-axis-line\"/>");
        }
        else
        {
            // Grid and value labels are intentionally light: they provide orientation
            // without competing with the series colours from the presentation theme.
            if (!hiddenValueAxis)
            {
                for (var tick = 0; tick <= 4; tick++)
                {
                    var ratio = tick / 4d;
                    var value = minimum + (maximum - minimum) * ratio;
                    var y = top + plotHeight * (1d - ratio);
                    sb.Append($"<line x1=\"{left:0.#}\" y1=\"{y:0.#}\" " +
                        $"x2=\"{left + plotWidth:0.#}\" y2=\"{y:0.#}\" class=\"chart-grid\"/>");
                    sb.Append($"<text x=\"{left - 10:0.#}\" y=\"{y + 4:0.#}\" " +
                        $"text-anchor=\"end\" class=\"chart-axis\">{value:0.##}</text>");
                }
            }

            var zeroY = top + plotHeight * (maximum / (maximum - minimum));
            sb.Append($"<line x1=\"{left:0.#}\" y1=\"{zeroY:0.#}\" " +
                $"x2=\"{left + plotWidth:0.#}\" y2=\"{zeroY:0.#}\" class=\"chart-axis-line\"/>");
        }

        if (isHorizontal)
            AppendHorizontalChart(sb, series, categoryCount, left, top, plotWidth, plotHeight, maximum, minimum);
        else if (isLine)
            AppendLineChart(sb, series, categoryCount, left, top, plotWidth, plotHeight, maximum, minimum);
        else
            AppendColumnChart(sb, series, categoryCount, left, top, plotWidth, plotHeight, maximum, minimum);

        var labels = series[0].Categories.Count > 0
            ? series[0].Categories
            : Enumerable.Range(1, categoryCount).Select(index => index.ToString(
                CultureInfo.InvariantCulture)).ToArray();
        for (var category = 0; category < Math.Min(categoryCount, labels.Count); category++)
        {
            if (isHorizontal)
            {
                var y = top + plotHeight * ((category + 0.5d) / categoryCount) + 5d;
                sb.Append($"<text x=\"{left - 18:0.#}\" y=\"{y:0.#}\" " +
                    $"text-anchor=\"end\" class=\"chart-axis\">")
                    .Append(WebUtility.HtmlEncode(Truncate(labels[category], 16)))
                    .Append("</text>");
            }
            else
            {
                var x = left + plotWidth * ((category + 0.5d) / categoryCount);
                sb.Append($"<text x=\"{x:0.#}\" y=\"{top + plotHeight + 24:0.#}\" " +
                    $"text-anchor=\"middle\" class=\"chart-axis\">")
                    .Append(WebUtility.HtmlEncode(Truncate(labels[category], 12)))
                    .Append("</text>");
            }
        }

        AppendChartLegend(sb, series, legendAtTop ? 72d : 516d);
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void AppendColumnChart(
        StringBuilder sb,
        IReadOnlyList<ChartSeries> series,
        int categoryCount,
        double left,
        double top,
        double plotWidth,
        double plotHeight,
        double maximum,
        double minimum)
    {
        var categoryWidth = plotWidth / categoryCount;
        var barWidth = Math.Max(4d, categoryWidth * 0.7d / series.Count);
        var zeroY = top + plotHeight * (maximum / (maximum - minimum));
        for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
        {
            var item = series[seriesIndex];
            for (var valueIndex = 0; valueIndex < item.Values.Count; valueIndex++)
            {
                var value = item.Values[valueIndex];
                var height = Math.Abs(value) / (maximum - minimum) * plotHeight;
                var x = left + valueIndex * categoryWidth +
                    (categoryWidth - barWidth * series.Count) / 2d +
                    seriesIndex * barWidth;
                var y = value >= 0 ? zeroY - height : zeroY;
                sb.Append($"<rect x=\"{x:0.#}\" y=\"{y:0.#}\" width=\"{barWidth * 0.88d:0.#}\" " +
                    $"height=\"{height:0.#}\" rx=\"2\" fill=\"{item.Color}\"/>");
                if (item.ShowValues)
                {
                    var labelY = value >= 0 ? y - 6d : y + height + 16d;
                    sb.Append($"<text x=\"{x + barWidth * 0.44d:0.#}\" y=\"{labelY:0.#}\" " +
                        $"text-anchor=\"middle\" class=\"chart-data\">{value:0.##}</text>");
                }
            }
        }
    }

    private static void AppendLineChart(
        StringBuilder sb,
        IReadOnlyList<ChartSeries> series,
        int categoryCount,
        double left,
        double top,
        double plotWidth,
        double plotHeight,
        double maximum,
        double minimum)
    {
        foreach (var item in series)
        {
            var points = new List<string>();
            for (var index = 0; index < item.Values.Count; index++)
            {
                var x = left + plotWidth * ((index + 0.5d) / categoryCount);
                var y = top + plotHeight * (maximum - item.Values[index]) / (maximum - minimum);
                points.Add($"{x:0.#},{y:0.#}");
            }

            if (points.Count == 0)
                continue;

            sb.Append($"<polyline points=\"{string.Join(" ", points)}\" fill=\"none\" " +
                $"stroke=\"{item.Color}\" stroke-width=\"4\" stroke-linejoin=\"round\" " +
                "stroke-linecap=\"round\"/>");
            foreach (var point in points)
            {
                var pair = point.Split(',');
                sb.Append($"<circle cx=\"{pair[0]}\" cy=\"{pair[1]}\" r=\"5\" " +
                    $"fill=\"#FFFFFF\" stroke=\"{item.Color}\" stroke-width=\"3\"/>");
            }
        }
    }

    private static void AppendHorizontalChart(
        StringBuilder sb,
        IReadOnlyList<ChartSeries> series,
        int categoryCount,
        double left,
        double top,
        double plotWidth,
        double plotHeight,
        double maximum,
        double minimum)
    {
        var categoryHeight = plotHeight / categoryCount;
        var groupHeight = categoryHeight * 0.64d;
        var gap = Math.Min(4d, groupHeight * 0.12d);
        var barHeight = Math.Max(
            3d, (groupHeight - gap * Math.Max(0, series.Count - 1)) / series.Count);
        var zeroX = left + plotWidth * ((0d - minimum) / (maximum - minimum));
        var usableHeight = barHeight * series.Count +
            gap * Math.Max(0, series.Count - 1);
        for (var seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
        {
            var item = series[seriesIndex];
            for (var valueIndex = 0;
                valueIndex < Math.Min(categoryCount, item.Values.Count);
                valueIndex++)
            {
                var value = item.Values[valueIndex];
                var width = Math.Abs(value) / (maximum - minimum) * plotWidth;
                var y = top + valueIndex * categoryHeight +
                    (categoryHeight - usableHeight) / 2d +
                    seriesIndex * (barHeight + gap);
                var x = value >= 0 ? zeroX : zeroX - width;
                sb.Append($"<rect x=\"{x:0.#}\" y=\"{y:0.#}\" width=\"{width:0.#}\" " +
                    $"height=\"{barHeight:0.#}\" rx=\"2\" fill=\"{item.Color}\" " +
                    "data-chart=\"horizontal\"/>");
                if (item.ShowValues)
                {
                    var centerY = y + barHeight / 2d + 5d;
                    var labelX = value >= 0 ? x + width + 6d : x - 6d;
                    var anchor = value >= 0 ? "start" : "end";
                    sb.Append($"<text x=\"{labelX:0.#}\" y=\"{centerY:0.#}\" " +
                        $"text-anchor=\"{anchor}\" class=\"chart-data\">{value:0.##}</text>");
                }
            }
        }
    }

    private static void AppendChartLegend(
        StringBuilder sb, IReadOnlyList<ChartSeries> series, double startY)
    {
        const double itemWidth = 220d;
        var startX = 480d - (series.Count * itemWidth) / 2d + itemWidth / 2d;
        for (var index = 0; index < series.Count; index++)
        {
            var x = startX + index * itemWidth;
            sb.Append($"<circle cx=\"{x - 52:0.#}\" cy=\"{startY - 4:0.#}\" r=\"6\" " +
                $"fill=\"{series[index].Color}\"/>");
            sb.Append($"<text x=\"{x - 40:0.#}\" y=\"{startY:0.#}\" class=\"chart-legend\">")
                .Append(WebUtility.HtmlEncode(Truncate(series[index].Name, 18)))
                .Append("</text>");
        }
    }

    private static IReadOnlyList<double> ChartValues(XElement owner, string elementName) =>
        owner.Element(C + elementName)?.Descendants(C + "pt")
            .Select(point => point.Descendants(C + "v").FirstOrDefault()?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => double.TryParse(
                value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : (double?)null)
            .OfType<double>()
            .ToArray()
        ?? Array.Empty<double>();

    private static IReadOnlyList<string> ChartTextValues(XElement owner, string elementName) =>
        owner.Element(C + elementName)?.Descendants(C + "pt")
            .Select(point => point.Descendants(C + "v").FirstOrDefault()?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray()
        ?? Array.Empty<string>();

    private readonly record struct ChartSeries(
        string Name,
        string Color,
        IReadOnlyList<string> Categories,
        IReadOnlyList<double> Values,
        bool ShowValues);

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

        var crop = PictureCropOf(pic.Element(P + "blipFill"));
        var sourceWidth = Math.Max(
            position.Value.Width / crop.WidthFactor,
            position.Value.Height / crop.HeightFactor);
        var url = ImageUrl(context, rels, ImageTarget(pic), sourceWidth);
        if (url is null)
            return string.Empty;

        var blip = pic.Descendants(A + "blip").FirstOrDefault();
        var frameStyle =
            $"left:{position.Value.Left:0.###}px;top:{position.Value.Top:0.###}px;" +
            $"width:{position.Value.Width:0.###}px;height:{position.Value.Height:0.###}px;" +
            TransformCss(pic.Element(P + "spPr"));
        var imageStyle =
            $"left:{-position.Value.Width * crop.Left / crop.WidthFactor:0.###}px;" +
            $"top:{-position.Value.Height * crop.Top / crop.HeightFactor:0.###}px;" +
            $"width:{position.Value.Width / crop.WidthFactor:0.###}px;" +
            $"height:{position.Value.Height / crop.HeightFactor:0.###}px;";
        var opacity = PictureOpacity(blip);
        var filter = PictureFilter(blip);
        var duotone = PictureDuotone(blip, context);
        if (duotone is { } colors)
        {
            var id = $"pic-paint-{Interlocked.Increment(ref _svgPaintId)}";
            var alpha = opacity < 0.999d ? $" opacity=\"{opacity:0.###}\"" : string.Empty;
            return $"<div class=\"pic pic-frame\" style=\"{frameStyle}\">" +
                $"<svg class=\"pic-image\" style=\"{imageStyle}\" " +
                "width=\"100%\" height=\"100%\" viewBox=\"0 0 100 100\" " +
                "preserveAspectRatio=\"none\" aria-hidden=\"true\">" +
                $"<defs><filter id=\"{id}\" color-interpolation-filters=\"sRGB\">" +
                "<feColorMatrix type=\"saturate\" values=\"0\"/>" +
                "<feComponentTransfer>" +
                $"<feFuncR type=\"table\" tableValues=\"{colors.Start.R:0.###} {colors.End.R:0.###}\"/>" +
                $"<feFuncG type=\"table\" tableValues=\"{colors.Start.G:0.###} {colors.End.G:0.###}\"/>" +
                $"<feFuncB type=\"table\" tableValues=\"{colors.Start.B:0.###} {colors.End.B:0.###}\"/>" +
                "</feComponentTransfer></filter></defs>" +
                $"<image href=\"{WebUtility.HtmlEncode(url)}\" width=\"100\" height=\"100\" " +
                $"preserveAspectRatio=\"none\" filter=\"url(#{id})\"{alpha}/>" +
                "</svg></div>";
        }

        if (opacity < 0.999d)
            imageStyle += $"opacity:{opacity:0.###};";
        if (filter.Length > 0)
            imageStyle += filter;
        return $"<div class=\"pic pic-frame\" style=\"{frameStyle}\">" +
            $"<img class=\"pic-image\" src=\"{url}\" style=\"{imageStyle}\"></div>";
    }

    private static ImageCrop PictureCropOf(XElement fill)
    {
        var source = fill?.Element(A + "srcRect");
        if (source is null)
            return ImageCrop.None;

        return new ImageCrop(
            CropFraction(source, "l"),
            CropFraction(source, "t"),
            CropFraction(source, "r"),
            CropFraction(source, "b"));
    }

    private static double CropFraction(XElement source, string attribute) =>
        Math.Clamp(((double?)source.Attribute(attribute) ?? 0d) / 100000d, -1d, 1d);

    private static double PictureOpacity(XElement blip)
    {
        var amount = (double?)blip?.Element(A + "alphaModFix")?.Attribute("amt");
        return amount is null ? 1d : Math.Clamp(amount.Value / 100000d, 0d, 1d);
    }

    private static string PictureFilter(XElement blip)
    {
        var lum = blip?.Element(A + "lum");
        if (lum is null)
            return string.Empty;

        var brightness = 1d + Math.Clamp(
            ((double?)lum.Attribute("bright") ?? 0d) / 100000d, -1d, 2d);
        var contrast = 1d + Math.Clamp(
            ((double?)lum.Attribute("contrast") ?? 0d) / 100000d, -1d, 1d);
        return $"filter:brightness({brightness:0.###}) contrast({contrast:0.###});";
    }

    private static ((double R, double G, double B) Start, (double R, double G, double B) End)?
        PictureDuotone(XElement blip, RenderContext context)
    {
        var duotone = blip?.Element(A + "duotone");
        var colors = duotone?.Elements()
            .Select(color => ColorNodeValue(color, context))
            .Where(color => color is not null)
            .Take(2)
            .ToArray();
        if (colors?.Length != 2)
            return null;

        return (Rgb(colors[0]), Rgb(colors[1]));
    }

    private static string ColorNodeValue(XElement color, RenderContext context)
    {
        if (color is null)
            return null;
        if (color.Name == A + "srgbClr")
            return Transformed("#" + Normalize((string)color.Attribute("val")), color, context);
        if (color.Name == A + "schemeClr")
            return Transformed(
                context.Theme.Color((string)color.Attribute("val")), color, context);
        if (color.Name == A + "sysClr")
            return Transformed(
                "#" + Normalize((string)color.Attribute("lastClr")), color, context);
        if (color.Name == A + "prstClr")
            return PresetColors.GetValueOrDefault(
                ((string)color.Attribute("val"))?.Trim());
        return null;
    }

    private static (double R, double G, double B) Rgb(string color)
    {
        try
        {
            var value = (Color)ColorConverter.ConvertFromString(color);
            return (value.R / 255d, value.G / 255d, value.B / 255d);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            return (0d, 0d, 0d);
        }
    }

    private static string ImageTarget(XElement element) =>
        (string)element.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");

    private static string ImageUrl(RenderContext context, Dictionary<string, string> rels, string embed, double widthPx)
    {
        if (embed is null || string.IsNullOrEmpty(context.ImageFolder) ||
            context.ImageCount >= MaxImages || !rels.TryGetValue(embed, out var entry))
            return null;

        var mediaPath = Part(entry).TrimStart('/');
        if (context.Cached(mediaPath) is { } cached)
            return cached;

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
        context.Cache(mediaPath, url);
        return url;
    }

    private static string ParagraphToHtml(
        RenderContext context,
        XElement para,
        XElement inheritedLevel = null,
        XElement shapeLevel = null)
    {
        var sb = new StringBuilder();
        var pPr = para.Element(A + "pPr");
        var inheritedDefault = inheritedLevel?.Element(A + "defRPr");
        var shapeDefault = shapeLevel?.Element(A + "defRPr");
        var paragraphDefault = pPr?.Element(A + "defRPr");
        var align = FirstValue(pPr, "algn", shapeLevel, inheritedLevel) switch
        {
            "ctr" => "center",
            "r" => "right",
            "just" => "justify",
            _ => "left",
        };

        foreach (var el in para.Elements())
        {
            if (el.Name == A + "br")
            {
                sb.Append("<br>");
                continue;
            }

            if (el.Name == A + "tab")
            {
                sb.Append("<span class=\"tab\"></span>");
                continue;
            }

            if (el.Name != A + "r" && el.Name != A + "fld")
                continue;

            var text = new StringBuilder();
            foreach (var t in el.Elements(A + "t"))
                text.Append(WebUtility.HtmlEncode(t.Value));

            if (text.Length == 0)
                continue;

            var rPr = el.Element(A + "rPr");
            var css = new List<string>();
            if (FirstValue(rPr, "b", paragraphDefault, shapeDefault, inheritedDefault) == "1")
                css.Add("font-weight:bold");
            if (FirstValue(rPr, "i", paragraphDefault, shapeDefault, inheritedDefault) == "1")
                css.Add("font-style:italic");
            if (FirstValue(rPr, "u", paragraphDefault, shapeDefault, inheritedDefault) is "1" or "sng")
                css.Add("text-decoration:underline");
            var sz = FirstValue(rPr, "sz", paragraphDefault, shapeDefault, inheritedDefault);
            if (double.TryParse(sz, NumberStyles.Float, CultureInfo.InvariantCulture, out var hundredths) &&
                hundredths > 0)
                css.Add($"font-size:{hundredths / 100d:0.#}pt");
            var gradient = rPr?.Element(A + "gradFill")
                ?? paragraphDefault?.Element(A + "gradFill")
                ?? shapeDefault?.Element(A + "gradFill")
                ?? inheritedDefault?.Element(A + "gradFill");
            if (gradient is not null && GradientCss(gradient, context) is { Length: > 0 } gradientCss)
            {
                css.Add(gradientCss.TrimEnd(';'));
                css.Add("display:inline-block");
                css.Add("background-repeat:no-repeat");
                css.Add("-webkit-background-clip:text");
                css.Add("background-clip:text");
                css.Add("-webkit-text-fill-color:transparent");
                css.Add("color:transparent");
            }
            else
            {
                var color = ColorOf(rPr?.Element(A + "solidFill"), context)
                    ?? ColorOf(paragraphDefault?.Element(A + "solidFill"), context)
                    ?? ColorOf(shapeDefault?.Element(A + "solidFill"), context)
                    ?? ColorOf(inheritedDefault?.Element(A + "solidFill"), context);
                if (color is not null)
                    css.Add($"color:{color}");
            }
            var face = rPr?.Element(A + "latin")?.Attribute("typeface")?.Value
                ?? paragraphDefault?.Element(A + "latin")?.Attribute("typeface")?.Value
                ?? shapeDefault?.Element(A + "latin")?.Attribute("typeface")?.Value
                ?? inheritedDefault?.Element(A + "latin")?.Attribute("typeface")?.Value;
            face ??= rPr?.Element(A + "ea")?.Attribute("typeface")?.Value
                ?? paragraphDefault?.Element(A + "ea")?.Attribute("typeface")?.Value
                ?? shapeDefault?.Element(A + "ea")?.Attribute("typeface")?.Value
                ?? inheritedDefault?.Element(A + "ea")?.Attribute("typeface")?.Value;
            if (!string.IsNullOrEmpty(face) && !face.StartsWith('+'))
                css.Add($"font-family:'{WebUtility.HtmlEncode(face)}'");
            var spacing = FirstValue(rPr, "spc", paragraphDefault, shapeDefault, inheritedDefault);
            if (double.TryParse(spacing, NumberStyles.Float, CultureInfo.InvariantCulture, out var spacingValue))
                css.Add($"letter-spacing:{spacingValue / 100d:0.##}pt");

            var style = css.Count > 0 ? $" style=\"{string.Join(";", css)}\"" : string.Empty;
            sb.Append("<span").Append(style).Append('>').Append(text).Append("</span>");
        }

        if (sb.Length == 0)
            return string.Empty;

        var paragraphStyle = new StringBuilder($"text-align:{align};");
        var lineSpacing = Percentage(
            (string)FirstElement(
                A + "lnSpc", pPr, shapeLevel, inheritedLevel)
                ?.Element(A + "spcPct")?.Attribute("val"));
        if (lineSpacing is not null)
            paragraphStyle.Append($"line-height:{lineSpacing.Value * 100d:0.##}%;");

        var spaceBefore = (double?)FirstElement(
                A + "spcBef", pPr, shapeLevel, inheritedLevel)
            ?.Element(A + "spcPts")?.Attribute("val");
        if (spaceBefore is not null)
            paragraphStyle.Append($"margin-top:{spaceBefore.Value / 100d:0.##}pt;");

        var spaceAfter = (double?)FirstElement(
                A + "spcAft", pPr, shapeLevel, inheritedLevel)
            ?.Element(A + "spcPts")?.Attribute("val");
        if (spaceAfter is not null)
            paragraphStyle.Append($"margin-bottom:{spaceAfter.Value / 100d:0.##}pt;");

        if (double.TryParse(
                FirstValue(pPr, "marL", shapeLevel, inheritedLevel),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var leftMargin))
            paragraphStyle.Append($"margin-left:{EmuToPx(leftMargin):0.#}px;");
        if (double.TryParse(
                FirstValue(pPr, "indent", shapeLevel, inheritedLevel),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var indent))
            paragraphStyle.Append($"text-indent:{EmuToPx(indent):0.#}px;");

        var bulletOwner = FirstBulletOwner(pPr, shapeLevel, inheritedLevel);
        var bullet = (string)bulletOwner?.Element(A + "buChar")?.Attribute("char");
        if (string.IsNullOrEmpty(bullet) &&
            bulletOwner?.Element(A + "buAutoNum") is not null)
            bullet = "•";
        if (bulletOwner?.Element(A + "buNone") is not null)
            bullet = null;
        var marker = string.IsNullOrEmpty(bullet)
            ? string.Empty
            : $"<span class=\"bullet\">{WebUtility.HtmlEncode(bullet)}</span>";

        return $"<p style=\"{paragraphStyle}\">{marker}{sb}</p>";
    }

    private static XElement FirstElement(XName name, params XElement[] owners) =>
        owners.FirstOrDefault(owner => owner?.Element(name) is not null)?.Element(name);

    private static XElement FirstBulletOwner(params XElement[] owners) =>
        owners.FirstOrDefault(owner =>
            owner?.Element(A + "buNone") is not null ||
            owner?.Element(A + "buChar") is not null ||
            owner?.Element(A + "buAutoNum") is not null ||
            owner?.Element(A + "buBlip") is not null);

    private static string FirstValue(
        XElement first, XName attribute, params XElement[] fallbacks)
    {
        var value = (string)first?.Attribute(attribute);
        if (!string.IsNullOrEmpty(value))
            return value;

        foreach (var fallback in fallbacks)
        {
            value = (string)fallback?.Attribute(attribute);
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// v5.7.0: a fill as CSS - an explicit colour, a theme colour (with its luminance/shade/tint/alpha
    /// transforms) or a picture fill. Templates colour almost everything through the theme, which is why the
    /// slides used to come out with wrong or invisible colours.
    /// </summary>
    private static string FillCss(
        XElement properties,
        Dictionary<string, string> rels,
        RenderContext context,
        XElement shapeStyle = null,
        XElement inheritedProperties = null)
    {
        if (properties is null)
            return FillCss(inheritedProperties, rels, context, shapeStyle);

        var solid = properties.Element(A + "solidFill");
        if (solid is not null)
            return ColorOf(solid, context) is { } color ? $"background:{color};" : string.Empty;

        var blip = properties.Element(A + "blipFill");
        if (blip is not null)
            return PictureFillCss(blip, rels, context);

        var gradient = properties.Element(A + "gradFill");
        if (gradient is not null)
            return GradientCss(gradient, context);

        if (properties.Element(A + "noFill") is not null)
            return "background:transparent;";

        if (inheritedProperties is not null)
            return FillCss(inheritedProperties, rels, context, shapeStyle);

        var fillReference = shapeStyle?.Element(A + "fillRef");
        return ColorOf(fillReference, context) is { } referenced
            ? $"background:{referenced};"
            : string.Empty;
    }

    private static string PictureFillCss(
        XElement fill, Dictionary<string, string> rels, RenderContext context)
    {
        var url = ImageUrl(context, rels, ImageTarget(fill), 0);
        if (url is null)
            return string.Empty;

        var tile = fill.Element(A + "tile");
        if (tile is not null)
        {
            var sizeX = Percentage((string)tile.Attribute("sx"));
            var sizeY = Percentage((string)tile.Attribute("sy"));
            var size = sizeX is not null && sizeY is not null
                ? $"background-size:{Math.Max(1d, sizeX.Value * 100d):0.##}% " +
                    $"{Math.Max(1d, sizeY.Value * 100d):0.##}%;"
                : "background-size:auto;";
            return $"background-image:url('{url}');{size}" +
                "background-repeat:repeat;background-position:0 0;";
        }

        var crop = PictureCropOf(fill);
        var horizontalCrop = crop.Left + crop.Right;
        var verticalCrop = crop.Top + crop.Bottom;
        var xPosition = Math.Abs(horizontalCrop) < 0.00001d
            ? 0d
            : crop.Left / horizontalCrop * 100d;
        var yPosition = Math.Abs(verticalCrop) < 0.00001d
            ? 0d
            : crop.Top / verticalCrop * 100d;
        return $"background-image:url('{url}');" +
            $"background-size:{100d / crop.WidthFactor:0.###}% " +
            $"{100d / crop.HeightFactor:0.###}%;" +
            $"background-position:{xPosition:0.###}% {yPosition:0.###}%;" +
            "background-repeat:no-repeat;";
    }

    private static string ShapeFillValue(
        XElement properties,
        RenderContext context,
        XElement shapeStyle,
        XElement inheritedProperties)
    {
        if (properties?.Element(A + "solidFill") is { } solid &&
            ColorOf(solid, context) is { } color)
            return color;
        if (properties?.Element(A + "gradFill") is { } gradient)
        {
            var stop = gradient.Descendants(A + "gs")
                .Select(item => ColorOf(item.Element(A + "solidFill"), context) ?? ColorOf(item, context))
                .FirstOrDefault(value => value is not null);
            if (stop is not null)
                return stop;
        }
        if (properties?.Element(A + "noFill") is not null)
            return "none";
        if (inheritedProperties is not null)
            return ShapeFillValue(inheritedProperties, context, shapeStyle, null);
        return ColorOf(shapeStyle?.Element(A + "fillRef"), context) ?? "none";
    }

    private static string GradientCss(XElement gradient, RenderContext context)
    {
        var gradientStops = GradientStops(gradient, context);
        if (gradientStops.Count < 2)
            return string.Empty;

        var stops = gradientStops
            .Select(stop => $"{stop.Color} {stop.Position * 100d:0.##}%")
            .ToArray();
        if (gradient.Element(A + "path") is not null)
            return $"background:radial-gradient(circle at center,{string.Join(",", stops)});";

        var cssAngle = GradientAngle(gradient) + 90d;
        return $"background:linear-gradient({cssAngle:0.##}deg,{string.Join(",", stops)});";
    }

    private static List<GradientStop> GradientStops(XElement gradient, RenderContext context) =>
        gradient.Element(A + "gsLst")?.Elements(A + "gs")
            .Select(stop =>
            {
                var color = ColorOf(stop.Element(A + "solidFill"), context) ??
                    ColorOf(stop, context);
                var position = Percentage((string)stop.Attribute("pos")) ?? 0d;
                return color is null
                    ? (GradientStop?)null
                    : new GradientStop(position, color);
            })
            .OfType<GradientStop>()
            .OrderBy(stop => stop.Position)
            .ToList()
        ?? [];

    private static double GradientAngle(XElement gradient) =>
        ((double?)gradient.Element(A + "lin")?.Attribute("ang") ?? 0d) / 60000d;

    private static string OutlineCss(
        XElement properties,
        RenderContext context,
        XElement shapeStyle = null,
        XElement inheritedProperties = null)
    {
        var line = ReadLine(properties, context, shapeStyle, inheritedProperties);
        if (line is null)
            return string.Empty;

        var style = line.Value.Dash.Length > 0 ? "dashed" : "solid";
        var color = line.Value.Gradient?.EndColor ?? line.Value.Color;
        return $"border:{line.Value.Width:0.##}px {style} {color};";
    }

    private static LinePaint? ReadLine(
        XElement properties,
        RenderContext context,
        XElement shapeStyle = null,
        XElement inheritedProperties = null)
    {
        var line = properties?.Element(A + "ln");
        if (line is not null && line.Element(A + "noFill") is not null)
            return null;

        if (line is null && inheritedProperties is not null)
            return ReadLine(inheritedProperties, context, shapeStyle);

        string color = null;
        LineGradient? gradient = null;
        if (line is not null)
        {
            if (line.Element(A + "gradFill") is { } lineGradient)
            {
                var stops = GradientStops(lineGradient, context);
                if (stops.Count > 0)
                {
                    color = stops[0].Color;
                    gradient = new LineGradient(
                        stops[0].Color,
                        stops[^1].Color,
                        GradientAngle(lineGradient));
                }
            }

            color ??= ColorOf(line.Element(A + "solidFill"), context);
        }

        color ??= ColorOf(shapeStyle?.Element(A + "lnRef"), context);
        if (line is null && color is null)
            return null;

        var width = (double?)line?.Attribute("w") ?? 12700d;
        var dash = (string)line?.Element(A + "prstDash")?.Attribute("val");
        var dashPattern = dash switch
        {
            "dash" => "7 5",
            "sysDash" => "4 3",
            "dot" or "sysDot" => "2 3",
            "lgDash" => "12 5",
            "dashDot" or "sysDashDot" => "7 4 2 4",
            _ => string.Empty,
        };

        return new LinePaint(
            color ?? "#000000",
            Math.Max(0.25d, width / 9525d),
            dashPattern,
            gradient);
    }

    private static string GeometryCss(string geometry) => geometry switch
    {
        "ellipse" => "border-radius:50%;",
        "roundRect" or "round2SameRect" => "border-radius:12px;",
        "trapezoid" => "clip-path:polygon(18% 0,82% 0,100% 100%,0 100%);",
        "chevron" => "clip-path:polygon(0 0,78% 0,100% 50%,78% 100%,0 100%,22% 50%);",
        "homePlate" => "clip-path:polygon(0 0,78% 0,100% 50%,78% 100%,0 100%);",
        "triangle" => "clip-path:polygon(50% 0,100% 100%,0 100%);",
        "diamond" => "clip-path:polygon(50% 0,100% 50%,50% 100%,0 50%);",
        _ => string.Empty,
    };

    private static bool HasFill(XElement properties) =>
        properties?.Elements().Any(element =>
            element.Name == A + "solidFill" ||
            element.Name == A + "gradFill" ||
            element.Name == A + "blipFill" ||
            element.Name == A + "noFill") == true;

    private static bool HasEffects(XElement properties) =>
        properties?.Element(A + "effectLst")?.HasElements == true;

    private static string EffectCss(XElement properties, RenderContext context)
    {
        var shadow = properties?.Element(A + "effectLst")?.Element(A + "outerShdw");
        if (shadow is null)
            return string.Empty;

        var color = ColorOf(shadow.Element(A + "solidFill"), context) ?? "#000000";
        var distance = EmuToPx((double?)shadow.Attribute("dist") ?? 0d);
        var direction = ((double?)shadow.Attribute("dir") ?? 0d) / 60000d * Math.PI / 180d;
        var blur = EmuToPx((double?)shadow.Attribute("blurRad") ?? 0d);
        var x = Math.Cos(direction) * distance;
        var y = Math.Sin(direction) * distance;
        return $"box-shadow:{x:0.#}px {y:0.#}px {blur:0.#}px {color};";
    }

    private static string TextBodyCss(XElement body)
    {
        var left = EmuToPx((double?)body?.Attribute("lIns") ?? 91440d);
        var right = EmuToPx((double?)body?.Attribute("rIns") ?? 91440d);
        var top = EmuToPx((double?)body?.Attribute("tIns") ?? 45720d);
        var bottom = EmuToPx((double?)body?.Attribute("bIns") ?? 45720d);
        var align = (string)body?.Attribute("anchor") switch
        {
            "ctr" => "center",
            "b" => "flex-end",
            _ => "flex-start",
        };
        return $"display:flex;flex-direction:column;justify-content:{align};" +
            $"padding:{top:0.#}px {right:0.#}px {bottom:0.#}px {left:0.#}px;" +
            "width:100%;height:100%;box-sizing:border-box;overflow:hidden;";
    }

    private static string MeasuredTextBoxCss(
        XElement body, double left, double top, double width, double height)
    {
        var measuredWidth = Math.Max(1d, width);
        var measuredHeight = Math.Max(1d, height);
        // TextBodyCss establishes padding, alignment and clipping. Its 100% size
        // is appropriate for ordinary shapes, but SmartArt supplies a separate
        // txXfrm in the drawing coordinate space, so restore that box afterwards.
        return $"left:{left:0.###}px;top:{top:0.###}px;" +
            $"width:{measuredWidth:0.###}px;height:{measuredHeight:0.###}px;" +
            $"{TextBodyCss(body)}width:{measuredWidth:0.###}px;" +
            $"height:{measuredHeight:0.###}px;";
    }

    private static string TransformCss(XElement properties)
    {
        var transform = properties?.Element(A + "xfrm");
        if (transform is null)
            return string.Empty;

        var rotation = ((double?)transform.Attribute("rot") ?? 0d) / 60000d;
        var flipX = (string)transform.Attribute("flipH") == "1";
        var flipY = (string)transform.Attribute("flipV") == "1";
        if (Math.Abs(rotation) < 0.01d && !flipX && !flipY)
            return string.Empty;

        return $"transform:rotate({rotation:0.##}deg)" +
            (flipX ? " scaleX(-1)" : string.Empty) +
            (flipY ? " scaleY(-1)" : string.Empty) +
            ";transform-origin:center;";
    }

    private readonly record struct LinePaint(
        string Color, double Width, string Dash, LineGradient? Gradient);

    private readonly record struct SvgLinePaint(
        string Defs, string Stroke, string Attributes);

    private readonly record struct ImageCrop(
        double Left, double Top, double Right, double Bottom)
    {
        internal static readonly ImageCrop None = new(0d, 0d, 0d, 0d);

        internal double WidthFactor => Math.Max(0.01d, 1d - Left - Right);

        internal double HeightFactor => Math.Max(0.01d, 1d - Top - Bottom);
    }

    private readonly record struct LineGradient(
        string StartColor, string EndColor, double Angle);

    private readonly record struct GradientStop(double Position, string Color);

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
            return PresetColors.GetValueOrDefault(
                ((string)preset.Attribute("val"))?.Trim());

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
                var picture = PictureFillCss(blip, rels, context);
                if (picture.Length > 0)
                    return picture.TrimEnd(';');
            }

            var gradient = fill.Element(A + "gradFill");
            if (gradient is not null)
            {
                var gradientCss = GradientCss(gradient, context);
                if (gradientCss.Length > 0)
                    return gradientCss.TrimEnd(';');
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
        // Shape properties carry <a:xfrm> as a child. A graphic frame carries
        // that xfrm directly, and SmartArt text carries <dsp:txXfrm>. Treat all
        // three as coordinate-bearing elements instead of returning no geometry.
        var localName = properties?.Name.LocalName;
        var xfrm = localName is "xfrm" or "txXfrm"
            ? properties
            : properties?.Element(A + "xfrm");
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

        var sourcePath = SourcePathFor(entryName);
        using var stream = entry.Open();
        var rels = XDocument.Load(stream);
        foreach (var rel in rels.Root?.Elements(Rel + "Relationship") ?? [])
        {
            var id = (string)rel.Attribute("Id");
            var target = (string)rel.Attribute("Target");
            var type = (string)rel.Attribute("Type");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target))
                continue;

            target = ResolvePartPath(sourcePath, target);

            // The layout/master lookups key on the relationship *type*, so it travels with the target.
            map[id] = type is null ? target : $"{target}\u0000{type.Split('/')[^1]}";
        }

        return map;
    }

    /// <summary>
    /// OPC relationships store targets relative to the part that owns the .rels
    /// file. Resolve them once so every caller can address the package entry
    /// directly, regardless of whether the target starts with "/", "../" or a
    /// sibling folder.
    /// </summary>
    private static string ResolvePartPath(string sourcePath, string target)
    {
        if (string.IsNullOrEmpty(target) ||
            target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return target;

        var combined = target.StartsWith('/')
            ? target.TrimStart('/')
            : $"{(Path.GetDirectoryName(sourcePath) ?? string.Empty).Replace('\\', '/')}/{target}";
        var parts = new List<string>();
        foreach (var segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return string.Join("/", parts);
    }

    private static string SourcePathFor(string relsPath)
    {
        var marker = relsPath.LastIndexOf("/_rels/", StringComparison.Ordinal);
        if (marker < 0)
            return relsPath;

        var file = Path.GetFileName(relsPath);
        return file.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
            ? $"{relsPath[..marker]}/{file[..^5]}"
            : relsPath;
    }

    private static string ErrorHtml(string message) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>body{margin:0;color:#C42B1C;" +
        "background:#FFFFFF;font-family:'Segoe UI',sans-serif;padding:24px;font-size:14px}" +
        "</style></head><body><div>" + message + "</div></body></html>";

    private static void AppendCss(StringBuilder sb)
    {
        // v3.17.0: fixed light canvas, independent of the app theme.
        // v6.3.1: every slide is part of one continuous vertical deck, and the
        // wrapper is sized by script while the original slide coordinate space is
        // transformed to the available width.
        sb.Append("html{scroll-behavior:smooth}body{margin:0;background:#F3F3F3;min-height:100%;" +
            "font-family:'Segoe UI',Helvetica,Arial,sans-serif;color:#1A1A1A}");
        sb.Append(".deck{display:flex;flex-direction:column;align-items:center;gap:24px;" +
            "padding:24px;box-sizing:border-box;min-height:100%;width:100%}");
        sb.Append("::-webkit-scrollbar{width:8px;height:8px}");
        sb.Append("::-webkit-scrollbar-thumb{background:rgba(0,0,0,.28);border-radius:4px}");
        sb.Append(".slide-frame{position:relative;flex:0 0 auto;overflow:hidden;" +
            "box-shadow:0 2px 12px rgba(0,0,0,.18);background:#FFFFFF}");
        sb.Append(".slide{position:absolute;left:0;top:0;transform-origin:top left;overflow:hidden}");
        sb.Append(".slide-no{position:absolute;right:10px;bottom:6px;font-size:11px;color:rgba(0,0,0,.35)}");
        sb.Append(".group{position:absolute;overflow:visible;transform-origin:center}");
        sb.Append(".shape{position:absolute;overflow:hidden;box-sizing:border-box}");
        sb.Append(".connector{position:absolute;overflow:visible;transform-origin:center}");
        sb.Append(".smartart-shape{position:absolute;overflow:hidden;box-sizing:border-box}");
        sb.Append(".smartart-text{position:absolute;box-sizing:border-box;overflow:hidden}");
        sb.Append(".shape-text{position:relative;z-index:0}");
        sb.Append(".shape p{margin:0}");
        sb.Append(".shape span{white-space:pre-wrap}");
        sb.Append(".tab{display:inline-block;width:4em}");
        sb.Append(".bullet{display:inline-block;min-width:1.25em}");
        sb.Append(".pic{position:absolute;object-fit:fill}");
        sb.Append(".pic-frame{overflow:hidden}");
        sb.Append(".pic-image{position:absolute;display:block;object-fit:fill}");
        sb.Append(".pic svg{display:block}");
        sb.Append(".placeholder-shape{border:1px dashed rgba(0,0,0,.3);display:flex;align-items:center;" +
            "justify-content:center;color:rgba(0,0,0,.45);font-size:12px;box-sizing:border-box}");
        sb.Append(".chart-svg{display:block;width:100%;height:100%}");
        sb.Append(".chart-grid{stroke:rgba(0,0,0,.10);stroke-width:1}");
        sb.Append(".chart-axis-line{stroke:rgba(0,0,0,.42);stroke-width:1.5}");
        sb.Append(".chart-axis{fill:#5F6368;font-size:15px;font-family:'Segoe UI',Arial,sans-serif}");
        sb.Append(".chart-data{fill:#4B4B4B;font-size:14px;font-family:'Segoe UI',Arial,sans-serif}");
        sb.Append(".chart-title{fill:#242424;font-size:24px;font-weight:600;" +
            "font-family:'Segoe UI',Arial,sans-serif}");
        sb.Append(".chart-legend{fill:#444444;font-size:15px;" +
            "font-family:'Segoe UI',Arial,sans-serif}");
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
    /// v5.7.0: a shape's box in slide pixels. Groups carry their own placement and their own coordinate
    /// space, so a child inside a group has to be mapped through it before it means anything on the slide.
    /// </summary>
    private readonly record struct Xform(
        double ScaleX, double ScaleY, double OffsetX, double OffsetY, double Width, double Height)
    {
        internal static readonly Xform Identity = new(1, 1, 0, 0, 0, 0);

        internal static readonly Xform Empty = new(0, 0, 0, 0, 0, 0);

        internal (double Left, double Top, double Width, double Height)? Map(
            Xform child, bool allowZeroDimension = false)
        {
            if (child.ScaleX == 0 || child.ScaleY == 0 ||
                child.Width < 0 || child.Height < 0 ||
                (!allowZeroDimension && (child.Width <= 0 || child.Height <= 0)) ||
                (allowZeroDimension && child.Width <= 0 && child.Height <= 0))
                return null;

            return (
                EmuToPx(OffsetX + child.OffsetX * ScaleX),
                EmuToPx(OffsetY + child.OffsetY * ScaleY),
                EmuToPx(child.Width * ScaleX),
                EmuToPx(child.Height * ScaleY));
        }

        /// <summary>
        /// Maps a group's child coordinate space into the group's local CSS box.
        /// The group box itself carries rotation and mirroring, so children remain
        /// axis-aligned inside it and inherited group transforms nest naturally.
        /// </summary>
        internal Xform ForGroupChildren(XElement groupProperties)
        {
            var xfrm = groupProperties?.Element(A + "xfrm");
            var ext = xfrm?.Element(A + "ext");
            var chOff = xfrm?.Element(A + "chOff");
            var chExt = xfrm?.Element(A + "chExt");
            if (ext is null || chOff is null || chExt is null)
                return this;

            var chW = (double?)chExt.Attribute("cx") ?? 0d;
            var chH = (double?)chExt.Attribute("cy") ?? 0d;
            if (chW <= 0 || chH <= 0)
                return this;

            var sx = ((double?)ext.Attribute("cx") ?? 0d) / chW;
            var sy = ((double?)ext.Attribute("cy") ?? 0d) / chH;
            var cox = (double?)chOff.Attribute("x") ?? 0d;
            var coy = (double?)chOff.Attribute("y") ?? 0d;

            var childScaleX = ScaleX * sx;
            var childScaleY = ScaleY * sy;
            return new Xform(
                childScaleX,
                childScaleY,
                -cox * childScaleX,
                -coy * childScaleY,
                Width, Height);
        }
    }
}
