// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Plugin.OfficeViewer;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace QuickLook.Tests;

/// <summary>
/// PowerPoint placeholders normally omit their own xfrm: the visible position is
/// inherited through the slide layout and master. Relationship targets are also
/// relative to the part that owns the .rels file, including pictures.
/// </summary>
internal class PptxToHtmlTests : SettingsFixture
{
    public void PlaceholderGeometryAndPictureRelationshipsSurviveConversion()
    {
        var path = Path.Combine(Root, "inherited-placeholder.pptx");
        WritePresentation(path);
        var imageFolder = OfficeImageStore.CreateFolder();

        try
        {
            var content = PptxToHtml.Convert(path, imageFolder, "test.local");
            Assert.True(
                content.Html.Contains(">Inherited title<", StringComparison.Ordinal),
                "text from a placeholder without its own geometry is rendered");
            Assert.True(
                content.Html.Contains(
                    "left:96px;top:48px;width:384px;height:192px",
                    StringComparison.Ordinal),
                "the placeholder uses the layout position");
            Assert.True(
                content.Html.Contains(">Master positioned body<", StringComparison.Ordinal),
                "a layout placeholder without geometry falls through to the master");
            Assert.True(
                content.Html.Contains(
                    "left:48px;top:240px;width:576px;height:192px",
                    StringComparison.Ordinal),
                "the body uses the master position when the layout has none");
            Assert.True(
                content.Html.Contains("font-size:30pt", StringComparison.Ordinal) &&
                content.Html.Contains("font-weight:bold", StringComparison.Ordinal) &&
                content.Html.Contains("color:#C00000", StringComparison.Ordinal) &&
                content.Html.Contains("text-align:center", StringComparison.Ordinal),
                "the title inherits its layout text style");
            Assert.True(
                content.Html.Contains("font-size:18pt", StringComparison.Ordinal),
                "the body inherits the master text style");
            Assert.True(
                content.Html.Contains("font-size:42pt", StringComparison.Ordinal) &&
                content.Html.Contains("color:#FFFFFF", StringComparison.Ordinal),
                "a shape's own list style overrides inherited font size and colour");
            Assert.True(
                content.Html.Contains("background:#EEF2F8", StringComparison.Ordinal) &&
                !content.Html.Contains("Layout prompt", StringComparison.Ordinal),
                "layout placeholder decoration is kept while its prompt text stays hidden");
            Assert.True(
                content.Html.Contains("class=\"pic pic-frame\"", StringComparison.Ordinal),
                "the slide picture is rendered through its relationship");
            Assert.True(
                content.Html.Contains("<image href=", StringComparison.Ordinal),
                "a duotone picture is rendered through an SVG colour filter");
            Assert.True(
                content.Html.Contains("<feComponentTransfer>", StringComparison.Ordinal),
                "the duotone filter maps image luminance between both colours");
            Assert.True(
                content.Html.Contains("opacity=\"0.42\"", StringComparison.Ordinal),
                "alphaModFix is preserved on the rendered picture");
            Assert.True(
                content.Html.Contains("class=\"pic pic-frame\"", StringComparison.Ordinal) &&
                content.Html.Contains(
                    "left:-32px;top:-96px;width:320px;height:480px;",
                    StringComparison.Ordinal),
                "srcRect crops a picture without stretching the cropped image into its frame");
            Assert.True(
                content.Html.Contains(
                    "background-size:200% 100%;background-position:50% 0%;",
                    StringComparison.Ordinal),
                "a cropped picture fill keeps its source rectangle");
            Assert.Equal(
                2, Directory.GetFiles(imageFolder).Length,
                "each slide resolves its own rId1 image instead of reusing another slide's picture");
            Assert.True(
                content.Html.Contains(">Second title<", StringComparison.Ordinal),
                "the second slide is converted through its own related parts");
            Assert.True(
                content.Html.Contains("class=\"slide-frame\" id=\"slide-1\"", StringComparison.Ordinal) &&
                content.Html.Contains("class=\"slide-frame\" id=\"slide-2\"", StringComparison.Ordinal),
                "all slides stay in one continuous layout");
            Assert.True(
                content.Html.Contains("function fitSlides()", StringComparison.Ordinal) &&
                content.Html.Contains("frame.style.width=", StringComparison.Ordinal) &&
                !content.Html.Contains("showSlide(", StringComparison.Ordinal),
                "slides scale to the viewport instead of hiding every page but one");
            Assert.True(
                content.Html.Contains("new IntersectionObserver", StringComparison.Ordinal) &&
                content.Html.Contains("type:'slide',index", StringComparison.Ordinal),
                "scrolling reports the visible slide back to the navigation rail");
            Assert.True(
                content.Html.Contains("class=\"connector\"", StringComparison.Ordinal),
                "connector shapes keep their geometry");
            Assert.True(
                content.Html.Contains("stroke=\"#C00000\"", StringComparison.Ordinal),
                "connector shapes keep their line paint");
            Assert.True(
                content.Html.Contains("<linearGradient id=\"line-paint-", StringComparison.Ordinal) &&
                content.Html.Contains("stop-color=\"#C00000\"", StringComparison.Ordinal) &&
                content.Html.Contains("stop-color=\"#E0522E\"", StringComparison.Ordinal),
                "gradient connector strokes keep their gradient paint");
            Assert.True(
                content.Html.Contains("vector-effect=\"non-scaling-stroke\"", StringComparison.Ordinal) &&
                content.Html.Contains("stroke-dasharray=\"7 5\"", StringComparison.Ordinal),
                "thin and dashed connectors use their real pixel geometry");
            Assert.True(
                content.Html.Contains("d=\"M0,0 H96 V96 H192\"", StringComparison.Ordinal) &&
                content.Html.Contains("marker-end=\"url(#line-arrow-end-", StringComparison.Ordinal),
                "bent connectors keep orthogonal segments and arrowheads");
            Assert.True(
                content.Html.Contains(
                    "class=\"shape smartart\" style=\"left:960px;top:480px;" +
                    "width:192px;height:192px",
                    StringComparison.Ordinal) &&
                content.Html.Contains(
                    "class=\"smartart-shape\" style=\"left:0px;top:0px;" +
                    "width:192px;height:192px;background:#336699;border-radius:50%;",
                    StringComparison.Ordinal) &&
                content.Html.Contains(">abc<", StringComparison.Ordinal) &&
                content.Html.Contains(
                    "left:0px;top:48px;width:192px;height:96px;display:flex",
                    StringComparison.Ordinal),
                "precomputed SmartArt drawings render their shapes and text coordinates");
            Assert.True(
                content.Html.Contains("background:linear-gradient(", StringComparison.Ordinal) &&
                content.Html.Contains("border-radius:50%", StringComparison.Ordinal),
                "gradient fills and preset geometry survive conversion");
            Assert.True(
                content.Html.Contains("class=\"shape custom-shape\"", StringComparison.Ordinal) &&
                content.Html.Contains("<path d=\"M", StringComparison.Ordinal) &&
                content.Html.Contains("fill=\"#00B050\"", StringComparison.Ordinal),
                "custom vector geometry is drawn instead of becoming a filled rectangle");
            Assert.True(
                content.Html.Contains("class=\"group\"", StringComparison.Ordinal) &&
                content.Html.Contains("scaleX(-1)", StringComparison.Ordinal),
                "group rotation and mirroring apply to the whole child coordinate space");
            Assert.True(
                content.Html.Contains("class=\"connector arc\"", StringComparison.Ordinal) &&
                content.Html.Contains("<path d=\"M", StringComparison.Ordinal) &&
                content.Html.Contains("marker-end=\"url(#line-arrow-end-", StringComparison.Ordinal),
                "an arc keeps its curved geometry, gradient stroke and arrowhead");
            Assert.True(
                content.Html.Contains("M50,0 L50,100 Z", StringComparison.Ordinal) &&
                content.Html.Contains("fill=\"#D9D9D9\"", StringComparison.Ordinal),
                "group paint and DrawingML guide formulas survive nested custom geometry");
            Assert.True(
                content.Html.Contains("class=\"chart-svg\"", StringComparison.Ordinal),
                "chart parts are rendered instead of replaced by a text placeholder");
            Assert.True(
                content.Html.Contains("Quarterly result", StringComparison.Ordinal),
                "the chart title comes from the related chart part");
            Assert.Equal(
                15,
                System.Text.RegularExpressions.Regex.Matches(
                    content.Html, "data-chart=\"horizontal\"").Count,
                "horizontal bar charts render every category in every series");
            Assert.True(
                content.Html.Contains(">类别 1</text>", StringComparison.Ordinal) &&
                content.Html.Contains(">类别 5</text>", StringComparison.Ordinal) &&
                content.Html.Contains(">5.3</text>", StringComparison.Ordinal) &&
                content.Html.Contains("fill=\"#BC1D2D\"", StringComparison.Ordinal),
                "horizontal bar charts keep category order, values and series colours");
            Assert.Equal(
                "1  Inherited title", content.Navigation[0].Label,
                "the outline still extracts the slide title");
            Assert.Equal(
                "2  Second title", content.Navigation[1].Label,
                "the second title stays distinct from the first");
        }
        finally
        {
            OfficeImageStore.Cleanup(imageFolder);
        }
    }

    private static void WritePresentation(string path)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        Add(
            archive,
            "ppt/presentation.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:presentation
                xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:sldSz cx="12192000" cy="6858000"/>
              <p:sldIdLst>
                <p:sldId id="256" r:id="rId1"/>
                <p:sldId id="257" r:id="rId2"/>
              </p:sldIdLst>
            </p:presentation>
            """);
        Add(
            archive,
            "ppt/_rels/presentation.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships
                xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1"
                  Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide"
                  Target="slides/slide1.xml"/>
              <Relationship Id="rId2"
                  Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide"
                  Target="slides/slide2.xml"/>
            </Relationships>
            """);
        Add(
            archive,
            "ppt/slides/slide1.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld
                xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart">
              <p:cSld>
                <p:spTree>
                  <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                  <p:grpSpPr/>
                  <p:sp>
                    <p:nvSpPr>
                      <p:cNvPr id="2" name="Title"/>
                      <p:cNvSpPr/>
                      <p:nvPr><p:ph type="title"/></p:nvPr>
                    </p:nvSpPr>
                    <p:spPr/>
                    <p:txBody>
                      <a:bodyPr/>
                      <a:p><a:r><a:t>Inherited title</a:t></a:r></a:p>
                    </p:txBody>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr>
                      <p:cNvPr id="3" name="Body"/>
                      <p:cNvSpPr/>
                      <p:nvPr><p:ph type="body" idx="1"/></p:nvPr>
                    </p:nvSpPr>
                    <p:spPr/>
                    <p:txBody>
                      <a:bodyPr/>
                      <a:p><a:r><a:t>Master positioned body</a:t></a:r></a:p>
                    </p:txBody>
                  </p:sp>
                  <p:pic>
                    <p:nvPicPr><p:cNvPr id="4" name="Picture"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
                    <p:blipFill>
                      <a:blip r:embed="rId1">
                        <a:alphaModFix amt="42000"/>
                        <a:duotone><a:srgbClr val="E0522E"/><a:prstClr val="white"/></a:duotone>
                      </a:blip>
                      <a:srcRect l="10000" t="20000" r="30000" b="40000"/>
                    </p:blipFill>
                    <p:spPr>
                      <a:xfrm>
                        <a:off x="5486400" y="2743200"/>
                        <a:ext cx="1828800" cy="1828800"/>
                      </a:xfrm>
                    </p:spPr>
                  </p:pic>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="5" name="Gradient circle"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                    <p:spPr>
                      <a:xfrm>
                        <a:off x="228600" y="228600"/>
                        <a:ext cx="914400" cy="914400"/>
                      </a:xfrm>
                      <a:prstGeom prst="ellipse"/>
                      <a:gradFill>
                        <a:gsLst>
                          <a:gs pos="0"><a:srgbClr val="FFFFFF"/></a:gs>
                          <a:gs pos="100000"><a:srgbClr val="0070C0"/></a:gs>
                        </a:gsLst>
                        <a:lin ang="0"/>
                      </a:gradFill>
                    </p:spPr>
                  </p:sp>
                  <p:cxnSp>
                    <p:nvCxnSpPr><p:cNvPr id="6" name="Connector"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr>
                    <p:spPr>
                      <a:xfrm><a:off x="914400" y="3657600"/><a:ext cx="1828800" cy="0"/></a:xfrm>
                      <a:prstGeom prst="line"/>
                      <a:ln w="25400"><a:solidFill><a:srgbClr val="C00000"/></a:solidFill></a:ln>
                    </p:spPr>
                  </p:cxnSp>
                  <p:cxnSp>
                    <p:nvCxnSpPr><p:cNvPr id="9" name="Gradient connector"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr>
                    <p:spPr>
                      <a:xfrm><a:off x="914400" y="3886200"/><a:ext cx="1828800" cy="0"/></a:xfrm>
                      <a:prstGeom prst="line"/>
                      <a:ln w="25400">
                        <a:gradFill>
                          <a:gsLst>
                            <a:gs pos="100000"><a:srgbClr val="E0522E"/></a:gs>
                            <a:gs pos="0"><a:srgbClr val="C00000"/></a:gs>
                          </a:gsLst>
                          <a:lin ang="0"/>
                        </a:gradFill>
                        <a:prstDash val="dash"/>
                      </a:ln>
                    </p:spPr>
                  </p:cxnSp>
                  <p:cxnSp>
                    <p:nvCxnSpPr><p:cNvPr id="14" name="Bent connector"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr>
                    <p:spPr>
                      <a:xfrm>
                        <a:off x="914400" y="4114800"/>
                        <a:ext cx="1828800" cy="914400"/>
                      </a:xfrm>
                      <a:prstGeom prst="bentConnector3"><a:avLst/></a:prstGeom>
                      <a:ln w="25400">
                        <a:solidFill><a:srgbClr val="C00000"/></a:solidFill>
                        <a:tailEnd type="triangle" w="sm" len="sm"/>
                      </a:ln>
                    </p:spPr>
                  </p:cxnSp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="12" name="Gradient arc"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                    <p:spPr>
                      <a:xfrm>
                        <a:off x="7772400" y="228600"/>
                        <a:ext cx="914400" cy="914400"/>
                      </a:xfrm>
                      <a:prstGeom prst="arc">
                        <a:avLst>
                          <a:gd name="adj1" fmla="val 10214044"/>
                          <a:gd name="adj2" fmla="val 310579"/>
                        </a:avLst>
                      </a:prstGeom>
                      <a:noFill/>
                      <a:ln w="38100">
                        <a:gradFill>
                          <a:gsLst>
                            <a:gs pos="100000"><a:srgbClr val="E0522E"/></a:gs>
                            <a:gs pos="0"><a:srgbClr val="C00000"/></a:gs>
                          </a:gsLst>
                          <a:lin ang="0"/>
                        </a:gradFill>
                        <a:tailEnd type="triangle" w="sm" len="sm"/>
                      </a:ln>
                    </p:spPr>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="13" name="Cropped picture fill"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                    <p:spPr>
                      <a:xfrm>
                        <a:off x="9144000" y="228600"/>
                        <a:ext cx="914400" cy="914400"/>
                      </a:xfrm>
                      <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                      <a:blipFill>
                        <a:blip r:embed="rId1"/>
                        <a:srcRect l="25000" r="25000"/>
                        <a:stretch><a:fillRect/></a:stretch>
                      </a:blipFill>
                    </p:spPr>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="6" name="Custom icon"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                    <p:spPr>
                      <a:xfrm>
                        <a:off x="1371600" y="228600"/>
                        <a:ext cx="914400" cy="914400"/>
                      </a:xfrm>
                      <a:custGeom>
                        <a:avLst/>
                        <a:gdLst/>
                        <a:ahLst/>
                        <a:cxnLst/>
                        <a:rect l="0" t="0" r="r" b="b"/>
                        <a:pathLst>
                          <a:path w="100" h="100">
                            <a:moveTo><a:pt x="10" y="50"/></a:moveTo>
                            <a:cubicBezTo>
                              <a:pt x="10" y="20"/>
                              <a:pt x="90" y="20"/>
                              <a:pt x="90" y="50"/>
                            </a:cubicBezTo>
                            <a:lnTo><a:pt x="50" y="90"/></a:lnTo>
                            <a:close/>
                          </a:path>
                        </a:pathLst>
                      </a:custGeom>
                      <a:solidFill><a:srgbClr val="00B050"/></a:solidFill>
                    </p:spPr>
                  </p:sp>
                  <p:grpSp>
                    <p:nvGrpSpPr><p:cNvPr id="10" name="Mapped group"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                    <p:grpSpPr>
                      <a:xfrm flipH="1">
                        <a:off x="2743200" y="228600"/>
                        <a:ext cx="914400" cy="914400"/>
                        <a:chOff x="0" y="0"/>
                        <a:chExt cx="100" cy="100"/>
                      </a:xfrm>
                      <a:solidFill><a:srgbClr val="D9D9D9"/></a:solidFill>
                    </p:grpSpPr>
                    <p:sp>
                      <p:nvSpPr><p:cNvPr id="11" name="Guided shape"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                      <p:spPr>
                        <a:xfrm><a:off x="0" y="0"/><a:ext cx="100" cy="100"/></a:xfrm>
                        <a:custGeom>
                          <a:avLst/>
                          <a:gdLst><a:gd name="T0" fmla="*/ 1 w 2"/></a:gdLst>
                          <a:pathLst>
                            <a:path w="100" h="100">
                              <a:moveTo><a:pt x="T0" y="0"/></a:moveTo>
                              <a:lnTo><a:pt x="T0" y="h"/></a:lnTo>
                              <a:close/>
                            </a:path>
                          </a:pathLst>
                        </a:custGeom>
                        <a:grpFill/>
                      </p:spPr>
                    </p:sp>
                  </p:grpSp>
                  <p:graphicFrame>
                    <p:nvGraphicFramePr>
                      <p:cNvPr id="7" name="Chart"/>
                      <p:cNvGraphicFramePr/>
                      <p:nvPr/>
                    </p:nvGraphicFramePr>
                    <p:xfrm><a:off x="457200" y="3657600"/><a:ext cx="5486400" cy="2743200"/></p:xfrm>
                    <a:graphic>
                      <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/chart">
                        <c:chart r:id="rId3"/>
                      </a:graphicData>
                    </a:graphic>
                  </p:graphicFrame>
                  <p:graphicFrame>
                    <p:nvGraphicFramePr>
                      <p:cNvPr id="8" name="Horizontal chart"/>
                      <p:cNvGraphicFramePr/>
                      <p:nvPr/>
                    </p:nvGraphicFramePr>
                    <p:xfrm><a:off x="6400800" y="3657600"/><a:ext cx="5486400" cy="2743200"/></p:xfrm>
                    <a:graphic>
                      <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/chart">
                        <c:chart r:id="rId4"/>
                      </a:graphicData>
                    </a:graphic>
                  </p:graphicFrame>
                  <p:graphicFrame>
                    <p:nvGraphicFramePr>
                      <p:cNvPr id="15" name="SmartArt"/>
                      <p:cNvGraphicFramePr/>
                      <p:nvPr/>
                    </p:nvGraphicFramePr>
                    <p:xfrm><a:off x="9144000" y="4572000"/><a:ext cx="1828800" cy="1828800"/></p:xfrm>
                    <a:graphic>
                      <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/diagram">
                        <dgm:relIds xmlns:dgm="http://schemas.openxmlformats.org/drawingml/2006/diagram"
                            r:dm="rId5" r:lo="rId6" r:qs="rId7" r:cs="rId8"/>
                      </a:graphicData>
                    </a:graphic>
                  </p:graphicFrame>
                </p:spTree>
              </p:cSld>
            </p:sld>
            """);
        Add(
            archive,
            "ppt/slides/slide2.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld
                xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:cSld>
                <p:spTree>
                  <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                  <p:grpSpPr/>
                  <p:sp>
                    <p:nvSpPr>
                      <p:cNvPr id="2" name="Title"/>
                      <p:cNvSpPr/>
                      <p:nvPr><p:ph type="title"/></p:nvPr>
                    </p:nvSpPr>
                    <p:spPr/>
                    <p:txBody>
                      <a:bodyPr/>
                      <a:lstStyle>
                        <a:lvl1pPr>
                          <a:defRPr sz="4200" b="1">
                            <a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill>
                            <a:latin typeface="Aptos"/>
                          </a:defRPr>
                        </a:lvl1pPr>
                      </a:lstStyle>
                      <a:p><a:r><a:t>Second title</a:t></a:r></a:p>
                    </p:txBody>
                  </p:sp>
                  <p:pic>
                    <p:nvPicPr><p:cNvPr id="3" name="Second picture"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
                    <p:blipFill><a:blip r:embed="rId1"/></p:blipFill>
                    <p:spPr>
                      <a:xfrm>
                        <a:off x="1828800" y="914400"/>
                        <a:ext cx="1828800" cy="1828800"/>
                      </a:xfrm>
                    </p:spPr>
                  </p:pic>
                </p:spTree>
              </p:cSld>
            </p:sld>
            """);
        Add(
            archive,
            "ppt/slides/_rels/slide2.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships
                xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1"
                  Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"
                  Target="../media/image2.png"/>
              <Relationship Id="rId2"
                  Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout"
                  Target="../slideLayouts/slideLayout1.xml"/>
            </Relationships>
            """);
        Add(
            archive,
            "ppt/slides/_rels/slide1.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships
                xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1"
                  Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"
                  Target="../media/image1.png"/>
              <Relationship Id="rId2"
                  Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout"
                  Target="../slideLayouts/slideLayout1.xml"/>
              <Relationship Id="rId3"
                  Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart"
                  Target="../charts/chart1.xml"/>
              <Relationship Id="rId4"
                  Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart"
                  Target="../charts/chart2.xml"/>
              <Relationship Id="rId5"
                  Type="http://schemas.microsoft.com/office/2007/relationships/diagramDrawing"
                  Target="../diagrams/drawing1.xml"/>
            </Relationships>
            """);
        Add(
            archive,
            "ppt/slideLayouts/slideLayout1.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sldLayout
                xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:cSld>
                <p:spTree>
                  <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                  <p:grpSpPr/>
                  <p:sp>
                    <p:nvSpPr>
                      <p:cNvPr id="2" name="Title"/>
                      <p:cNvSpPr/>
                      <p:nvPr><p:ph type="title"/></p:nvPr>
                    </p:nvSpPr>
                    <p:spPr>
                      <a:xfrm>
                        <a:off x="914400" y="457200"/>
                        <a:ext cx="3657600" cy="1828800"/>
                      </a:xfrm>
                      <a:solidFill><a:srgbClr val="EEF2F8"/></a:solidFill>
                    </p:spPr>
                    <p:txBody>
                      <a:bodyPr/>
                      <a:lstStyle>
                        <a:lvl1pPr algn="ctr">
                          <a:defRPr sz="3000" b="1">
                            <a:solidFill><a:srgbClr val="C00000"/></a:solidFill>
                          </a:defRPr>
                        </a:lvl1pPr>
                      </a:lstStyle>
                      <a:p><a:r><a:t>Layout prompt</a:t></a:r></a:p>
                    </p:txBody>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr>
                      <p:cNvPr id="3" name="Body"/>
                      <p:cNvSpPr/>
                      <p:nvPr><p:ph type="body" idx="1"/></p:nvPr>
                    </p:nvSpPr>
                    <p:spPr/>
                    <p:txBody><a:bodyPr/><a:p><a:r><a:t>Layout body prompt</a:t></a:r></a:p></p:txBody>
                  </p:sp>
                </p:spTree>
              </p:cSld>
            </p:sldLayout>
            """);
        Add(
            archive,
            "ppt/slideLayouts/_rels/slideLayout1.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships
                xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1"
                  Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster"
                  Target="../slideMasters/slideMaster1.xml"/>
            </Relationships>
            """);
        Add(
            archive,
            "ppt/slideMasters/slideMaster1.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sldMaster
                xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <p:cSld>
                <p:spTree>
                  <p:nvGrpSpPr/>
                  <p:grpSpPr/>
                  <p:sp>
                    <p:nvSpPr>
                      <p:cNvPr id="2" name="Body"/>
                      <p:cNvSpPr/>
                      <p:nvPr><p:ph type="body" idx="1"/></p:nvPr>
                    </p:nvSpPr>
                    <p:spPr>
                      <a:xfrm>
                        <a:off x="457200" y="2286000"/>
                        <a:ext cx="5486400" cy="1828800"/>
                      </a:xfrm>
                    </p:spPr>
                    <p:txBody><a:bodyPr/><a:p><a:r><a:t>Master prompt</a:t></a:r></a:p></p:txBody>
                  </p:sp>
                </p:spTree>
              </p:cSld>
              <p:txStyles>
                <p:bodyStyle>
                  <a:lvl1pPr><a:defRPr sz="1800"/></a:lvl1pPr>
                </p:bodyStyle>
              </p:txStyles>
            </p:sldMaster>
            """);
        Add(
            archive,
            "ppt/charts/chart1.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <c:chartSpace
                xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <c:chart>
                <c:title><c:tx><c:rich>
                  <a:bodyPr/><a:p><a:r><a:t>Quarterly result</a:t></a:r></a:p>
                </c:rich></c:tx></c:title>
                <c:plotArea>
                  <c:barChart>
                    <c:barDir val="col"/>
                    <c:ser>
                      <c:tx><c:strRef><c:strCache><c:pt idx="0"><c:v>Revenue</c:v></c:pt></c:strCache></c:strRef></c:tx>
                      <c:cat><c:strLit>
                        <c:pt idx="0"><c:v>Q1</c:v></c:pt>
                        <c:pt idx="1"><c:v>Q2</c:v></c:pt>
                      </c:strLit></c:cat>
                      <c:val><c:numLit>
                        <c:pt idx="0"><c:v>12</c:v></c:pt>
                        <c:pt idx="1"><c:v>18</c:v></c:pt>
                      </c:numLit></c:val>
                    </c:ser>
                  </c:barChart>
                </c:plotArea>
              </c:chart>
            </c:chartSpace>
            """);
        Add(
            archive,
            "ppt/charts/chart2.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <c:chartSpace
                xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <c:chart>
                <c:plotArea>
                  <c:barChart>
                    <c:barDir val="bar"/>
                    <c:ser>
                      <c:tx><c:strRef><c:strCache><c:pt idx="0"><c:v>系列 1</c:v></c:pt></c:strCache></c:strRef></c:tx>
                      <c:spPr><a:solidFill><a:srgbClr val="BC1D2D"/></a:solidFill></c:spPr>
                      <c:dLbls><c:showVal val="1"/></c:dLbls>
                      <c:cat><c:strLit>
                        <c:pt idx="0"><c:v>类别 1</c:v></c:pt>
                        <c:pt idx="1"><c:v>类别 2</c:v></c:pt>
                        <c:pt idx="2"><c:v>类别 3</c:v></c:pt>
                        <c:pt idx="3"><c:v>类别 4</c:v></c:pt>
                        <c:pt idx="4"><c:v>类别 5</c:v></c:pt>
                      </c:strLit></c:cat>
                      <c:val><c:numLit>
                        <c:pt idx="0"><c:v>4.2</c:v></c:pt>
                        <c:pt idx="1"><c:v>2.5</c:v></c:pt>
                        <c:pt idx="2"><c:v>3.7</c:v></c:pt>
                        <c:pt idx="3"><c:v>4.5</c:v></c:pt>
                        <c:pt idx="4"><c:v>5.3</c:v></c:pt>
                      </c:numLit></c:val>
                    </c:ser>
                    <c:ser>
                      <c:tx><c:strRef><c:strCache><c:pt idx="0"><c:v>系列 2</c:v></c:pt></c:strCache></c:strRef></c:tx>
                      <c:spPr><a:solidFill><a:srgbClr val="E0522E"/></a:solidFill></c:spPr>
                      <c:cat><c:strLit>
                        <c:pt idx="0"><c:v>类别 1</c:v></c:pt>
                        <c:pt idx="1"><c:v>类别 2</c:v></c:pt>
                        <c:pt idx="2"><c:v>类别 3</c:v></c:pt>
                        <c:pt idx="3"><c:v>类别 4</c:v></c:pt>
                        <c:pt idx="4"><c:v>类别 5</c:v></c:pt>
                      </c:strLit></c:cat>
                      <c:val><c:numLit>
                        <c:pt idx="0"><c:v>2.3</c:v></c:pt>
                        <c:pt idx="1"><c:v>4.3</c:v></c:pt>
                        <c:pt idx="2"><c:v>1.8</c:v></c:pt>
                        <c:pt idx="3"><c:v>2.9</c:v></c:pt>
                        <c:pt idx="4"><c:v>3.2</c:v></c:pt>
                      </c:numLit></c:val>
                    </c:ser>
                    <c:ser>
                      <c:tx><c:strRef><c:strCache><c:pt idx="0"><c:v>系列 3</c:v></c:pt></c:strCache></c:strRef></c:tx>
                      <c:spPr><a:solidFill><a:srgbClr val="E8A0A4"/></a:solidFill></c:spPr>
                      <c:cat><c:strLit>
                        <c:pt idx="0"><c:v>类别 1</c:v></c:pt>
                        <c:pt idx="1"><c:v>类别 2</c:v></c:pt>
                        <c:pt idx="2"><c:v>类别 3</c:v></c:pt>
                        <c:pt idx="3"><c:v>类别 4</c:v></c:pt>
                        <c:pt idx="4"><c:v>类别 5</c:v></c:pt>
                      </c:strLit></c:cat>
                      <c:val><c:numLit>
                        <c:pt idx="0"><c:v>3.8</c:v></c:pt>
                        <c:pt idx="1"><c:v>2.8</c:v></c:pt>
                        <c:pt idx="2"><c:v>2.2</c:v></c:pt>
                        <c:pt idx="3"><c:v>3.2</c:v></c:pt>
                        <c:pt idx="4"><c:v>4.4</c:v></c:pt>
                      </c:numLit></c:val>
                    </c:ser>
                  </c:barChart>
                  <c:catAx>
                    <c:delete val="0"/>
                    <c:axPos val="l"/>
                  </c:catAx>
                  <c:valAx>
                    <c:delete val="1"/>
                    <c:axPos val="b"/>
                  </c:valAx>
                </c:plotArea>
                <c:legend><c:legendPos val="t"/></c:legend>
              </c:chart>
            </c:chartSpace>
            """);
        Add(
            archive,
            "ppt/diagrams/drawing1.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <dsp:drawing
                xmlns:dsp="http://schemas.microsoft.com/office/drawing/2008/diagram"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <dsp:spTree>
                <dsp:nvGrpSpPr><dsp:cNvPr id="0" name=""/><dsp:cNvGrpSpPr/></dsp:nvGrpSpPr>
                <dsp:grpSpPr/>
                <dsp:sp>
                  <dsp:nvSpPr><dsp:cNvPr id="0" name=""/><dsp:cNvSpPr/></dsp:nvSpPr>
                  <dsp:spPr>
                    <a:xfrm><a:off x="0" y="0"/><a:ext cx="1828800" cy="1828800"/></a:xfrm>
                    <a:prstGeom prst="ellipse"><a:avLst/></a:prstGeom>
                    <a:solidFill><a:srgbClr val="336699"/></a:solidFill>
                  </dsp:spPr>
                  <dsp:txBody>
                    <a:bodyPr anchor="ctr"/>
                    <a:lstStyle/>
                    <a:p><a:r><a:rPr lang="en-US" sz="1800"/><a:t>abc</a:t></a:r></a:p>
                  </dsp:txBody>
                  <dsp:txXfrm><a:off x="0" y="457200"/><a:ext cx="1828800" cy="914400"/></dsp:txXfrm>
                </dsp:sp>
              </dsp:spTree>
            </dsp:drawing>
            """);

        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        Add(archive, "ppt/media/image1.png", image);
        Add(archive, "ppt/media/image2.png", image);
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void Add(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }
}
