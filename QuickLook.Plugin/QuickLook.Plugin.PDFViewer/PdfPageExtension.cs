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

using PdfiumViewer;
using QuickLook.Common.ExtensionMethods;
using QuickLook.Common.Helpers;
using System;
using System.Drawing;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.PDFViewer;

internal static class PdfPageExtension
{
    // The thumbnail strip gives each page a 160-DIP image area. Render it at that logical size with two
    // source pixels per display pixel: WPF can then paint a native-resolution bitmap instead of enlarging
    // a small one or softening a much larger one through a second downscale.
    private const double ThumbnailWidth = 160d;
    private const double ThumbnailHeight = 160d;
    private const double ThumbnailPixelDensity = 2d;

    internal static readonly object SyncRoot = new();

    public static BitmapSource RenderThumbnail(this PdfDocumentWrapper doc, int page)
    {
        var scale = DisplayScaleFactor();

        // v6.1.3: the virtualized strip asks again every time an item scrolls
        // back into view. A remembered thumbnail paints immediately instead of
        // flashing the loading image and rendering the page a second time.
        var cached = FindCachedThumbnail(doc, page, scale);
        if (cached is not null)
            return cached;

        // v6.1.4: this used to reload and re-parse the whole PDF after every
        // fifty thumbnails. On a large document that copied the file a second
        // time and blocked every other PDFium render while it ran, producing a
        // periodic stall during a long scroll. The current Pdfium renderer
        // releases its per-page resources after each bitmap and the thumbnail
        // cache already has a hard entry/byte budget, so the document itself
        // no longer needs to be rebuilt.

        var size = doc.PdfDocument.PageSizes[page];
        var factorX = ThumbnailWidth / size.Width;
        var factorY = ThumbnailHeight / size.Height;

        // Thumbnail text is downscaled by WPF, so subpixel (LCD) colour fringes only add softness here.
        var image = doc.Render(page, Math.Min(factorX, factorY), true, scale,
            ThumbnailPixelDensity, useLcdText: false);
        if (image is not null)
            doc.ThumbnailCache.Add(page, scale, ThumbnailPixelDensity, image);

        return image;
    }

    /// <summary>
    /// Answers a repeated realization from memory without putting it behind
    /// queued rasterization work.
    /// </summary>
    internal static BitmapSource FindCachedThumbnail(PdfDocumentWrapper doc, int page)
        => FindCachedThumbnail(doc, page, DisplayScaleFactor());

    private static BitmapSource FindCachedThumbnail(
        PdfDocumentWrapper doc,
        int page,
        double scale)
    {
        if (doc is null || page < 0 || page >= doc.PdfDocument.PageCount)
            return null;

        return doc.ThumbnailCache.Find(page, scale, ThumbnailPixelDensity);
    }

    /// <summary>
    /// v6.1.3: the scale the page will be rasterized for. The page bitmap cache
    /// keys on the same value, so it has to be decided once on the caller's
    /// thread instead of being read again inside the background render.
    /// </summary>
    internal static double DisplayScaleFactor()
    {
        var current = DisplayScale.Current;
        if (current > 0d && !double.IsNaN(current))
            return current;

        var scale = DisplayDeviceHelper.GetCurrentScaleFactor();
        var value = scale.Horizontal;
        return value > 0d && !double.IsNaN(value) ? value : 1d;
    }

    public static BitmapSource Render(this PdfDocumentWrapper doc, int page, double factor, bool fixDpi = true)
        => doc.Render(page, factor, fixDpi, 0d);

    /// <param name="displayScale">
    /// A scale already decided by the caller; 0 means "ask the system now".
    /// </param>
    public static BitmapSource Render(
        this PdfDocumentWrapper doc, int page, double factor, bool fixDpi, double displayScale,
        double pixelDensity = 1d, bool useLcdText = true)
    {
        // v6.1.2: use the scale of the window that will show the page. Asking
        // for the foreground window's monitor can be wrong on a mixed-DPI
        // desktop, which makes correctly sized PDF pixels look soft.
        var scale = displayScale > 0d && !double.IsNaN(displayScale)
            ? displayScale
            : DisplayScaleFactor();
        var density = pixelDensity > 0d && !double.IsNaN(pixelDensity) ? pixelDensity : 1d;
        var baseDpi = fixDpi ? scale * density * DisplayDeviceHelper.DefaultDpi : 96d;

        Bitmap bitmap;
        double dpiX;
        double dpiY;

        lock (SyncRoot)
        {
            var size = doc.PdfDocument.PageSizes[page];

            // v6.1.6: at extreme zoom the requested raster can be several
            // hundred megapixels. Cap it, and lower the DPI by the same ratio:
            // the bitmap then keeps the DIP size the viewer asked for, so the
            // page is simply magnified from a smaller source instead of making
            // PDFium and WPF allocate memory no screen can show at once.
            var ratio = PdfPageRenderLimit.Ratio(
                new System.Windows.Size(size.Width, size.Height), factor, scale, density);
            dpiX = baseDpi * ratio;
            dpiY = baseDpi * ratio;

            var realWidth = Math.Max(1, (int)Math.Round(size.Width * scale * density * factor * ratio));
            var realHeight = Math.Max(1, (int)Math.Round(size.Height * scale * density * factor * ratio));

            var flags = PdfRenderFlags.LimitImageCacheSize | PdfRenderFlags.Annotations |
                        PdfRenderFlags.ForPrinting;
            if (useLcdText)
                flags |= PdfRenderFlags.LcdText;

            bitmap = doc.PdfDocument.Render(page, realWidth, realHeight, (float)dpiX, (float)dpiY,
                PdfRotation.Rotate0,
                flags, true) as Bitmap;
        }

        var bs = bitmap?.ToBitmapSource(dpiX, dpiY);
        bitmap?.Dispose();

        bs?.Freeze();
        return bs;
    }

    /// <summary>
    /// v6.1.8: rasterizes one rectangle of a page at screen density. PDFium
    /// draws the whole page into the DC; a negative viewport origin shifts it
    /// so only the requested rectangle lands in the (small) target bitmap, and
    /// the DC clips the rest. This is what keeps a magnified page sharp beyond
    /// the full-page pixel cap without allocating a full-page raster.
    /// </summary>
    internal static BitmapSource RenderRegion(
        this PdfDocumentWrapper doc,
        int page,
        System.Windows.Rect region,
        double viewFactor,
        double displayScale)
    {
        if (doc is null || region.IsEmpty ||
            region.Width <= 0d || region.Height <= 0d ||
            viewFactor <= 0d || double.IsNaN(viewFactor))
            return null;

        var scale = displayScale > 0d && !double.IsNaN(displayScale)
            ? displayScale
            : DisplayScaleFactor();
        if (scale <= 0d || double.IsNaN(scale))
            return null;

        // Device pixels per page unit, and the device pixels of the whole page.
        var pixelScale = viewFactor * scale;
        var tilePixels = region.Width * pixelScale * region.Height * pixelScale;
        var ratio = tilePixels > MaxTilePixels
            ? Math.Sqrt(MaxTilePixels / tilePixels)
            : 1d;

        // Lowering the density with the pixel count keeps the bitmap's DIP
        // size at region * viewFactor, so the overlay still lines up.
        pixelScale *= ratio;
        var dpi = scale * DisplayDeviceHelper.DefaultDpi * ratio;

        var size = doc.PdfDocument.PageSizes[page];
        var offsetX = (int)Math.Round(region.X * pixelScale);
        var offsetY = (int)Math.Round(region.Y * pixelScale);
        var width = Math.Max(1, (int)Math.Round(region.Width * pixelScale));
        var height = Math.Max(1, (int)Math.Round(region.Height * pixelScale));
        var fullWidth = Math.Max(1, (int)Math.Round(size.Width * pixelScale));
        var fullHeight = Math.Max(1, (int)Math.Round(size.Height * pixelScale));

        try
        {
            using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            bitmap.SetResolution((float)dpi, (float)dpi);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                // PDF pages are painted on white; an alpha bitmap would let the
                // softer base page show through the tile's antialiased edges.
                graphics.Clear(Color.White);

                var flags = PdfRenderFlags.LimitImageCacheSize | PdfRenderFlags.Annotations |
                            PdfRenderFlags.ForPrinting | PdfRenderFlags.LcdText;

                lock (SyncRoot)
                {
                    doc.PdfDocument.Render(
                        page,
                        graphics,
                        (float)dpi,
                        (float)dpi,
                        new Rectangle(-offsetX, -offsetY, fullWidth, fullHeight),
                        flags);
                }
            }

            var source = bitmap.ToBitmapSource(dpi, dpi);
            source?.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A visible-region tile is at most this many pixels (~96 MB in BGRA).
    /// Only an exceptionally large screen reaches it at 1:1 density.
    /// </summary>
    private const long MaxTilePixels = 24_000_000L;
}
