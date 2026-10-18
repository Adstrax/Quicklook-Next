// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Common.ExtensionMethods;
using QuickLook.Common.Helpers;
using QuickLook.Plugin.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.VideoViewer;

/// <summary>
/// Starts the shell thumbnail while the plugin match is still being resolved,
/// before the window is shown. The preview used to start this work only after
/// View, so the first video spent ~250 ms on a dark surface before its
/// thumbnail arrived. The cache is small and bounded: a frozen 640x360 image
/// is about 0.9 MB, and files are re-extracted when their stamp changes.
/// </summary>
internal static class ThumbnailPrefetch
{
    private const int MaxEntries = 4;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, (FileCacheStamp Stamp, Task<BitmapSource> Task)> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts (or joins) the extraction for a video file.</summary>
    internal static void Ensure(string path)
    {
        _ = GetOrStart(path);
    }

    /// <summary>Returns the thumbnail task so the panel can display it when ready.</summary>
    internal static Task<BitmapSource> GetOrStart(string path)
    {
        if (!FileCacheStamp.TryCapture(path, out var stamp))
            return Task.FromResult<BitmapSource>(null);

        lock (Sync)
        {
            if (Entries.TryGetValue(path, out var existing))
            {
                if (existing.Stamp == stamp &&
                    !existing.Task.IsFaulted &&
                    !existing.Task.IsCanceled)
                {
                    return existing.Task;
                }

                Entries.Remove(path);
            }

            if (Entries.Count >= MaxEntries)
            {
                // Completed entries are the cheapest to lose; if every slot is
                // still in flight, clear the table instead of growing without a
                // bound. Four ordinary thumbnails are well under 4 MB.
                foreach (var key in Entries
                             .Where(entry => entry.Value.Task.IsCompleted)
                             .Select(entry => entry.Key)
                             .Take(2)
                             .ToList())
                {
                    Entries.Remove(key);
                }

                if (Entries.Count >= MaxEntries)
                    Entries.Clear();
            }

            var task = Task.Run(() => Extract(path));
            Entries[path] = (stamp, task);
            return task;
        }
    }

    private static BitmapSource Extract(string path)
    {
        try
        {
            var scale = DisplayDeviceHelper.GetCurrentScaleFactor();
            using var thumb = WindowsThumbnailProvider.GetThumbnail(
                path,
                (int)(640 * scale.Horizontal),
                (int)(360 * scale.Vertical),
                ThumbnailOptions.ScaleUp);

            if (thumb == null)
                return null;

            var source = thumb.ToBitmapSource();
            source.Freeze();
            return source;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return null;
        }
    }
}
