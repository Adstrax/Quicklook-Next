// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Diagnostics;
using System.IO;

namespace QuickLook.Plugin.VideoViewer;

/// <summary>
/// Diagnostic-only timing for the video pipeline. Enabled by the measurement
/// script through QL_VIDEO_TIMING=1; nothing is written in a normal run.
/// </summary>
internal static class VideoTiming
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("QL_VIDEO_TIMING") == "1";

    private static readonly string Directory =
        Environment.GetEnvironmentVariable("QL_SMOKE_DIR");

    internal static Stopwatch Begin() => Enabled ? Stopwatch.StartNew() : null;

    internal static void Write(string stage, string path, long milliseconds)
    {
        if (!Enabled || string.IsNullOrEmpty(Directory))
            return;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(
                Path.Combine(Directory, "video-timing.txt"),
                $"{DateTime.UtcNow:o}|{stage}|{milliseconds}ms|{path}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break a preview.
        }
    }
}
