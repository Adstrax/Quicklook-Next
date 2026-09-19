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

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace QuickLookNext.Helpers;

/// <summary>
/// v5.0.8: text recognition for the previewed image, using the OCR engine Windows already ships
/// (<see cref="OcrEngine"/>) - upstream issue
/// <see href="https://github.com/QL-Win/QuickLook/issues/1608">#1608</see> asked for it. No extra
/// dependency is needed: the app already carries the WinRT projection for the share feature, and
/// the engine itself only requires a language pack (Windows Settings → Time &amp; language).
/// <para>
/// Lives in the app rather than the image plugin: the plugin targets plain <c>net10.0-windows</c>,
/// so adding the OCR API there would drag the ~24 MB WinRT projection into the plugin folder.
/// </para>
/// </summary>
internal static class OcrRecognizer
{
    /// <summary>
    /// OCR is only useful on a readable bitmap - a 100 MP scan costs seconds and hundreds of MB
    /// without recognizing anything more than a 16 MP copy would.
    /// </summary>
    internal const long MaxPixels = 16_000_000L;

    /// <summary>Whether any OCR language pack is installed.</summary>
    internal static bool IsAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    /// <summary>
    /// Size to decode at: never larger than <see cref="OcrEngine.MaxImageDimension"/> on either side
    /// (the engine rejects bigger bitmaps) and never more than <see cref="MaxPixels"/> in total.
    /// </summary>
    internal static Size DecodeSize(Size fullSize)
    {
        if (fullSize.Width <= 0 || fullSize.Height <= 0)
            return fullSize;

        var scale = 1d;

        var longestSide = Math.Max(fullSize.Width, fullSize.Height);
        if (longestSide > OcrEngine.MaxImageDimension)
            scale = OcrEngine.MaxImageDimension / longestSide;

        var pixels = fullSize.Width * scale * fullSize.Height * scale;
        if (pixels > MaxPixels)
            scale *= Math.Sqrt(MaxPixels / pixels);

        return new Size(
            Math.Max(1d, Math.Floor(fullSize.Width * scale)),
            Math.Max(1d, Math.Floor(fullSize.Height * scale)));
    }

    /// <summary>
    /// Recognizes the text in <paramref name="path"/> and returns it (empty when the image holds no
    /// text). Throws <see cref="InvalidOperationException"/> when no OCR language is installed, and
    /// lets decoding failures bubble up so the caller can report them.
    /// </summary>
    internal static async Task<string> RecognizeAsync(string path)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
        {
            // The user's own languages may not have an OCR pack; any installed one beats failing.
            var fallback = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
            engine = fallback == null ? null : OcrEngine.TryCreateFromLanguage(fallback);
        }

        if (engine == null)
            throw new InvalidOperationException("no OCR language pack is installed");

        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var size = DecodeSize(new Size(decoder.PixelWidth, decoder.PixelHeight));

        // One code path: the transform scales to the bounded size (equal to the file size when no
        // scaling is needed) and RespectExifOrientation keeps phone photos the right way up -
        // otherwise the text would be recognized sideways.
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform
            {
                ScaledWidth = (uint)size.Width,
                ScaledHeight = (uint)size.Height,
                InterpolationMode = BitmapInterpolationMode.Linear,
            },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);

        var result = await engine.RecognizeAsync(bitmap);

        // OcrResult.Text joins every line with a space; keep the line structure instead - text
        // extraction is usually a prelude to pasting the result somewhere.
        return result.Lines.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
    }
}
