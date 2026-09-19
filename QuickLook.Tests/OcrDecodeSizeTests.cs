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

using QuickLookNext.Helpers;
using System;
using System.Windows;
using Windows.Media.Ocr;

namespace QuickLook.Tests;

/// <summary>
/// v5.0.8: the OCR engine rejects bitmaps larger than its own limit and is slow on huge ones, so
/// the decode size the recognizer asks for has to stay inside both bounds - and keep the aspect
/// ratio, otherwise text is stretched and recognized worse.
/// </summary>
internal class OcrDecodeSizeTests
{
    public void OrdinaryImagesAreDecodedAsIs()
    {
        var size = OcrRecognizer.DecodeSize(new Size(1920, 1080));

        Assert.Equal(1920d, size.Width, "width");
        Assert.Equal(1080d, size.Height, "height");
    }

    public void OversizedSidesAreScaledToTheEngineLimit()
    {
        var size = OcrRecognizer.DecodeSize(new Size(20000, 5000));

        Assert.True(size.Width <= OcrEngine.MaxImageDimension, "width within the engine limit");
        Assert.True(size.Height <= OcrEngine.MaxImageDimension, "height within the engine limit");
        Assert.True(size.Width * size.Height <= OcrRecognizer.MaxPixels, "pixels within the cap");
        Assert.True(Math.Abs(size.Width / size.Height - 4d) < 0.01, "aspect ratio kept (4:1)");
    }

    public void TotalPixelsAreCapped()
    {
        var size = OcrRecognizer.DecodeSize(new Size(9000, 9000));

        Assert.True(size.Width * size.Height <= OcrRecognizer.MaxPixels, "pixels within the cap");
        Assert.True(size.Width < 9000, "scaled down");
        Assert.True(Math.Abs(size.Width - size.Height) < 1, "a square stays square");
    }

    public void EmptySizesAreReturnedUnchanged()
    {
        var size = OcrRecognizer.DecodeSize(new Size(0, 0));

        Assert.Equal(0d, size.Width, "width unchanged");
        Assert.Equal(0d, size.Height, "height unchanged");
    }

    /// <summary>
    /// v5.1.0: small pictures are enlarged before recognition. Measured on a 560x150 screenshot
    /// with 14 px text: the Chinese line came back as "138 佣佣 1 1 1 1", and as
    /// "13800001111" once the same picture was handed over at twice the size.
    /// </summary>
    public void SmallImagesAreEnlargedBeforeRecognition()
    {
        var size = OcrRecognizer.DecodeSize(new Size(560, 150));

        Assert.Equal(1120d, size.Width, "width doubled");
        Assert.Equal(300d, size.Height, "height doubled");
    }

    public void LargerImagesAreStillLeftAtTheirOwnSize()
    {
        // The page that started the OCR report: 1264x1522 is read well as it is, and scaling it
        // would only cost time.
        var size = OcrRecognizer.DecodeSize(new Size(1264, 1522));

        Assert.Equal(1264d, size.Width, "width unchanged");
        Assert.Equal(1522d, size.Height, "height unchanged");
    }
}
