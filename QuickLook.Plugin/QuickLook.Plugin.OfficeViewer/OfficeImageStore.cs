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
using System.Text;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v5.7.0: pictures a self-rendered Office page references are written here once, scaled to what the page
/// can actually show, and served to WebView2 over a virtual host.
///
/// <para>
/// The previous approach inlined the original bytes as base64. For a design template that meant one slide
/// carrying a 25 MB photo became 34 MB of HTML - which Chromium then had to parse and decode at full size -
/// and for Word it regularly pushed the page past NavigateToString's 2 MB limit, which fails the whole
/// navigation rather than one image.
/// </para>
/// </summary>
internal static class OfficeImageStore
{
    /// <summary>A preview never shows more detail than this, whatever the slide or page measures.</summary>
    private const int MaxPixels = 1600;

    /// <summary>A fresh folder per preview, cleaned up when the panel goes away.</summary>
    internal static string CreateFolder()
    {
        var folder = Path.Combine(
            Path.GetTempPath(), "QuickLookNext-office", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    internal static void Cleanup(string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return;

        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception e)
        {
            // The next preview writes its own folder; a leftover is only disk noise.
            Debug.WriteLine(e.Message);
        }
    }

    /// <summary>
    /// Drops folders left behind by a session that was killed before its panel
    /// could dispose. A day is well past any parse, so no live preview owns
    /// one of these.
    /// </summary>
    internal static void CleanupStaleFolders()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "QuickLookNext-office");
            if (!Directory.Exists(root))
                return;

            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                        Directory.Delete(directory, true);
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                }
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
        }
    }

    /// <summary>
    /// v5.7.1: writes the page itself beside its pictures and returns the file name, so the panel can
    /// navigate to it over the same virtual host instead of handing the whole document over as a string.
    /// <c>NavigateToString</c> destroys the page that was showing the moment it is called, which leaves the
    /// browser with nothing to draw while the new document is parsed and laid out - measured as an opaque
    /// near-black surface for the 300-500 ms that takes. Navigating to a document instead is the ordinary
    /// case every browser is built around: the page on screen stays up until the new one is ready to paint.
    /// A fresh folder per preview means the URL is new every time, so a repeat preview is never served from
    /// a stale cache.
    /// </summary>
    internal static string SaveHtml(string folder, string html)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(html))
            return null;

        try
        {
            File.WriteAllText(
                Path.Combine(folder, "page.html"), html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return "page.html";
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes a scaled copy of <paramref name="bytes"/> and returns its file name, or null when the bytes
    /// are not an image this machine can decode.
    /// </summary>
    /// <param name="displayWidthPx">How wide the page will draw it; 0 means "as wide as it is".</param>
    internal static string Save(string folder, byte[] bytes, double displayWidthPx, string baseName)
    {
        if (bytes is null || bytes.Length == 0 || string.IsNullOrEmpty(folder))
            return null;

        try
        {
            var natural = NaturalWidth(bytes);
            var target = (int)Math.Ceiling(Math.Min(
                displayWidthPx > 0 ? displayWidthPx * 2d : MaxPixels, MaxPixels));

            // Never scale a small picture up: it only makes the file bigger.
            if (natural > 0 && target > natural)
                target = natural;

            var image = Decode(bytes, target);
            if (image is null)
                return null;

            var isJpeg = bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
            var name = baseName + (isJpeg ? ".jpg" : ".png");

            BitmapEncoder encoder = isJpeg
                ? new JpegBitmapEncoder { QualityLevel = 88 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            using var file = File.Create(Path.Combine(folder, name));
            encoder.Save(file);
            return name;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            return null;
        }
    }

    private static int NaturalWidth(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return decoder.Frames.Count > 0 ? decoder.Frames[0].PixelWidth : 0;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            return 0;
        }
    }

    private static BitmapSource Decode(byte[] bytes, int target)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(bytes);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (target > 0)
            image.DecodePixelWidth = target;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
