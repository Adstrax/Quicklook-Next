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
using QuickLook.MediaInfo;
using QuickLook.MediaInfo.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;

namespace QuickLook.Plugin.VideoViewer;

public sealed class Plugin : IViewer, IPreviewQuiesce
{
    // v3.3.0: load MediaInfo natively on first actual use instead of in the
    // static ctor. Creating the plugin instance during startup used to force
    // MediaInfo.dll (~8 MB) into every resident process even when no media
    // file was ever previewed.
    private static readonly Lazy<MediaInfoNative> _mediaInfo = new(() =>
    {
        var lib = new MediaInfoNative(Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
            Environment.Is64BitProcess ? @"runtimes\win-x64\native\" : @"runtimes\win-x86\native\"));
        lib.Option("Cover_Data", "base64");
        return lib;
    });
    private static readonly Dictionary<string, MediaInfoSnapshot> SnapshotCache = [];
    private const int MaxCacheEntries = 48;

    // v1.2.13: unambiguous media extensions skip the MediaInfo sniff in
    // CanHandle, so matching a video/audio file is instant instead of opening
    // the native library on the UI thread for every preview.
    private static readonly HashSet<string> KnownMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".mpg", ".mpeg", ".m4v",
        ".3gp", ".m2ts", ".mts", ".vob", ".ogv", ".m2v", ".mpv", ".divx",
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma",
        ".mid", ".midi", ".ape", ".wv", ".aiff", ".amr", ".mka", ".mp2",
    };

    // v1.3.0: CanHandle used to run a native MediaInfo sniff on every file
    // whose extension is not a known media one - including txt/md/json/zip
    // that another plugin is guaranteed to claim anyway. That open ran on the
    // UI thread for every distinct previewed file. Skip the sniff for
    // extensions that are definitely handled elsewhere; genuinely unusual
    // media extensions (e.g. .ts, .rm, .asf) still go through the sniff.
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
            return true;

        // Extension is definitely handled by another plugin - sniffing would
        // only cost a native open on the UI thread for a file we cannot claim.
        if (NonMediaExtensions.Contains(extension))
            return false;

        var info = GetInfo(path);
        return info is not null && (info.HasVideo || info.HasAudio);
    }

    public void Prepare(string path, ContextObject context)
    {
        // v1.2.15: the video panel shows a thumbnail while the media opens, so
        // the busy spinner is not needed for video previews.
        context.ShowBusyIndicator = false;

        var info = GetInfo(path);
        if (info is { HasVideo: true }) // video
        {
            var windowSize = new Size
            {
                Width = Math.Max(100, info.Width == 0 ? 1366 : info.Width),
                Height = Math.Max(100, info.Height == 0 ? 768 : info.Height)
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
        _vp = new ViewerPanel(context);

        context.ViewerContent = _vp;

        context.Title = $"{Path.GetFileName(path)}";

        _vp.LoadAndPlay(path, GetInfo(path));
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
        lock (SnapshotCache)
        {
            if (SnapshotCache.TryGetValue(path, out var cached))
                return cached;
        }

        var info = Sniff(path);
        if (info != null)
        {
            lock (SnapshotCache)
            {
                SnapshotCache[path] = info;
                if (SnapshotCache.Count > MaxCacheEntries)
                    SnapshotCache.Clear();
            }
        }

        return info;
    }

    private static MediaInfoSnapshot Sniff(string path)
    {
        try
        {
            var lib = _mediaInfo.Value;
            lib.Open(path);
            var videoCodec = lib.Get(StreamKind.Video, 0, "Format");
            var audioCodec = lib.Get(StreamKind.Audio, 0, "Format");

            if (videoCodec == "Unable to load MediaInfo library") // should not happen
                return null;

            int.TryParse(lib.Get(StreamKind.Video, 0, "Width"), out var width);
            int.TryParse(lib.Get(StreamKind.Video, 0, "Height"), out var height);
            double.TryParse(lib.Get(StreamKind.Video, 0, "Rotation"), out var rotation);

            // Correct rotation: on some machine the value "90" becomes "90000" by some reason
            if (rotation > 360)
                rotation /= 1e3;

            var hasVideo = !string.IsNullOrWhiteSpace(videoCodec);
            var hasAudio = !string.IsNullOrWhiteSpace(audioCodec);

            // The remaining metadata (tags/cover art) is only used by the audio
            // info panel; skip it for videos to keep the first preview fast.
            if (hasVideo)
                return new MediaInfoSnapshot(true, hasAudio, audioCodec, width, height,
                    rotation, null, null, null, null, null);

            return new MediaInfoSnapshot(false, hasAudio, audioCodec, width, height, rotation,
                lib.Get(StreamKind.General, 0, "Title"),
                lib.Get(StreamKind.General, 0, "Performer"),
                lib.Get(StreamKind.General, 0, "Album"),
                lib.Get(StreamKind.General, 0, "Cover_Data"),
                lib.Get(StreamKind.General, 0, "Lyrics"));
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
    string Lyrics);
