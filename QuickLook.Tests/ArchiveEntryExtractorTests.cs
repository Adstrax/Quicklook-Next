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

    public void PreviewCacheReusesRecentEntries()
    {
        var archivePath = CreateZip("cached.zip", ("cached.txt", "cached"));
        var extracted = ArchiveEntryExtractor.Extract(archivePath, "cached.txt");

        using var cache = new ArchiveEntryPreviewCache();
        Assert.True(
            cache.TryAdd(archivePath, "cached.txt", 6, extracted),
            "the extracted entry can be cached");
        Assert.True(
            cache.TryGet(archivePath, @"cached.txt", 6, out var cached),
            "a recent entry is available without extracting again");
        Assert.Equal(extracted.FilePath, cached.FilePath, "the cached path is reused");
    }

    public void PreviewCacheEvictsLeastRecentlyUsedEntries()
    {
        var archivePath = CreateZip(
            "lru.zip",
            ("first.txt", "first!"),
            ("second.txt", "second"),
            ("third.txt", "third!"));

        var first = ArchiveEntryExtractor.Extract(archivePath, "first.txt");
        var second = ArchiveEntryExtractor.Extract(archivePath, "second.txt");
        var third = ArchiveEntryExtractor.Extract(archivePath, "third.txt");

        using var cache = new ArchiveEntryPreviewCache(maxBytes: 12, maxEntries: 2);
        cache.TryAdd(archivePath, "first.txt", 6, first);
        cache.TryAdd(archivePath, "second.txt", 6, second);
        Assert.True(
            cache.TryGet(archivePath, "first.txt", 6, out _),
            "the first entry is available");

        cache.TryAdd(archivePath, "third.txt", 6, third);

        Assert.True(
            cache.TryGet(archivePath, "first.txt", 6, out _),
            "the recently used entry survives eviction");
        Assert.False(
            cache.TryGet(archivePath, "second.txt", 6, out _),
            "the least recently used entry is evicted");
    }

    public void DisposingPreviewCacheRemovesTemporaryFiles()
    {
        var archivePath = CreateZip("dispose.zip", ("dispose.txt", "dispose"));
        var extracted = ArchiveEntryExtractor.Extract(archivePath, "dispose.txt");

        var cache = new ArchiveEntryPreviewCache();
        cache.TryAdd(archivePath, "dispose.txt", 7, extracted);
        cache.Dispose();

        Assert.False(
            Directory.Exists(extracted.TemporaryDirectory),
            "closing the archive preview removes cached temporary files");
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
