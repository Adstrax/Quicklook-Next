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
                content.Html.Contains("class=\"pic\"", StringComparison.Ordinal),
                "the slide picture is rendered through its relationship");
            Assert.Equal(
                2, Directory.GetFiles(imageFolder).Length,
                "each slide resolves its own rId1 image instead of reusing another slide's picture");
            Assert.True(
                content.Html.Contains(">Second title<", StringComparison.Ordinal),
                "the second slide is converted through its own related parts");
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
                    <p:blipFill><a:blip r:embed="rId1"/></p:blipFill>
                    <p:spPr>
                      <a:xfrm>
                        <a:off x="5486400" y="2743200"/>
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
