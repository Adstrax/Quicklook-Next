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
using QuickLook.Plugin.ImageViewer.Helpers;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MediaPixelFormats = System.Windows.Media.PixelFormats;

namespace QuickLook.Plugin.ImageViewer.AnimatedImage.Providers;

internal class ImageMagickProvider : AnimationProvider
{
    public ImageMagickProvider(Uri path, MetaProvider meta, ContextObject contextObject) : base(path, meta, contextObject)
    {
    }

    public override Task<BitmapSource> GetThumbnail(Size renderSize)
        => GetThumbnail(renderSize, CancellationToken.None);

    public override Task<BitmapSource> GetThumbnail(Size renderSize, CancellationToken cancellationToken)
    {
        var fullSize = Meta.GetSize();
        var orientation = Meta.GetOrientation();

        return new Task<BitmapSource>(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;

                using var buffer = new MemoryStream(Meta.GetThumbnail());
                if (buffer.Length > 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return null;

                    var img = new BitmapImage();
                    img.BeginInit();
                    img.StreamSource = buffer;
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.EndInit();

                    if (cancellationToken.IsCancellationRequested)
                        return null;

                    // Do not show a thumbnail too small for the frame and replace it a moment
                    // later: that is the visible blur-to-sharp transition. Decode the file up
                    // front when the embedded thumbnail cannot carry the initial view.
                    if (IsThumbnailDetailedEnough(img, renderSize))
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return null;

                        var transformed = RotateThumbnail(img, orientation, fullSize);

                        ImageHelper.DpiHack(transformed);
                        transformed.Freeze();
                        return cancellationToken.IsCancellationRequested ? null : transformed;
                    }
                }

                // No usable embedded thumbnail, or the user zoomed past it: decode the file
                // itself at the requested size, still capped by MaxDecodePixels.
                var requested = RequestedDecodeSize(fullSize, renderSize);
                return DecodeAt(DecodePixelLimit.Limit(requested), cancellationToken);
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
        return new Task<BitmapSource>(() => GetRenderedFrame(cancellationToken));
    }

    protected virtual BitmapSource GetRenderedFrame()
        => GetRenderedFrame(CancellationToken.None);

    protected virtual BitmapSource GetRenderedFrame(CancellationToken cancellationToken)
        => DecodeAt(DecodePixelLimit.Limit(Meta.GetSize()), cancellationToken);

    /// <summary>
    /// v6.1.0: decodes the file (not its embedded thumbnail) at a specific size. Large decodes
    /// are serialized so a fast arrow-key walk cannot hold several full-size images at once.
    /// </summary>
    private BitmapSource DecodeAt(Size decodeSize, CancellationToken cancellationToken)
    {
        var fullSize = Meta.GetSize();
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

        IMagickImage<byte> mi = null;
        var isImageOwned = false;  // Track whether this provider owns the Magick image instance and must dispose it.

        try
        {
            if (!LargeImageDecodeGate.TryEnter(fullSize, cancellationToken, out var gate))
                return null!;

            using (gate)
            {
                if (cancellationToken.IsCancellationRequested)
                    return null!;

                // Only flatten multi-layer gimp xcf files. Other formats (e.g. PSD) should avoid
                // loading all layers via MagickImageCollection for performance.
                if (Path.LocalPath.EndsWith(".xcf", StringComparison.OrdinalIgnoreCase))
                {
                    using var layers = new MagickImageCollection(Path.LocalPath, settings);
                    if (layers.Count > 1)
                    {
                        mi = layers.Flatten(MagickColor.FromRgba(0, 0, 0, 0));
                        isImageOwned = true; // Flatten creates a new image instance we must dispose.
                    }
                    else
                    {
                        mi = layers[0];
                    }
                }
                else
                {
                    mi = new MagickImage(Path.LocalPath, settings);
                    isImageOwned = true; // New MagickImage created here must be disposed by this provider.
                }

                if (cancellationToken.IsCancellationRequested)
                    return null!;

                if (SettingHelper.Get("UseColorProfile", false, "QuickLook.Plugin.ImageViewer"))
                {
                    if (mi.ColorSpace == ColorSpace.RGB || mi.ColorSpace == ColorSpace.sRGB || mi.ColorSpace == ColorSpace.scRGB)
                    {
                        mi.SetProfile(ColorProfiles.SRGB);
                        if (ContextObject.ColorProfileName != null)
                            mi.SetProfile(new ColorProfile(ContextObject.ColorProfileName)); // map to monitor color
                    }
                }

                mi.AutoOrient();

                if (cancellationToken.IsCancellationRequested)
                    return null!;

                if (mi.Width != (int)decodeSize.Width || mi.Height != (int)decodeSize.Height)
                    mi.Resize((uint)decodeSize.Width, (uint)decodeSize.Height);

                if (cancellationToken.IsCancellationRequested)
                    return null!;

                var displayScale = ImageHelper.GetDisplayScaleFactor();
                mi.Density = new Density(
                    DisplayDeviceHelper.DefaultDpi * displayScale.Horizontal,
                    DisplayDeviceHelper.DefaultDpi * displayScale.Vertical);

                var img = mi.ToBitmapSourceWithDensity();

                if (cancellationToken.IsCancellationRequested)
                    return null!;

                img.Freeze();
                return img;
            }
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog(e.ToString());
            return null!;
        }
        finally
        {
            if (isImageOwned)
                mi?.Dispose();
        }
    }

    public override void Dispose()
    {
    }

    private static Size RequestedDecodeSize(Size fullSize, Size requested)
    {
        if (fullSize.IsEmpty || requested.IsEmpty)
            return fullSize;

        var scale = Math.Min(1d,
            Math.Min(requested.Width / fullSize.Width, requested.Height / fullSize.Height));

        return new Size(
            Math.Max(1d, Math.Round(fullSize.Width * scale)),
            Math.Max(1d, Math.Round(fullSize.Height * scale)));
    }

    private static bool IsThumbnailDetailedEnough(BitmapImage image, Size requested)
    {
        if (requested.IsEmpty)
            return true;

        // 0.6 is deliberately generous: a slightly soft first frame is better than decoding
        // the whole RAW/PSD for every initial preview. Zooming asks for the exact size.
        return image.PixelWidth >= requested.Width * 0.6 &&
               image.PixelHeight >= requested.Height * 0.6;
    }

    protected static BitmapSource RotateThumbnail(BitmapImage image, Orientation orientation, Size fullSize)
    {
        var transforms = new TransformGroup();

        // some RAWs, like from RX100, have thumbnails already rotated.
        if (fullSize.Height >= fullSize.Width && image.PixelHeight <= image.PixelWidth ||
            fullSize.Height < fullSize.Width && image.PixelHeight > image.PixelWidth)
            switch (orientation)
            {
                case Orientation.TopRight:
                    transforms.Children.Add(new ScaleTransform(-1, 1, 0, 0));
                    break;

                case Orientation.BottomRight:
                    transforms.Children.Add(new RotateTransform(180));
                    break;

                case Orientation.BottomLeft:
                    transforms.Children.Add(new ScaleTransform(1, 1, 0, 0));
                    break;

                case Orientation.LeftTop:
                    transforms.Children.Add(new RotateTransform(90));
                    transforms.Children.Add(new ScaleTransform(-1, 1, 0, 0));
                    break;

                case Orientation.RightTop:
                    transforms.Children.Add(new RotateTransform(90));
                    break;

                case Orientation.RightBottom:
                    transforms.Children.Add(new RotateTransform(270));
                    transforms.Children.Add(new ScaleTransform(-1, 1, 0, 0));
                    break;

                case Orientation.LeftBottom:
                    transforms.Children.Add(new RotateTransform(270));
                    break;
            }

        return transforms.Children.Count == 0
            ? image
            : new TransformedBitmap(image, transforms);
    }

    protected bool IsImageMagickSupported(string path)
    {
        try
        {
            return new MagickImageInfo(path).Format != MagickFormat.Unknown;
        }
        catch
        {
            return false;
        }
    }
}

file static class Extension
{
    /// <summary>
    /// https://github.com/dlemstra/Magick.NET/blob/main/src/Magick.NET.SystemWindowsMedia/IMagickImageExtentions.cs
    /// </summary>
    public static BitmapSource ToBitmapSourceWithDensity(this IMagickImage<byte> self, bool useDensity = true)
    {
        var image = self;

        var mapping = "RGB";
        var format = MediaPixelFormats.Rgb24;

        try
        {
            if (self.ColorSpace == ColorSpace.CMYK && !image.HasAlpha)
            {
                mapping = "CMYK";
                format = MediaPixelFormats.Cmyk32;
            }
            else
            {
                if (image.ColorSpace != ColorSpace.sRGB)
                {
                    image = self.Clone();
                    image.ColorSpace = ColorSpace.sRGB;
                }

                if (image.HasAlpha)
                {
                    mapping = "BGRA";
                    format = MediaPixelFormats.Bgra32;
                }
            }

            var step = format.BitsPerPixel / 8;
            var stride = (int)image.Width * step;

            using var pixels = image.GetPixelsUnsafe();
            var bytes = pixels.ToByteArray(mapping);
            var dpi = GetDefaultDensity(image, useDensity ? DensityUnit.PixelsPerInch : DensityUnit.Undefined);
            return BitmapSource.Create((int)image.Width, (int)image.Height, dpi.X, dpi.Y, format, null, bytes, stride);
        }
        finally
        {
            if (!ReferenceEquals(self, image))
                image.Dispose();
        }
    }

    private static Density GetDefaultDensity(IMagickImage image, DensityUnit units)
    {
        if (units == DensityUnit.Undefined || (image.Density.X <= 0 || image.Density.Y <= 0))
            return new Density(96);

        return image.Density.ChangeUnits(units);
    }
}
