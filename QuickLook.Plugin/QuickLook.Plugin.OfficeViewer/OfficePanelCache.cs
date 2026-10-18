// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Plugin.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v6.1.2: keeps the expensive result of reading a Word document or PowerPoint
/// deck between previews. Side-button switching used to parse the same file again
/// every time it came back on screen.
/// <para>
/// Entries are keyed by the full path and validated against the file's length and
/// timestamp, so editing or replacing a document cannot serve the old page. The
/// cache stores its own copy of the pictures; a panel always receives a fresh
/// folder it can delete when it closes, which keeps cache eviction independent of
/// whichever preview happens to be visible.
/// </para>
/// </summary>
internal static class OfficePanelCache
{
    private const int MaxEntries = 8;
    private const long MaxBytes = 96L * 1024 * 1024;
    private const long MaxSingleBytes = 48L * 1024 * 1024;

    private static readonly BoundedFileCache<OfficePanelContent> Cache =
        new(MaxEntries, MaxBytes, EstimateSize, MaxSingleBytes, OnEvicted);

    private static readonly object PendingSync = new();
    private static readonly Dictionary<string, TaskCompletionSource<OfficePanelContent>> Pending =
        new(StringComparer.OrdinalIgnoreCase);

    private static int _waiting;

    internal static int WaitingCountForTests => Volatile.Read(ref _waiting);

    /// <summary>
    /// v6.1.3: false while the host is in low memory mode. Panels still parse
    /// into their own folder, but nothing is published for a later preview, so
    /// closing the preview really does return the memory.
    /// </summary>
    internal static bool RetainAcrossPreviews { get; set; } = true;

    /// <summary>
    /// Returns a panel-owned result. <paramref name="build"/> receives a fresh
    /// folder for this preview's pictures and runs at most once per file version
    /// even when two previews request it at the same time.
    /// </summary>
    internal static OfficePanelContent Get(
        string path,
        Func<string, OfficePanelContent> build)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (build is null)
            throw new ArgumentNullException(nameof(build));

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return BuildUncached(path, build);
        }

        if (!FileCacheStamp.TryCapture(fullPath, out var stamp))
            return BuildUncached(fullPath, build);

        if (Cache.TryGet(fullPath, stamp, out var cached))
        {
            var copy = CopyForPanel(cached);
            if (copy is not null)
                return copy;

            Cache.Remove(fullPath);
        }

        var pendingKey =
            $"{fullPath}\n{stamp.Length}\n{stamp.LastWriteTimeUtcTicks}";
        TaskCompletionSource<OfficePanelContent> completion;
        var leader = false;

        lock (PendingSync)
        {
            if (!Pending.TryGetValue(pendingKey, out completion))
            {
                completion = new TaskCompletionSource<OfficePanelContent>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Pending[pendingKey] = completion;
                leader = true;
            }
        }

        if (!leader)
        {
            Interlocked.Increment(ref _waiting);
            try
            {
                var shared = completion.Task.GetAwaiter().GetResult();
                if (shared is null)
                    return null;

                // The leader keeps the folder it parsed into. Joining callers
                // must not hand the same directory to a second panel: the first
                // panel to close would delete the other panel's pictures.
                return CopyForPanel(shared) ?? BuildUncached(fullPath, build);
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }

        try
        {
            var folder = OfficeImageStore.CreateFolder();
            OfficePanelContent content;
            try
            {
                content = build(folder);
            }
            catch
            {
                OfficeImageStore.Cleanup(folder);
                throw;
            }

            if (content is null)
            {
                OfficeImageStore.Cleanup(folder);
                completion.TrySetResult(null);
                return null;
            }

            Publish(fullPath, stamp, content);
            completion.TrySetResult(content);
            return content;
        }
        catch (Exception e)
        {
            completion.TrySetException(e);
            throw;
        }
        finally
        {
            lock (PendingSync)
                Pending.Remove(pendingKey);
        }
    }

    private static OfficePanelContent CopyForPanel(OfficePanelContent content)
    {
        if (content is null || string.IsNullOrEmpty(content.ImageFolder))
            return content;

        var folder = OfficeImageStore.CreateFolder();
        if (OfficeImageStore.TryCloneFiles(content.ImageFolder, folder))
            return content.WithImageFolder(folder);

        OfficeImageStore.Cleanup(folder);
        return null;
    }

    private static OfficePanelContent BuildUncached(
        string path,
        Func<string, OfficePanelContent> build)
    {
        var folder = OfficeImageStore.CreateFolder();
        try
        {
            var content = build(folder);
            if (content is null)
                OfficeImageStore.Cleanup(folder);
            return content;
        }
        catch
        {
            OfficeImageStore.Cleanup(folder);
            throw;
        }
    }

    /// <summary>
    /// Publishes a private copy of the parse result. The original folder stays
    /// owned by the panel, so it follows the existing disposal path.
    /// </summary>
    private static void Publish(
        string path,
        FileCacheStamp stamp,
        OfficePanelContent content)
    {
        if (!RetainAcrossPreviews)
            return;

        // Do not cache a file that changed while it was being parsed.
        if (!FileCacheStamp.TryCapture(path, out var current) || current != stamp)
            return;

        string cacheFolder = null;
        OfficePanelContent cached;

        if (HasFiles(content.ImageFolder))
        {
            cacheFolder = OfficeImageStore.CreateCacheFolder();
            if (!OfficeImageStore.TryCloneFiles(content.ImageFolder, cacheFolder))
            {
                OfficeImageStore.Cleanup(cacheFolder);
                return;
            }

            cached = content.WithImageFolder(cacheFolder);
        }
        else
        {
            // The HTML is still worth keeping, and a page without pictures
            // does not need an image directory.
            cached = content.WithImageFolder(null);
        }

        if (!Cache.Set(path, stamp, cached) && cacheFolder is not null)
            OfficeImageStore.Cleanup(cacheFolder);
    }

    private static bool HasFiles(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return false;

        try
        {
            return Directory.EnumerateFiles(folder).Any();
        }
        catch
        {
            return false;
        }
    }

    private static long EstimateSize(OfficePanelContent content)
    {
        if (content is null)
            return 0;

        long bytes = Encoding.UTF8.GetByteCount(content.Html ?? string.Empty);

        foreach (var entry in content.Navigation ?? [])
        {
            bytes += Encoding.UTF8.GetByteCount(entry.Label ?? string.Empty);
            bytes += Encoding.UTF8.GetByteCount(entry.Target ?? string.Empty);
        }

        if (!string.IsNullOrEmpty(content.ImageFolder))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(content.ImageFolder))
                    bytes += new FileInfo(file).Length;
            }
            catch (Exception)
            {
                // The estimate is advisory; the HTML is still a useful size.
            }
        }

        return Math.Max(1, bytes);
    }

    private static void OnEvicted(OfficePanelContent content)
        => OfficeImageStore.Cleanup(content?.ImageFolder);

    /// <summary>Drops every parsed panel and releases its picture folder.</summary>
    internal static void Clear()
    {
        Cache.Clear();
        lock (PendingSync)
            Pending.Clear();
        Interlocked.Exchange(ref _waiting, 0);
    }

    internal static void ResetForTests()
    {
        RetainAcrossPreviews = true;
        Clear();
    }
}
