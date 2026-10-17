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

using QuickLook.MediaInfo;
using QuickLook.MediaInfo.Core;
using System;
using System.IO;
using System.Threading;

namespace QuickLook.Plugin.Shared;

/// <summary>
/// The MediaInfo wrapper can install its native-library resolver only once per
/// process. Both the video viewer and the media-info viewer need the library, so
/// this shared service owns the single anchor instance and creates an
/// independent handle for each read. Handles are cheap and can be scanned
/// concurrently without sharing mutable state.
/// </summary>
public static class MediaInfoSession
{
    private static readonly Lazy<MediaInfoNative> Anchor = new(() => new MediaInfoNative(NativeDirectory));

    public static MediaInfoProbe Read(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var lib = CreateHandle();
        lib.Option("Cover_Data", "base64");
        lib.Open(path);

        var videoCodec = lib.Get(StreamKind.Video, 0, "Format");
        var audioCodec = lib.Get(StreamKind.Audio, 0, "Format");

        if (videoCodec == "Unable to load MediaInfo library")
            return null;

        int.TryParse(lib.Get(StreamKind.Video, 0, "Width"), out var width);
        int.TryParse(lib.Get(StreamKind.Video, 0, "Height"), out var height);
        double.TryParse(lib.Get(StreamKind.Video, 0, "Rotation"), out var rotation);

        // Some machines report a rotation such as 90000 instead of 90.
        if (rotation > 360)
            rotation /= 1e3;

        var hasVideo = !string.IsNullOrWhiteSpace(videoCodec);
        var hasAudio = !string.IsNullOrWhiteSpace(audioCodec);

        // Tags and cover art are only used by the audio panel. Skipping them
        // for video keeps the first preview fast.
        return hasVideo
            ? new MediaInfoProbe(hasVideo, hasAudio, videoCodec, audioCodec,
                width, height, rotation, null, null, null, null, null)
            : new MediaInfoProbe(hasVideo, hasAudio, videoCodec, audioCodec,
                width, height, rotation,
                lib.Get(StreamKind.General, 0, "Title"),
                lib.Get(StreamKind.General, 0, "Performer"),
                lib.Get(StreamKind.General, 0, "Album"),
                lib.Get(StreamKind.General, 0, "Cover_Data"),
                lib.Get(StreamKind.General, 0, "Lyrics"));
    }

    public static string ReadText(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var lib = CreateHandle();
        lib.Option("Cover_Data", string.Empty);
        lib.Open(path);
        return lib.Inform();
    }

    private static MediaInfoHandle CreateHandle()
    {
        // Touch the anchor first: its constructor loads the native library and
        // installs the resolver used by every handle.
        _ = Anchor.Value;
        return new MediaInfoHandle();
    }

    private static string NativeDirectory
    {
        get
        {
            var rid = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            var baseDirectory = AppContext.BaseDirectory;
            var assemblyDirectory = Path.GetDirectoryName(typeof(MediaInfoSession).Assembly.Location)
                                    ?? baseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDirectory, "QuickLook.Plugin", "QuickLook.Plugin.VideoViewer",
                    "runtimes", rid, "native"),
                Path.Combine(assemblyDirectory, "QuickLook.Plugin", "QuickLook.Plugin.VideoViewer",
                    "runtimes", rid, "native"),
                Path.Combine(assemblyDirectory,
                    "runtimes", rid, "native"),
                Path.Combine(baseDirectory, "runtimes", rid, "native"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, "MediaInfo.dll")))
                    return candidate;
            }

            return candidates[0];
        }
    }
}

public sealed record MediaInfoProbe(
    bool HasVideo,
    bool HasAudio,
    string VideoCodec,
    string AudioCodec,
    int Width,
    int Height,
    double Rotation,
    string Title,
    string Artist,
    string Album,
    string CoverData,
    string Lyrics);
