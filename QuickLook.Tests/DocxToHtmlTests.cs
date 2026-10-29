// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Plugin.ArchiveViewer.ArchiveFile;
using QuickLook.Plugin.OfficeViewer;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace QuickLook.Tests;

/// <summary>
/// Word relationship targets are ZIP package paths. They must be looked up with
/// forward slashes, including after a document has been extracted from an archive.
/// </summary>
internal class DocxToHtmlTests : SettingsFixture
{
    public void EmbeddedPictureIsRendered()
    {
        var path = Path.Combine(Root, "picture.docx");
        WriteDocument(path, "media/image1.png");

        AssertPictureIsRendered(path);
    }

    public void PictureInAnArchiveContainedWordIsRendered()
    {
        var wordPath = Path.Combine(Root, "source.docx");
        var archivePath = Path.Combine(Root, "documents.zip");
        WriteDocument(wordPath, "media/image1.png");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(wordPath, "nested/report.docx");

        var extracted = ArchiveEntryExtractor.Extract(
            archivePath,
            @"nested\report.docx");

        try
        {
            Assert.True(
                extracted.FilePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase),
                "the archive entry keeps the extension the Office viewer matches");
            AssertPictureIsRendered(extracted.FilePath);
        }
        finally
        {
            ArchiveEntryExtractor.TryDeleteDirectory(extracted.TemporaryDirectory);
        }
    }

    public void HeadingWithoutBookmarkGetsAScrollTrackingTarget()
    {
        var path = Path.Combine(Root, "heading.docx");
        WriteHeadingDocument(path);
        var imageFolder = OfficeImageStore.CreateFolder();

        try
        {
            var content = DocxToHtml.Convert(path, imageFolder, "test.local");

            Assert.Equal(1, content.Navigation.Count, "the heading appears in the floating outline");
            Assert.Equal("Introduction", content.Navigation[0].Label, "the row uses the heading text");
            Assert.Equal("h1", content.Navigation[0].Target, "the heading receives a generated target id");
            Assert.True(content.Html.Contains("<h1 id=\"h1\"", StringComparison.Ordinal),
                "the generated target id is emitted on the heading");
            Assert.True(content.Html.Contains("var officeNavTargets=[\"h1\"]", StringComparison.Ordinal),
                "the page watches the heading while the reader scrolls");
            Assert.True(content.Html.Contains("type:'office-position'", StringComparison.Ordinal),
                "the current heading is reported without asking the page to scroll");
        }
        finally
        {
            OfficeImageStore.Cleanup(imageFolder);
        }
    }

    private static void AssertPictureIsRendered(string path)
    {
        var imageFolder = OfficeImageStore.CreateFolder();

        try
        {
            var content = DocxToHtml.Convert(path, imageFolder, "test.local");
            var picture = Path.Combine(imageFolder, "img0.png");

            Assert.True(File.Exists(picture), "the embedded picture is written beside the page");
            Assert.True(
                content.Html.Contains(
                    "src=\"https://test.local/img0.png\"",
                    StringComparison.Ordinal),
                "the page points at the extracted picture");
            Assert.False(
                content.Html.Contains("This Word document cannot be read", StringComparison.Ordinal),
                "the document is converted without an error page");
        }
        finally
        {
            OfficeImageStore.Cleanup(imageFolder);
        }
    }

    private static void WriteDocument(string path, string imageTarget)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        Add(
            archive,
            "word/document.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document
                xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
              <w:body>
                <w:p>
                  <w:r>
                    <w:drawing>
                      <wp:inline>
                        <wp:extent cx="914400" cy="914400"/>
                        <a:graphic>
                          <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                            <pic:pic>
                              <pic:blipFill>
                                <a:blip r:embed="rId1"/>
                              </pic:blipFill>
                            </pic:pic>
                          </a:graphicData>
                        </a:graphic>
                      </wp:inline>
                    </w:drawing>
                  </w:r>
                </w:p>
                <w:sectPr>
                  <w:pgSz w:w="12240" w:h="15840"/>
                </w:sectPr>
              </w:body>
            </w:document>
            """);
        Add(
            archive,
            "word/_rels/document.xml.rels",
            $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship
                  Id="rId1"
                  Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"
                  Target="{{imageTarget}}"/>
            </Relationships>
            """);

        var picture = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        Add(archive, "word/media/image1.png", picture);
    }

    private static void WriteHeadingDocument(string path)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        Add(
            archive,
            "word/document.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p>
                  <w:pPr><w:pStyle w:val="Heading1"/></w:pPr>
                  <w:r><w:t>Introduction</w:t></w:r>
                </w:p>
                <w:sectPr><w:pgSz w:w="12240" w:h="15840"/></w:sectPr>
              </w:body>
            </w:document>
            """);
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
