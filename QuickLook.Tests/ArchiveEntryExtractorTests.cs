// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Plugin.ArchiveViewer.ArchiveFile;
using System;
using System.IO;
using System.IO.Compression;

namespace QuickLook.Tests;

internal class ArchiveEntryExtractorTests : ITestFixture
{
    private readonly string _testDirectory = Path.Combine(
        AppContext.BaseDirectory,
        $"archive-extractor-{Guid.NewGuid():N}");

    public void Setup()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    public void Teardown()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
    }

    public void ExtractsOnlyTheSelectedNestedEntry()
    {
        var archivePath = CreateZip(
            "nested.zip",
            ("root/first.txt", "first"),
            ("root/deeper/preview.txt", "selected preview"));

        var extracted = ArchiveEntryExtractor.Extract(
            archivePath,
            @"root\deeper\preview.txt");

        try
        {
            Assert.True(File.Exists(extracted.FilePath), "the selected entry should be extracted");
            Assert.Equal("selected preview", File.ReadAllText(extracted.FilePath), "entry contents");
            Assert.False(
                File.Exists(Path.Combine(extracted.TemporaryDirectory, "first.txt")),
                "unselected entries must not be extracted");
        }
        finally
        {
            ArchiveEntryExtractor.TryDeleteDirectory(extracted.TemporaryDirectory);
        }
    }

    public void MatchesEntryNamesRegardlessOfArchiveSeparators()
    {
        var archivePath = CreateZip("separators.zip", ("images/photo.jpg", "image"));

        var extracted = ArchiveEntryExtractor.Extract(
            archivePath,
            @"images\photo.jpg");

        try
        {
            Assert.Equal("photo.jpg", Path.GetFileName(extracted.FilePath),
                "the original extension must be preserved for viewer matching");
        }
        finally
        {
            ArchiveEntryExtractor.TryDeleteDirectory(extracted.TemporaryDirectory);
        }
    }

    public void ReportsMissingEntry()
    {
        var archivePath = CreateZip("missing.zip", ("present.txt", "present"));
        var failed = false;

        try
        {
            ArchiveEntryExtractor.Extract(archivePath, "absent.txt");
        }
        catch (ArchiveEntryNotFoundException)
        {
            failed = true;
        }

        Assert.True(failed, "a missing entry should be reported without scanning the whole archive twice");
    }

    public void RejectsOversizedEntryBeforeExtraction()
    {
        var archivePath = CreateZip("large.zip", ("large.bin", "large"));
        var failed = false;

        try
        {
            ArchiveEntryExtractor.Extract(
                archivePath,
                "large.bin",
                (ulong)ArchiveEntryExtractor.MaxPreviewBytes + 1);
        }
        catch (ArchiveEntryTooLargeException)
        {
            failed = true;
        }

        Assert.True(failed, "files over the preview limit must not be extracted");
    }

    private string CreateZip(string fileName, params (string Name, string Content)[] entries)
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
