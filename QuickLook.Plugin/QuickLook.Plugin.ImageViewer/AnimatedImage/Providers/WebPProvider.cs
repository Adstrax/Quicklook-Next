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
using ImageMagick.Formats;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.ImageViewer.AnimatedImage.Providers;

internal class WebPProvider : ImageMagickProvider
{
    private bool _isPlaying;

    public WebPProvider(Uri path, MetaProvider meta, ContextObject contextObject) : base(path, meta, contextObject)
    {
    }

    public override Task<BitmapSource> GetRenderedFrame(int index)
    {
        return new Task<BitmapSource>(() =>
        {
            var settings = new MagickReadSettings
            {
                BackgroundColor = MagickColors.None,
                Defines = new DngReadDefines
                {
                    OutputColor = DngOutputColor.SRGB,
                    UseCameraWhiteBalance = true,
                    DisableAutoBrightness = false,
                }
            };

            try
            {
                // Unfortunately we only support Animated WebP on x64 platforms
                if (Environment.Is64BitProcess)
                {
                    var layers = MagickImageInfo.ReadCollection(Path.LocalPath);
                    int count = layers.Count();

                    // Animated WebP image
                    if (count > 1)
                    {
                        return AnimatedWebP(Path.LocalPath);
                    }
                    else
                    {
                        return base.GetRenderedFrame();
                    }
                }

                return base.GetRenderedFrame();
            }
            catch (Exception e)
            {
                ProcessHelper.WriteLog(e.ToString());
                return null!;
            }
        });
    }

    public override void Pause()
    {
        _isPlaying = false;
    }

    public override void Dispose()
    {
        _isPlaying = false;
        base.Dispose();
    }

    private BitmapSource AnimatedWebP(string fileName)
    {
        using var collection = new MagickImageCollection(fileName);
        collection.Coalesce();

        var frames = collection.Select(frame =>
        {
            var durationMs = frame.AnimationDelay > 0 ? frame.AnimationDelay * 10 : 100;
            frame.Format = MagickFormat.Png;
            using var stream = new MemoryStream(frame.ToByteArray());
            return (Bitmap: ToArgb32(new Bitmap(stream)), Duration: TimeSpan.FromMilliseconds(durationMs));
        }).ToList();

        var writeableBitmap = Application.Current.Dispatcher.Invoke(() =>
        {
            return frames[0].Bitmap.ToWriteableBitmap();
        });

        _isPlaying = true;
        _ = Task.Factory.StartNew(() =>
        {
            try
            {
                while (_isPlaying)
                {
                    foreach (var frame in frames)
                    {
                        if (!_isPlaying) break;

                        writeableBitmap.Dispatcher.Invoke(() =>
                        {
                            frame.Bitmap.CopyToWriteableBitmap(writeableBitmap);
                        });

                        Thread.Sleep((int)frame.Duration.TotalMilliseconds);
                    }
                }
            }
            finally
            {
                foreach (var frame in frames)
                    frame.Bitmap.Dispose();
            }
        }, TaskCreationOptions.LongRunning);

        return writeableBitmap;
    }

    /// <summary>
    /// v5.6.13: GDI+ hands back whatever the PNG inside the frame was encoded as, and a frame with few
    /// colours comes back indexed - which <see cref="Extension.ToWriteableBitmap"/> cannot map to a WPF
    /// pixel format. The frame is drawn into a 32-bit ARGB bitmap first. Without this, an animated WebP
    /// (or any of its frames) with few colours threw NotSupportedException, the frame came back null and
    /// the preview showed nothing but its own background.
    /// </summary>
    private static Bitmap ToArgb32(Bitmap bitmap)
    {
        if (bitmap.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppArgb)
            return bitmap;

        var copy = new Bitmap(bitmap.Width, bitmap.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        copy.SetResolution(bitmap.HorizontalResolution, bitmap.VerticalResolution);

        using (var graphics = Graphics.FromImage(copy))
            graphics.DrawImageUnscaled(bitmap, 0, 0);

        bitmap.Dispose();
        return copy;
    }
}

file static class Extension
{
    public static WriteableBitmap ToWriteableBitmap(this Bitmap bitmap)
    {
        if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));

        var pixelFormat = bitmap.PixelFormat;
        var width = bitmap.Width;
        var height = bitmap.Height;

        var wpfPixelFormat = pixelFormat switch
        {
            System.Drawing.Imaging.PixelFormat.Format32bppArgb => PixelFormats.Bgra32,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb => PixelFormats.Bgr24,
            _ => throw new NotSupportedException($"Unsupported PixelFormat: {pixelFormat}")
        };

        var writeableBitmap = new WriteableBitmap(width, height, 96, 96, wpfPixelFormat, null);

        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            pixelFormat);

        try
        {
            writeableBitmap.Lock();
            unsafe
            {
                Buffer.MemoryCopy(
                    source: bitmapData.Scan0.ToPointer(),
                    destination: writeableBitmap.BackBuffer.ToPointer(),
                    destinationSizeInBytes: writeableBitmap.BackBufferStride * height,
                    sourceBytesToCopy: bitmapData.Stride * height);
            }

            writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
            writeableBitmap.Unlock();
        }

        return writeableBitmap;
    }

    public static void CopyToWriteableBitmap(this Bitmap bitmap, WriteableBitmap writeableBitmap)
    {
        var pixelFormat = bitmap.PixelFormat;
        var width = bitmap.Width;
        var height = bitmap.Height;

        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            pixelFormat);

        try
        {
            writeableBitmap.Lock();
            unsafe
            {
                Buffer.MemoryCopy(
                    source: bitmapData.Scan0.ToPointer(),
                    destination: writeableBitmap.BackBuffer.ToPointer(),
                    destinationSizeInBytes: writeableBitmap.BackBufferStride * height,
                    sourceBytesToCopy: bitmapData.Stride * height);
            }

            writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
            writeableBitmap.Unlock();
        }
    }
}
