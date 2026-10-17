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

using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.ImageViewer.Helpers;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.ImageViewer.AnimatedImage.Providers;

internal class NativeProvider : AnimationProvider
{
    public NativeProvider(Uri path, MetaProvider meta, ContextObject contextObject) : base(path, meta, contextObject)
    {
    }

    public override Task<BitmapSource> GetThumbnail(Size renderSize)
        => GetThumbnail(renderSize, CancellationToken.None);

    public override Task<BitmapSource> GetThumbnail(Size renderSize, CancellationToken cancellationToken)
    {
        var fullSize = Meta.GetSize();
        var decodeWidth =
            (int)Math.Round(Math.Min(Meta.GetSize().Width, Math.Max(1d, Math.Floor(renderSize.Width))));
        var decodeHeight =
            (int)Math.Round(Math.Min(Meta.GetSize().Height, Math.Max(1d, Math.Floor(renderSize.Height))));
        var orientation = Meta.GetOrientation();
        var rotate = ShouldRotate(orientation);

        // v1.2.13: cache the downscaled decode (small); the full-size wrap is
        // rebuilt cheaply on a hit, so switching back to an image already
        // previewed does not re-decode the file.
        var cacheKey = $"t|{Path}|{decodeWidth}x{decodeHeight}|{(int)orientation}";
        if (DecodedImageCache.TryGet(cacheKey, out var cachedImg))
        {
            return new Task<BitmapSource>(() =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                        return null;

                    // v6.1.0: the thumbnail keeps its own pixel coordinate space. Scaling it to
                    // the file's (or the 40 MP cap's) size made WPF lay out and render inside an
                    // enormous coordinate space, which was the bulk of the large-image peak.
                    var cachedRotated = ApplyTransformFromExif(cachedImg, orientation);

                    ImageHelper.DpiHack(cachedRotated);
                    cachedRotated.Freeze();

                    return cancellationToken.IsCancellationRequested ? null : cachedRotated;
                }
                catch (Exception e)
                {
                    ProcessHelper.WriteLog(e.ToString());
                    return null;
                }
            });
        }

        return new Task<BitmapSource>(() =>
        {
            try
            {
                // v6.1.0: never run two large decodes at once. A stale decode is discarded by
                // AnimatedImage, but it still costs memory while it runs.
                if (!LargeImageDecodeGate.TryEnter(fullSize, cancellationToken, out var gate))
                    return null;

                using (gate)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return null;

                    var img = new BitmapImage();
                    img.BeginInit();
                    img.UriSource = Path;
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    // specific renderSize to avoid .net's double to int conversion
                    img.DecodePixelWidth = rotate ? decodeHeight : decodeWidth;
                    img.DecodePixelHeight = rotate ? decodeWidth : decodeHeight;
                    img.EndInit();

                    if (cancellationToken.IsCancellationRequested)
                        return null;

                    DecodedImageCache.Add(cacheKey, img);

                    var rotated = ApplyTransformFromExif(img, orientation);

                    ImageHelper.DpiHack(rotated);
                    rotated.Freeze();

                    return cancellationToken.IsCancellationRequested ? null : rotated;
                }
            }
            // System.IO.IOException: Cannot read from the stream.
            // Inner Exception: COMException(0x88982F72): Failed to read from the stream.
            catch (IOException ioe) when (ioe.InnerException is COMException come)
            {
                // https://github.com/QL-Win/QuickLook/issues/1671
                // This error is caused by unsupported ColorContexts in Windows
                // Although can avoid color problems through `CreateOptions = BitmapCreateOptions.IgnoreColorProfile`
                // But it may cause color casts, so fallback to ImageMagick
                if (come.Message.Contains("0x88982F72"))
                {
                    var fallbackProvidor = new ImageMagickProvider(Path, Meta, ContextObject);
                    var task = fallbackProvidor.GetThumbnail(renderSize, cancellationToken);

                    task.Start();
                    return task.Result;
                }
                ProcessHelper.WriteLog(ioe.ToString());
                return null;
            }
            catch (Exception e)
            {
                ProcessHelper.WriteLog(e.ToString());
                return null;
            }
        });
    }

    public override Task<BitmapSource> GetRenderedFrame(int index)
        => GetRenderedFrame(index, CancellationToken.None);

    public override Task<BitmapSource> GetRenderedFrame(int index, CancellationToken cancellationToken)
    {
        var fullSize = Meta.GetSize();
        var rotate = ShouldRotate(Meta.GetOrientation());

        return new Task<BitmapSource>(() =>
        {
            try
            {
                if (!LargeImageDecodeGate.TryEnter(fullSize, cancellationToken, out var gate))
                    return null;

                using (gate)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return null;

                    var img = new BitmapImage();
                    img.BeginInit();
                    img.UriSource = Path;
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    // v5.0.5: cap the full-resolution decode so a huge image cannot exhaust
                    // memory (see DecodePixelLimit; the aspect ratio is preserved).
                    var decodeSize = DecodePixelLimit.Limit(fullSize);
                    img.DecodePixelWidth = (int)(rotate ? decodeSize.Height : decodeSize.Width);
                    img.DecodePixelHeight = (int)(rotate ? decodeSize.Width : decodeSize.Height);
                    img.EndInit();

                    var img2 = ApplyTransformFromExif(img, Meta.GetOrientation());

                    ImageHelper.DpiHack(img2);
                    img2.Freeze();

                    return cancellationToken.IsCancellationRequested ? null : img2;
                }
            }
            // System.IO.IOException: Cannot read from the stream.
            // Inner Exception: COMException(0x88982F72): Failed to read from the stream.
            catch (IOException ioe) when (ioe.InnerException is COMException come)
            {
                // https://github.com/QL-Win/QuickLook/issues/1671
                // This error is caused by unsupported ColorContexts in Windows
                // Although can avoid color problems through `CreateOptions = BitmapCreateOptions.IgnoreColorProfile`
                // But it may cause color casts, so fallback to ImageMagick
                if (come.Message.Contains("0x88982F72"))
                {
                    var fallbackProvidor = new ImageMagickProvider(Path, Meta, ContextObject);
                    var task = fallbackProvidor.GetRenderedFrame(index, cancellationToken);

                    task.Start();
                    return task.Result;
                }
                ProcessHelper.WriteLog(ioe.ToString());
                return null;
            }
            catch (Exception e)
            {
                ProcessHelper.WriteLog(e.ToString());
                return null;
            }
        });
    }

    private static bool ShouldRotate(Orientation orientation)
    {
        var rotate = false;
        switch (orientation)
        {
            case Orientation.LeftTop:
            case Orientation.RightTop:
            case Orientation.RightBottom:
            case Orientation.LeftBottom:
                rotate = true;
                break;
        }

        return rotate;
    }

    private static BitmapSource ApplyTransformFromExif(BitmapSource image, Orientation orientation)
    {
        return orientation switch
        {
            Orientation.Undefined or Orientation.TopLeft => image,
            Orientation.TopRight => new TransformedBitmap(image, new ScaleTransform(-1, 1, 0, 0)),
            Orientation.BottomRight => new TransformedBitmap(image, new RotateTransform(180)),
            Orientation.BottomLeft => new TransformedBitmap(image, new ScaleTransform(1, 1, 0, 0)),
            Orientation.LeftTop => new TransformedBitmap(
                new TransformedBitmap(image, new RotateTransform(90)),
                new ScaleTransform(-1, 1, 0, 0)),
            Orientation.RightTop => new TransformedBitmap(image, new RotateTransform(90)),
            Orientation.RightBottom => new TransformedBitmap(
                new TransformedBitmap(image, new RotateTransform(270)),
                new ScaleTransform(-1, 1, 0, 0)),
            Orientation.LeftBottom => new TransformedBitmap(image, new RotateTransform(270)),
            _ => image,
        };
    }

    public override void Dispose()
    {
    }
}
