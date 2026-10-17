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

using QuickLook.Common.Plugin;
using QuickLook.Plugin.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace QuickLook.Plugin.VideoViewer;

public sealed class Plugin : IViewer, IPreviewQuiesce
{
    private const int MaxProbeEntries = 64;
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromMilliseconds(80);

    private static readonly object ProbeLock = new();
    private static readonly Dictionary<string, Task<MediaInfoSnapshot>> ProbeTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxSnapshotEntries = 48;
    private const long MaxSnapshotBytes = 8L * 1024 * 1024;
    private const long MaxSingleSnapshotBytes = 4L * 1024 * 1024;
    private static readonly BoundedFileCache<MediaInfoSnapshot> SnapshotCache =
        new(MaxSnapshotEntries, MaxSnapshotBytes, EstimateSnapshotBytes, MaxSingleSnapshotBytes);

    // The header result is much cheaper than a MediaInfo snapshot and lets
    // CanHandle reject text, images and archives before the native open. It is
    // kept separately and bounded so a long preview session cannot grow it.
    private const int MaxHeaderEntries = 256;
    private static readonly BoundedFileCache<MediaHeaderKind> HeaderCache =
        new(MaxHeaderEntries, MaxHeaderEntries, static _ => 1);

    // An extension tells us which panel to build without opening the file.
    // The actual streams are still checked in the background: a container can
    // be audio-only or video-only even when its extension is usually video.
    private static readonly HashSet<string> KnownVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".mpg", ".mpeg", ".m4v",
        ".3gp", ".m2ts", ".mts", ".vob", ".ogv", ".m2v", ".mpv", ".divx",
    };

    private static readonly HashSet<string> KnownAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma",
        ".mid", ".midi", ".ape", ".wv", ".aiff", ".amr", ".mka", ".mp2",
    };

    private static readonly HashSet<string> KnownMediaExtensions =
        [.. KnownVideoExtensions, .. KnownAudioExtensions];

    // v1.3.0: CanHandle used to run a native MediaInfo sniff on every file
    // whose extension is not a known media one - including txt/md/json/zip
    // that another plugin is guaranteed to claim anyway. That open ran on the
    // UI thread for every distinct previewed file. Skip the sniff for
    // extensions that are definitely handled elsewhere; genuinely unusual
    // media extensions (e.g. .ts, .rm, .asf) now go through a header check.
    // Deliberately NOT included: ".ts" (TypeScript vs MPEG-TS), ".m3u".
    private static readonly HashSet<string> NonMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // text / code / data (TextViewer, MarkdownViewer, CsvViewer)
        ".txt", ".log", ".md", ".markdown", ".mdown", ".json", ".xml", ".csv", ".tsv", ".psv",
        ".ini", ".cfg", ".config", ".yaml", ".yml", ".toml", ".ipynb", ".adoc", ".rst",
        // office (OfficeViewer)
        ".doc", ".docx", ".docm", ".dotx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".pptm",
        ".odt", ".ods", ".odp", ".rtf", ".vsd", ".vsdx",
        // archives (ArchiveViewer)
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".cbz", ".cbr", ".jar", ".apk", ".msi",
        // images (ImageViewer)
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".svg", ".ico",
        ".psd", ".raw", ".heic", ".jxl", ".jxr", ".dcm", ".dicom", ".cur", ".ani", ".icns", ".clip",
        // fonts (FontViewer)
        ".ttf", ".otf", ".woff", ".woff2", ".ttc",
        // pdf / html / app packages / executables
        ".pdf", ".html", ".htm", ".url", ".appx", ".appxbundle", ".msix", ".msixbundle",
        ".deb", ".rpm", ".ipa", ".dmg", ".appimage", ".hap", ".wgt", ".exe", ".dll", ".qlplugin",
    };

    // Playlists are text, but their target is only known to MediaInfo. Keep
    // the old sniff for these; the extension is unambiguous and there are far
    // fewer playlists than ordinary text files.
    private static readonly HashSet<string> PlaylistExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u", ".m3u8", ".pls", ".xspf", ".asx", ".wax", ".wvx", ".cue",
    };

    private ViewerPanel _vp;

    public int Priority => -3;

    public void Init()
    {
        // Remove legacy LAV hardware acceleration settings
        // https://github.com/QL-Win/QuickLook/issues/1928
#if false
        QLVRegistry.Register();
#endif
    }

    public bool CanHandle(string path)
    {
        if (Directory.Exists(path))
            return false;

        var extension = Path.GetExtension(path);
        if (KnownMediaExtensions.Contains(extension))
        {
            // The loaded plugin instance that answers CanHandle is not the
            // instance that will run Prepare/View (the host creates one per
            // preview), so the probe is kept in a static table keyed by path.
            // It gives Prepare a chance to size the window from the real
            // dimensions without ever waiting for a slow disk on the UI thread.
            try
            {
                EnsureProbe(path);
            }
            catch
            {
                // A failed prefetch must not turn a known video/audio
                // extension into "this plugin cannot handle the file".
            }

            return true;
        }

        // Extension is definitely handled by another plugin - do not spend
        // even a header read on a file we cannot claim.
        if (NonMediaExtensions.Contains(extension))
            return false;

        var stamp = GetFileStamp(path);

        if (PlaylistExtensions.Contains(extension))
            return SniffUnknownPath(path, stamp);

        var headerKind = GetHeaderKind(path, stamp);

        if (headerKind == MediaHeaderKind.NotMedia)
            return false;

        // A recognised media signature lets the preview start immediately and
        // the real probe finish in the background, exactly like a known
        // extension. This is what keeps .ts / .rm / .asf from blocking the
        // shell thread while MediaInfo opens them.
        if (headerKind is MediaHeaderKind.Audio or MediaHeaderKind.Video or MediaHeaderKind.Container)
        {
            try
            {
                EnsureProbe(path);
            }
            catch
            {
                // A failed prefetch must not turn a media-looking file into
                // "this plugin cannot handle the file".
            }

            return true;
        }

        // Binary without a known media signature. MediaInfo gets the final
        // word once; a confirmed "no streams" answer is remembered so the
        // next preview of the same unchanged file does not repeat the native
        // open.
        return SniffUnknownPath(path, stamp);
    }

    private static bool SniffUnknownPath(string path, FileCacheStamp stamp)
    {
        var info = GetInfo(path);
        if (info is { HasVideo: false, HasAudio: false })
            RememberNotMedia(path, stamp);

        return info is not null && (info.HasVideo || info.HasAudio);
    }

    public void Prepare(string path, ContextObject context)
    {
        // v1.2.15: the video panel shows a thumbnail while the media opens, so
        // the busy spinner is not needed for video previews.
        context.ShowBusyIndicator = false;

        var info = ResolvePreparedInfo(path);
        if (info is { HasVideo: true }) // video
        {
            // When the header read is still in flight, start with the common
            // 16:9 video size rather than a narrow 1366x768 window. On a
            // typical 1080p screen the placeholder and the real window then
            // have the same size, so the preview does not visibly jump when
            // the probe finishes.
            var windowSize = new Size
            {
                Width = Math.Max(100, info.Width == 0 ? 1920 : info.Width),
                Height = Math.Max(100, info.Height == 0 ? 1080 : info.Height)
            };

            if (info.Rotation % 180 != 0)
                windowSize = new Size(windowSize.Height, windowSize.Width);

            context.SetPreferredSizeFit(windowSize, 0.8);

            context.TitlebarAutoHide = true;
            context.Theme = Themes.Dark;
            context.TitlebarBlurVisibility = true;
            context.TitlebarColourVisibility = true; // keep the video glass bar look
        }
        else // audio
        {
            context.PreferredSize = new Size(500, 300);

            context.CanResize = false;
            context.TitlebarAutoHide = false;
            context.TitlebarBlurVisibility = false;
            context.TitlebarColourVisibility = false;
        }

        context.TitlebarOverlap = true;
    }

    public void View(string path, ContextObject context)
    {
        var info = ResolveViewInfo(path);

        _vp = new ViewerPanel(context);

        context.ViewerContent = _vp;

        context.Title = $"{Path.GetFileName(path)}";

        _vp.LoadAndPlay(path, info);

        // A guessed snapshot is enough to build the right panel and start the
        // thumbnail/playback immediately. The panel applies the real metadata
        // (rotation, window size, tags) when the background probe finishes.
        if (info is null || info.IsEstimated)
            _vp.ObserveDeferredInfo(path, EnsureProbe(path));
    }

    public void Cleanup()
    {
        _vp?.Dispose();
        _vp = null;
    }

    public void Quiesce()
    {
        _vp?.Quiesce();
    }

    /// <summary>
    /// v1.2.13: returns cached media info for a path, sniffing (and caching) on
    /// first use. The MediaInfo native open is the expensive part of video
    /// matching; caching it makes switching between media files much faster.
    /// </summary>
    private static MediaInfoSnapshot GetInfo(string path)
    {
        if (TryGetCachedInfo(path, out var cached))
            return cached;

        var hasStamp = FileCacheStamp.TryCapture(path, out var stamp);
        var info = Sniff(path);
        if (info != null && hasStamp)
            SnapshotCache.Set(path, stamp, info);

        return info;
    }

    private static bool TryGetCachedInfo(string path, out MediaInfoSnapshot info)
    {
        if (FileCacheStamp.TryCapture(path, out var stamp))
            return SnapshotCache.TryGet(path, stamp, out info);

        info = null;
        return false;
    }

    /// <summary>
    /// Returns an in-flight probe for a media file, starting one if needed.
    /// The task is kept in a bounded static table because the plugin instance
    /// that matches the file is not the one that renders it.
    /// </summary>
    private static Task<MediaInfoSnapshot> EnsureProbe(string path)
    {
        if (TryGetCachedInfo(path, out var cached))
            return Task.FromResult(cached);

        lock (ProbeLock)
        {
            if (ProbeTasks.TryGetValue(path, out var running))
                return running;

            // Bounded so a long preview session cannot keep every path alive.
            // Waiters already hold their own task reference.
            if (ProbeTasks.Count >= MaxProbeEntries)
                ProbeTasks.Clear();

            var probe = Task.Run(() => GetInfo(path));
            ProbeTasks[path] = probe;

            // A completed task has already put its answer in SnapshotCache (or
            // failed, which is not cached). Keeping the task forever only holds
            // another reference to the result and the exception, so drop it as
            // soon as the preview that asked for it has been handed the task.
            _ = probe.ContinueWith(
                static (completed, state) =>
                {
                    var (taskPath, task) = ((string Path, Task<MediaInfoSnapshot> Task))state!;
                    lock (ProbeLock)
                    {
                        if (ProbeTasks.TryGetValue(taskPath, out var current) &&
                            ReferenceEquals(current, task))
                        {
                            ProbeTasks.Remove(taskPath);
                        }
                    }
                },
                (path, probe),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return probe;
        }
    }

    private static Task<MediaInfoSnapshot> GetProbe(string path)
    {
        lock (ProbeLock)
            return ProbeTasks.TryGetValue(path, out var probe) ? probe : null;
    }

    private static FileCacheStamp GetFileStamp(string path)
    {
        return FileCacheStamp.TryCapture(path, out var stamp) ? stamp : default;
    }

    private static MediaHeaderKind GetHeaderKind(string path, FileCacheStamp stamp)
    {
        if (HeaderCache.TryGet(path, stamp, out var cached))
            return cached;

        var kind = MediaHeaderSniffer.Probe(path);
        HeaderCache.Set(path, stamp, kind);

        return kind;
    }

    private static void RememberNotMedia(string path, FileCacheStamp stamp)
    {
        HeaderCache.Set(path, stamp, MediaHeaderKind.NotMedia);
    }

    private static MediaInfoSnapshot CompletedProbe(Task<MediaInfoSnapshot> probe)
    {
        if (probe is not { IsCompletedSuccessfully: true })
            return null;

        try
        {
            return probe.Result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Called before the window is positioned. A completed header read is used
    /// when it is already available; a short, bounded wait catches the common
    /// case where the probe started during CanHandle and is almost done. A
    /// slow file falls back to a 16:9 placeholder instead of freezing the UI.
    /// </summary>
    private static MediaInfoSnapshot ResolvePreparedInfo(string path)
    {
        if (TryGetCachedInfo(path, out var cached))
            return cached;

        var extension = Path.GetExtension(path);
        var headerKind = MediaHeaderKind.Unknown;

        if (!KnownMediaExtensions.Contains(extension))
        {
            headerKind = GetHeaderKind(path, GetFileStamp(path));
            if (headerKind == MediaHeaderKind.NotMedia)
                return null;
        }

        var probe = EnsureProbe(path);
        if (!probe.IsCompleted)
        {
            try
            {
                probe.Wait(ProbeBudget);
            }
            catch
            {
                // CompletedProbe below treats a failed probe as unavailable.
            }
        }

        return CompletedProbe(probe) ?? GuessInfo(extension, headerKind);
    }

    private static MediaInfoSnapshot ResolveViewInfo(string path)
    {
        if (TryGetCachedInfo(path, out var cached))
            return cached;

        var extension = Path.GetExtension(path);
        var headerKind = MediaHeaderKind.Unknown;

        if (!KnownMediaExtensions.Contains(extension))
        {
            headerKind = GetHeaderKind(path, GetFileStamp(path));
            if (headerKind == MediaHeaderKind.NotMedia)
                return null;
        }

        return CompletedProbe(GetProbe(path)) ?? GuessInfo(extension, headerKind);
    }

    private static MediaInfoSnapshot GuessInfo(
        string extension,
        MediaHeaderKind headerKind = MediaHeaderKind.Unknown)
    {
        var hasVideo = KnownVideoExtensions.Contains(extension) ||
                       headerKind is MediaHeaderKind.Video or MediaHeaderKind.Container;
        var isMidi = string.Equals(extension, ".mid", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(extension, ".midi", StringComparison.OrdinalIgnoreCase);

        return new MediaInfoSnapshot(
            HasVideo: hasVideo,
            HasAudio: true,
            AudioCodec: isMidi ? "MIDI" : null,
            Width: 0,
            Height: 0,
            Rotation: 0d,
            Title: null,
            Artist: null,
            Album: null,
            CoverData: null,
            Lyrics: null,
            IsEstimated: true);
    }

    private static long EstimateSnapshotBytes(MediaInfoSnapshot info)
    {
        const long overhead = 256;
        return overhead +
               Utf16Bytes(info.AudioCodec) +
               Utf16Bytes(info.Title) +
               Utf16Bytes(info.Artist) +
               Utf16Bytes(info.Album) +
               Utf16Bytes(info.CoverData) +
               Utf16Bytes(info.Lyrics);
    }

    private static long Utf16Bytes(string value)
        => string.IsNullOrEmpty(value) ? 0 : ((long)value.Length + 1) * 2;

    private static MediaInfoSnapshot Sniff(string path)
    {
        try
        {
            var probe = MediaInfoSession.Read(path);
            if (probe is null)
                return null;

            return new MediaInfoSnapshot(
                probe.HasVideo,
                probe.HasAudio,
                probe.AudioCodec,
                probe.Width,
                probe.Height,
                probe.Rotation,
                probe.Title,
                probe.Artist,
                probe.Album,
                probe.CoverData,
                probe.Lyrics);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Immutable snapshot of the MediaInfo data a preview needs, so repeated
/// previews of the same file never re-open the native MediaInfo library.
/// </summary>
public sealed record MediaInfoSnapshot(
    bool HasVideo,
    bool HasAudio,
    string AudioCodec,
    int Width,
    int Height,
    double Rotation,
    string Title,
    string Artist,
    string Album,
    string CoverData,
    string Lyrics,
    bool IsEstimated = false);
