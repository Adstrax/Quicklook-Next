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
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using QuickLook.Common.Helpers;
using Windows.Graphics.Imaging;
using Windows.Globalization;
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
/// <para>
/// v5.0.11: one engine is not enough. <see cref="OcrEngine.TryCreateFromUserProfileLanguages"/>
/// answers with the user's Windows display language, which on a Chinese system with an English
/// UI (or the other way round) is exactly the wrong one: a page of Chinese came back as 33 Latin
/// characters of nonsense, because the English engine was asked to read Chinese. Every installed
/// engine is now tried and the result whose characters actually belong to that language's script
/// wins (see <see cref="Score"/>).
/// </para>
/// </summary>
internal static class OcrRecognizer
{
    /// <summary>
    /// OCR is only useful on a readable bitmap - a 100 MP scan costs seconds and hundreds of MB
    /// without recognizing anything more than a 16 MP copy would.
    /// </summary>
    internal const long MaxPixels = 16_000_000L;

    /// <summary>
    /// Upper bound on how many engines a single recognition may run. Every installed pack costs one
    /// pass over the bitmap, and a machine rarely has more than two or three.
    /// </summary>
    internal const int MaxLanguagesTried = 4;

    /// <summary>One engine's answer, with the score that decided the winner.</summary>
    internal sealed class Attempt
    {
        public string Language { get; init; }

        public int Score { get; init; }

        /// <summary>Characters of the language's own script found in the text.</summary>
        public int InScript { get; init; }

        /// <summary>Characters the text has in total (without whitespace).</summary>
        public int Characters { get; init; }

        public string Text { get; init; }
    }

    /// <summary>What the last <see cref="RecognizeAsync"/> run tried - diagnostics and tests only.</summary>
    internal static IReadOnlyList<Attempt> LastAttempts { get; private set; } = Array.Empty<Attempt>();

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
        var candidates = CandidateLanguages();
        if (candidates.Count == 0)
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

        var attempts = new List<Attempt>();
        Attempt best = null;

        foreach (var language in candidates)
        {
            try
            {
                var engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine == null)
                    continue;

                var result = await engine.RecognizeAsync(bitmap);
                var text = JoinLines(result);
                var attempt = new Attempt
                {
                    Language = language.LanguageTag,
                    Score = Score(language.LanguageTag, text),
                    InScript = CountInScript(language.LanguageTag, text),
                    Characters = CountCharacters(text),
                    Text = text,
                };

                attempts.Add(attempt);

                // Strictly greater: equal scores keep the earlier candidate, and the list starts
                // with the languages the user actually has in Windows.
                if (best == null || attempt.Score > best.Score)
                    best = attempt;
            }
            catch (Exception e)
            {
                // One broken language pack must not take the whole feature down.
                ProcessHelper.WriteLog($"Text recognition with {language.LanguageTag} failed: {e}");
            }
        }

        LastAttempts = attempts;

        if (best == null)
            throw new InvalidOperationException("no OCR language pack could be used");

        return best.Text;
    }

    /// <summary>
    /// OcrResult.Text joins every line with a space; keep the line structure instead - text
    /// extraction is usually a prelude to pasting the result somewhere.
    /// <para>
    /// v5.0.11: the lines are built from <see cref="OcrLine.Words"/> rather than from
    /// <c>OcrLine.Text</c>, because the engine treats every CJK glyph as its own word and joins
    /// them with spaces: "不要去回应" came out as "不 要 去 回 应", which is useless to paste.
    /// </para>
    /// </summary>
    private static string JoinLines(OcrResult result)
        => result.Lines.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine,
                result.Lines.Select(line => JoinWords(line.Words.Select(word => word.Text))));

    /// <summary>
    /// One line, with a separator kept only where the writing system uses one: never between two
    /// CJK glyphs (Chinese, Japanese and Korean are written without spaces), everywhere else -
    /// including between CJK and Latin, where a space is the convention.
    /// </summary>
    internal static string JoinWords(IEnumerable<string> words)
    {
        var text = new StringBuilder();

        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word))
                continue;

            if (text.Length > 0 && !char.IsWhiteSpace(text[^1]) &&
                !(IsWrittenWithoutSpaces(text[^1]) && IsWrittenWithoutSpaces(word[0])))
                text.Append(' ');

            text.Append(word);
        }

        return text.ToString();
    }

    internal static bool IsCjk(char c) => IsHan(c) || IsKana(c) || IsHangul(c);

    /// <summary>
    /// CJK glyphs plus the punctuation that goes with them (、。！？ and the full-width forms):
    /// the engine hands those back as words of their own as well, and "能量 ， 回应" is just as
    /// wrong as "不 要 去 回 应".
    /// </summary>
    private static bool IsWrittenWithoutSpaces(char c)
        => IsCjk(c) || InRange(c, 0x3000, 0x303F) || InRange(c, 0xFF00, 0xFFEF);

    /// <summary>
    /// Engines to try, in order: the user's own OCR language first (a tie - a picture of digits,
    /// say, is scored the same by everyone - then resolves to what the user actually reads), then
    /// every other installed pack.
    /// </summary>
    private static List<Language> CandidateLanguages()
    {
        var candidates = new List<Language>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(Language language)
        {
            if (language != null && !string.IsNullOrEmpty(language.LanguageTag) &&
                seen.Add(language.LanguageTag))
                candidates.Add(language);
        }

        try
        {
            Add(OcrEngine.TryCreateFromUserProfileLanguages()?.RecognizerLanguage);
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"Querying the user's OCR language failed: {e}");
        }

        foreach (var language in OcrEngine.AvailableRecognizerLanguages)
            Add(language);

        return candidates.Take(MaxLanguagesTried).ToList();
    }

    /// <summary>
    /// How believable an engine's answer is: characters of the language's own script are worth
    /// <see cref="InScriptWeight"/> points, characters of another script cost
    /// <see cref="ForeignPenalty"/> each. Digits, punctuation and symbols count as neither - they
    /// are read the same way by every engine.
    /// <para>
    /// Reading Chinese with the English engine produces a handful of Latin letters, while reading
    /// it with the Chinese engine produces hundreds of Han characters, so the two answers are
    /// easy to tell apart; the same holds in the other direction.
    /// </para>
    /// </summary>
    internal static int Score(string languageTag, string text)
    {
        var inScript = CountInScript(languageTag, text);
        var foreign = CountLetters(text) - inScript;

        return inScript * InScriptWeight - foreign * ForeignPenalty;
    }

    internal const int InScriptWeight = 10;
    internal const int ForeignPenalty = 1;

    /// <summary>Characters of <paramref name="text"/> that belong to the language's own script.</summary>
    internal static int CountInScript(string languageTag, string text)
        => text.Count(c => IsInScript(languageTag, c));

    /// <summary>Non-whitespace characters.</summary>
    internal static int CountCharacters(string text)
        => text.Count(c => !char.IsWhiteSpace(c));

    /// <summary>Letters, whatever the script.</summary>
    private static int CountLetters(string text)
        => text.Count(c => !char.IsWhiteSpace(c) && !IsNeutral(c));

    /// <summary>
    /// Whether <paramref name="c"/> belongs to the script the language is written in. Languages
    /// outside the list below are treated as Latin, which is what Windows ships for the rest.
    /// </summary>
    internal static bool IsInScript(string languageTag, char c)
    {
        switch (PrimarySubtag(languageTag))
        {
            case "zh":
            case "yue":
                return IsHan(c);

            case "ja":
                return IsHan(c) || IsKana(c);

            case "ko":
                return IsHan(c) || IsHangul(c);

            case "ru":
            case "uk":
            case "bg":
            case "be":
            case "sr":
            case "mk":
            case "kk":
            case "ky":
            case "tg":
            case "mn":
                return InRange(c, 0x0400, 0x052F) || InRange(c, 0x2DE0, 0x2DFF) ||
                       InRange(c, 0xA640, 0xA69F);

            case "el":
                return InRange(c, 0x0370, 0x03FF) || InRange(c, 0x1F00, 0x1FFF);

            case "ar":
            case "fa":
            case "ur":
            case "ps":
            case "sd":
            case "ug":
                return InRange(c, 0x0600, 0x06FF) || InRange(c, 0x0750, 0x077F) ||
                       InRange(c, 0x08A0, 0x08FF) || InRange(c, 0xFB50, 0xFDFF) ||
                       InRange(c, 0xFE70, 0xFEFF);

            case "he":
                return InRange(c, 0x0590, 0x05FF) || InRange(c, 0xFB1D, 0xFB4F);

            case "th":
                return InRange(c, 0x0E00, 0x0E7F);

            case "hi":
            case "mr":
            case "ne":
            case "sa":
                return InRange(c, 0x0900, 0x097F) || InRange(c, 0xA8E0, 0xA8FF);

            default:
                return IsLatin(c);
        }
    }

    private static string PrimarySubtag(string languageTag)
    {
        if (string.IsNullOrEmpty(languageTag))
            return string.Empty;

        var dash = languageTag.IndexOf('-');
        return (dash < 0 ? languageTag : languageTag[..dash]).ToLowerInvariant();
    }

    private static bool IsNeutral(char c)
        => char.IsDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c) || char.IsSeparator(c) ||
           char.IsControl(c);

    private static bool IsLatin(char c)
        => InRange(c, 'a', 'z') || InRange(c, 'A', 'Z') || InRange(c, 0x00C0, 0x024F);

    private static bool IsHan(char c)
        => InRange(c, 0x3400, 0x4DBF) || InRange(c, 0x4E00, 0x9FFF) ||
           InRange(c, 0xF900, 0xFAFF);

    private static bool IsKana(char c)
        => InRange(c, 0x3040, 0x30FF) || InRange(c, 0x31F0, 0x31FF);

    private static bool IsHangul(char c)
        => InRange(c, 0x1100, 0x11FF) || InRange(c, 0x3130, 0x318F) ||
           InRange(c, 0xA960, 0xA97F) || InRange(c, 0xAC00, 0xD7FF);

    private static bool InRange(char c, int low, int high) => c >= low && c <= high;
}
