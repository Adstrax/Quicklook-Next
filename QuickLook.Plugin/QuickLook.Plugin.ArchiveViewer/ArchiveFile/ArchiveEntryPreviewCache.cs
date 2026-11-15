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

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

/// <summary>
/// Keeps a small, bounded set of recently extracted archive entries alive for
/// the lifetime of one archive preview. Revisiting an entry then avoids opening
/// and scanning the archive again. All temporary directories remain owned by
/// this cache so they can be removed together when the preview closes.
/// </summary>
internal sealed class ArchiveEntryPreviewCache : IDisposable
{
    internal const long DefaultMaxBytes = 64L * 1024L * 1024L;
    internal const int DefaultMaxEntries = 12;

    private sealed record CacheItem(
        string Key,
        ExtractedArchiveEntry Entry,
        long Size);

    private readonly long _maxBytes;
    private readonly int _maxEntries;
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _items =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheItem> _lru = [];

    private long _totalBytes;
    private bool _disposed;

    internal ArchiveEntryPreviewCache(
        long maxBytes = DefaultMaxBytes,
        int maxEntries = DefaultMaxEntries)
    {
        _maxBytes = Math.Max(1, maxBytes);
        _maxEntries = Math.Max(1, maxEntries);
    }

    internal int Count
    {
        get
        {
            lock (_sync)
                return _items.Count;
        }
    }

    internal bool TryGet(
        string archivePath,
        string entryPath,
        ulong expectedSize,
        out ExtractedArchiveEntry entry)
    {
        var key = BuildKey(archivePath, entryPath, expectedSize);

        lock (_sync)
        {
            entry = null;
            if (_disposed || !_items.TryGetValue(key, out var node))
                return false;

            if (!System.IO.Directory.Exists(node.Value.Entry.TemporaryDirectory) ||
                !System.IO.File.Exists(node.Value.Entry.FilePath))
            {
                RemoveNode(node);
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            entry = node.Value.Entry;
            return true;
        }
    }

    internal bool TryAdd(
        string archivePath,
        string entryPath,
        ulong expectedSize,
        ExtractedArchiveEntry entry)
    {
        if (entry == null)
            return false;

        var key = BuildKey(archivePath, entryPath, expectedSize);
        var size = GetEntrySize(entry);

        lock (_sync)
        {
            if (_disposed)
                return false;

            if (_items.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _totalBytes -= existing.Value.Size;
                _items.Remove(key);
            }

            while (_items.Count > 0 &&
                   (_items.Count >= _maxEntries || _totalBytes + size > _maxBytes))
            {
                RemoveNode(_lru.Last);
            }

            var item = new CacheItem(key, entry, size);
            var node = _lru.AddFirst(item);
            _items.Add(key, node);
            _totalBytes += size;
            return true;
        }
    }

    public void Dispose()
    {
        List<string> directories;

        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            directories = new List<string>(_items.Count);
            foreach (var node in _lru)
                directories.Add(node.Entry.TemporaryDirectory);

            _items.Clear();
            _lru.Clear();
            _totalBytes = 0;
        }

        foreach (var directory in directories)
        {
            ArchiveEntryExtractor.TryDeleteDirectory(directory);
            ArchiveEntryExtractor.DeleteDirectoryWhenUnused(directory);
        }
    }

    private void RemoveNode(LinkedListNode<CacheItem> node)
    {
        if (node == null)
            return;

        _items.Remove(node.Value.Key);
        _lru.Remove(node);
        _totalBytes -= node.Value.Size;
        ArchiveEntryExtractor.DeleteDirectoryWhenUnused(
            node.Value.Entry.TemporaryDirectory);
    }

    private static long GetEntrySize(ExtractedArchiveEntry entry)
    {
        try
        {
            return Math.Max(0L, new System.IO.FileInfo(entry.FilePath).Length);
        }
        catch
        {
            return 0L;
        }
    }

    private static string BuildKey(
        string archivePath,
        string entryPath,
        ulong expectedSize)
    {
        return string.Concat(
            System.IO.Path.GetFullPath(archivePath),
            "\n",
            ArchiveEntryExtractor.NormalizeArchivePath(entryPath),
            "\n",
            expectedSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
