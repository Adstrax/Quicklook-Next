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

using ImageMagick;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Xml;

namespace QuickLook.Plugin.ImageViewer;

public partial class MetaProvider
{
    private readonly object _lock = new();
    private SortedDictionary<string, (string, string)> _cache = []; // [key, [label, value]]
    private Size? _pingSize;

    private readonly string _path;

    public MetaProvider(string path)
    {
        _path = path;
        // NOTE: EXIF is intentionally NOT read eagerly here. The native exiv2
        // call can take tens of milliseconds on large files; deferring it until
        // GetExif() is first called (metadata panel, after the window is shown)
        // makes the preview window appear sooner. GetSize() uses a fast
        // header-only MagickImage.Ping instead.
    }

    public SortedDictionary<string, (string, string)> GetExif()
    {
        lock (_lock)
        {
            if (_cache.Count != 0)
                return _cache;

            var exif = NativeMethods.GetExif(_path);
            if (string.IsNullOrEmpty(exif))
                return _cache;

            var xml = new XmlDocument();
            xml.LoadXml(exif);

            var parsed = new SortedDictionary<string, (string, string)>();
            var iter = xml.SelectNodes("/Exif/child::node()")?.GetEnumerator();
            while (iter != null && iter.MoveNext())
            {
                if (iter.Current is not XmlNode node)
                    continue;

                var key = node.Name;
                var label = node.Attributes?["Label"]?.InnerText;
                var value = node.InnerText;

                parsed.Add(key, (label, value));
            }

            _cache = parsed;
            return _cache;
        }
    }

    public byte[] GetThumbnail()
    {
        return NativeMethods.GetThumbnail(_path) ?? [];
    }

    public Size GetSize()
    {
        lock (_lock)
        {
            _cache.TryGetValue("_.Size.Width", out var w_);
            _cache.TryGetValue("_.Size.Height", out var h_);

            if (int.TryParse(w_.Item2, out var w) && int.TryParse(h_.Item2, out var h))
                return new Size(w, h);

            if (IsDicomFile(_path))
            {
                var dicomSize = TryGetDicomSize(_path);
                if (!dicomSize.IsEmpty)
                    return dicomSize;
            }

            if (IsGfieFile(_path))
            {
                var gfieSize = TryGetGfieSize(_path);
                if (!gfieSize.IsEmpty)
                    return gfieSize;
            }

            if (_pingSize.HasValue)
                return _pingSize.Value;

            // v6.1.1: probe with the decoder the panel itself uses (WIC). Magick.NET
            // is a large native library; loading it here just to learn the size cost
            // ~600 ms on the first image of a session (measured: 653 ms inside
            // Prepare for a 1.6 KB PNG). WIC reads the header in a few milliseconds.
            if (TryGetWicSize(_path, out var wicSize))
            {
                _pingSize = wicSize;
                return wicSize;
            }

            // Fallback: header-only read via MagickImage.Ping, for the formats WIC
            // does not have a codec for.
            try
            {
                using var mi = new MagickImage();
                mi.Ping(_path);
                w = (int)mi.Width;
                h = (int)mi.Height;
            }
            catch
            {
                // There are always formats that MagickImage does not support;
                // fall back to the (lazy) exiv2 metadata.
                GetExif();
                _cache.TryGetValue("_.Size.Width", out w_);
                _cache.TryGetValue("_.Size.Height", out h_);
                if (int.TryParse(w_.Item2, out w) && int.TryParse(h_.Item2, out h))
                    return new Size(w, h);

                return Size.Empty;
            }

            if (w + h == 0)
                return new Size(800, 600);

            _pingSize = new Size(w, h);
            return _pingSize.Value;
        }
    }

    /// <summary>
    /// Reads only the image header through WIC, the same decoder the panel uses.
    /// <see cref="BitmapCreateOptions.DelayCreation"/> plus
    /// <see cref="BitmapCacheOption.None"/> keep the pixels off the heap; only the
    /// frame's dimensions are touched.
    /// </summary>
    private static bool TryGetWicSize(string path, out Size size)
    {
        size = Size.Empty;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);

            if (decoder.Frames.Count == 0)
                return false;

            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                return false;

            size = new Size(frame.PixelWidth, frame.PixelHeight);
            return true;
        }
        catch
        {
            // Not a format WIC can open - the caller falls back to Magick.
            return false;
        }
    }

    public Orientation GetOrientation()
    {
        return (Orientation)NativeMethods.GetOrientation(_path);
    }
}

file static class NativeMethods
{
    public static string GetExif(string file)
    {
        try
        {
            var len = GetExif_64(file, null);
            if (len <= 0)
                return string.Empty;

            var sb = new StringBuilder(len + 1);
            var _ = GetExif_64(file, sb);

            return sb.ToString();
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return string.Empty;
        }
    }

    public static byte[] GetThumbnail(string file)
    {
        try
        {
            var len = GetThumbnail_64(file, null);
            if (len <= 0)
                return null;

            var buffer = new byte[len];
            var _ = GetThumbnail_64(file, buffer);

            return buffer;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return null;
        }
    }

    public static int GetOrientation(string file)
    {
        try
        {
            return GetOrientation_64(file);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return 0;
        }
    }

    [DllImport("exiv2-ql-64.dll", EntryPoint = "GetExif", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetExif_64([MarshalAs(UnmanagedType.LPWStr)] string file,
        [MarshalAs(UnmanagedType.LPStr)] StringBuilder sb);

    [DllImport("exiv2-ql-64.dll", EntryPoint = "GetThumbnail", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetThumbnail_64([MarshalAs(UnmanagedType.LPWStr)] string file,
        [MarshalAs(UnmanagedType.LPArray)] byte[] buffer);

    [DllImport("exiv2-ql-64.dll", EntryPoint = "GetOrientation", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetOrientation_64([MarshalAs(UnmanagedType.LPWStr)] string file);
}

public enum Orientation
{
    Undefined = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomRight = 3,
    BottomLeft = 4,
    LeftTop = 5,
    RightTop = 6,
    RightBottom = 7,
    LeftBottom = 8
}
