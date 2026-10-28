// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Plugin.XMindViewer;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using XMindViewerPlugin = QuickLook.Plugin.XMindViewer.Plugin;

namespace QuickLook.Tests;

internal class XMindParserTests : ITestFixture
{
    private readonly string _testDirectory = Path.Combine(
        AppContext.BaseDirectory,
        $"xmind-parser-{Guid.NewGuid():N}");

    public void Setup()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    public void Teardown()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
    }

    public void ModernJsonContentPreservesTopicTree()
    {
        var path = CreateArchive(
            "modern.xmind",
            ("content.json",
                """
                [{
                  "title": "Test map",
                  "rootTopic": {
                    "title": "Root",
                    "children": {
                      "attached": [
                        {
                          "title": "Camera",
                          "children": {
                            "attached": [
                              { "title": "Normal" },
                              { "title": "Boundary" }
                            ]
                          }
                        },
                        { "title": "Lighting" }
                      ]
                    }
                  }
                }]
                """));

        var sheets = XMindParser.Parse(path);

        Assert.Equal(1, sheets.Count, "one XMind sheet is expected");
        Assert.Equal("Test map", sheets[0].Title, "sheet title");
        Assert.Equal("Root", sheets[0].RootTopic.Title, "root title");
        Assert.Equal(2, sheets[0].RootTopic.Children.Count, "direct child count");
        Assert.Equal("Camera", sheets[0].RootTopic.Children[0].Title, "first child title");
        Assert.Equal(2, sheets[0].RootTopic.Children[0].Children.Count, "grandchild count");
    }

    public void XMindViewerClaimsTheExtensionWithoutTheThumbnailFallback()
    {
        var plugin = new XMindViewerPlugin();

        Assert.True(plugin.CanHandle(@"C:\maps\requirements.XMIND"),
            "the native XMind viewer must claim the extension case-insensitively");
        Assert.False(plugin.CanHandle(@"C:\maps\requirements.json"),
            "unrelated file types must stay with their existing viewers");
    }

    public void PanelLaysOutLeftAndRightBranches()
    {
        var path = CreateArchive(
            "two-sided.xmind",
            ("content.json",
                """
                [{
                  "title": "Two sided",
                  "rootTopic": {
                    "title": "Root",
                    "children": {
                      "attached": [
                        {
                          "title": "Right branch",
                          "children": {
                            "attached": [{ "title": "Right leaf" }]
                          }
                        },
                        {
                          "title": "Left branch",
                          "children": {
                            "attached": [{ "title": "Left leaf" }]
                          }
                        }
                      ]
                    }
                  }
                }]
                """));

        var panel = new XMindPanel();
        panel.Apply(XMindParser.Parse(path));
        panel.Measure(new Size(1120, 760));
        panel.Arrange(new Rect(0, 0, 1120, 760));
        panel.UpdateLayout();

        Assert.True(panel.ActualWidth > 0 && panel.ActualHeight > 0,
            "the two-sided mind map must complete layout");
    }

    public void LegacyXmlContentPreservesTopicTree()
    {
        var path = CreateArchive(
            "legacy.xmind",
            ("content.xml",
                """
                <xmap-content xmlns="urn:xmind:xmap:xmlns:content:2.0">
                  <sheet id="sheet-1">
                    <title>Legacy map</title>
                    <topic id="root">
                      <title>Root</title>
                      <children>
                        <topics type="attached">
                          <topic id="child">
                            <title>Child</title>
                          </topic>
                        </topics>
                      </children>
                    </topic>
                  </sheet>
                </xmap-content>
                """));

        var sheets = XMindParser.Parse(path);

        Assert.Equal(1, sheets.Count, "one legacy sheet is expected");
        Assert.Equal("Legacy map", sheets[0].Title, "legacy sheet title");
        Assert.Equal("Root", sheets[0].RootTopic.Title, "legacy root title");
        Assert.Equal("Child", sheets[0].RootTopic.Children.Single().Title, "legacy child title");
    }

    public void InvalidJsonFallsBackToXml()
    {
        var path = CreateArchive(
            "fallback.xmind",
            ("content.json", "{ not-json"),
            ("content.xml",
                """
                <xmap-content>
                  <sheet>
                    <title>Fallback</title>
                    <topic>
                      <title>XML root</title>
                    </topic>
                  </sheet>
                </xmap-content>
                """));

        var sheets = XMindParser.Parse(path);

        Assert.Equal("Fallback", sheets.Single().Title, "XML fallback sheet title");
        Assert.Equal("XML root", sheets.Single().RootTopic.Title, "XML fallback root title");
    }

    public void UserSampleParsesWhenProvided()
    {
        var sample = Environment.GetEnvironmentVariable("QKN_XMIND_SAMPLE");
        if (string.IsNullOrWhiteSpace(sample) || !File.Exists(sample))
            return;

        var sheets = XMindParser.Parse(sample);

        Assert.True(sheets.Count > 0, "the real XMind sample has at least one sheet");
        Assert.True(
            sheets.Any(sheet => sheet.RootTopic.Children.Count > 0),
            "the real XMind sample contains visible branches");

        var panel = new XMindPanel();
        panel.Apply(sheets);
        panel.Measure(new Size(1120, 760));
        panel.Arrange(new Rect(0, 0, 1120, 760));
        panel.UpdateLayout();

        Assert.True(panel.ActualWidth > 0 && panel.ActualHeight > 0,
            "the real XMind sample must complete the left/right layout");
    }

    private string CreateArchive(
        string fileName,
        params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_testDirectory, fileName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            var item = archive.CreateEntry(entry.Name, CompressionLevel.Fastest);
            using var writer = new StreamWriter(item.Open());
            writer.Write(entry.Content);
        }

        return path;
    }
}
