// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;

namespace QuickLook.Plugin.Shared;

/// <summary>
/// Identifies one version of a file without opening it. A preview cache keyed
/// only by path would serve the old metadata after the file is replaced or
/// edited in place.
/// </summary>
public readonly record struct FileCacheStamp(long Length, long LastWriteTimeUtcTicks)
{
    /// <summary>Captures the current stamp, or returns false when the file is unavailable.</summary>
    public static bool TryCapture(string path, out FileCacheStamp stamp)
    {
        stamp = default;
        if (string.IsNullOrEmpty(path))
            return false;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;

            stamp = new FileCacheStamp(info.Length, info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// v6.0.1: a small LRU cache for values derived from files.
/// <para>
/// The video viewer used plain dictionaries and cleared the whole table when
/// it reached a fixed entry count. That lost every useful answer at once, kept
/// entries for files that had since changed, and retained completed probe tasks
/// until the next flush. This cache validates a file stamp on every lookup,
/// moves a hit to the front, and evicts one entry at a time under both an entry
/// and a byte budget. Callers provide the byte estimate because only they know
/// whether a value is a small enum or carries cover art and lyrics.
/// </para>
/// <para>All operations are thread safe; values are handed out as references.</para>
/// </summary>
public sealed class BoundedFileCache<T>
{
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _lru = [];
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly long _maxSingleBytes;
    private readonly Func<T, long> _sizeOf;

    private long _bytes;

    /// <param name="maxEntries">Maximum number of live entries.</param>
    /// <param name="maxBytes">Maximum sum of the sizes reported by <paramref name="sizeOf"/>.</param>
    /// <param name="sizeOf">Approximate retained size of one value, in bytes.</param>
    /// <param name="maxSingleBytes">
    /// Values larger than this are used for the current preview but not cached;
    /// one huge value must not evict every ordinary one.
    /// </param>
    public BoundedFileCache(
        int maxEntries,
        long maxBytes,
        Func<T, long> sizeOf,
        long maxSingleBytes = long.MaxValue)
    {
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (sizeOf is null)
            throw new ArgumentNullException(nameof(sizeOf));
        if (maxSingleBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSingleBytes));

        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
        _sizeOf = sizeOf;
        _maxSingleBytes = maxSingleBytes;
    }

    public int Count
    {
        get
        {
            lock (_sync)
                return _map.Count;
        }
    }

    public long ApproximateBytes
    {
        get
        {
            lock (_sync)
                return _bytes;
        }
    }

    /// <summary>
    /// Returns the cached value when the file still has the same stamp. A stale
    /// entry is removed immediately so it cannot be returned by a later lookup.
    /// </summary>
    public bool TryGet(string path, FileCacheStamp stamp, out T value)
    {
        value = default;
        if (string.IsNullOrEmpty(path))
            return false;

        lock (_sync)
        {
            if (!_map.TryGetValue(path, out var node))
                return false;

            if (node.Value.Stamp != stamp)
            {
                RemoveNode(node);
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    /// <summary>
    /// Stores or replaces an entry. An oversized value is deliberately skipped:
    /// caching it would evict all ordinary entries for no net benefit.
    /// </summary>
    public void Set(string path, FileCacheStamp stamp, T value)
    {
        if (string.IsNullOrEmpty(path) || value is null)
            return;

        var size = Math.Max(1, _sizeOf(value));
        if (size > _maxSingleBytes || size > _maxBytes)
            return;

        lock (_sync)
        {
            if (_map.TryGetValue(path, out var existing))
                RemoveNode(existing);

            var node = new LinkedListNode<Entry>(new Entry(path, stamp, value, size));
            _map[path] = node;
            _lru.AddFirst(node);
            _bytes += size;

            while ((_lru.Count > _maxEntries || _bytes > _maxBytes) && _lru.Last is { } last)
                RemoveNode(last);
        }
    }

    public void Remove(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        lock (_sync)
        {
            if (_map.TryGetValue(path, out var node))
                RemoveNode(node);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }

    private void RemoveNode(LinkedListNode<Entry> node)
    {
        _bytes = Math.Max(0, _bytes - node.Value.Size);
        _map.Remove(node.Value.Path);
        _lru.Remove(node);
    }

    private readonly record struct Entry(
        string Path,
        FileCacheStamp Stamp,
        T Value,
        long Size);
}
