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

namespace QuickLook.Tests;

/// <summary>
/// v5.0.11: how the OCR feature picks an engine. Windows ships several, and the one the user's
/// profile points at is often the wrong one for the picture at hand - a Chinese page read by the
/// English engine came back as 33 Latin characters of nonsense. These tests pin the rule that
/// decides the winner: the answer with the most characters of the language's own script wins.
/// </summary>
internal class OcrLanguageTests
{
    // A line from the report that started this ("不要去回应负能量" sheet).
    private const string Chinese =
        "人生建议：不要去搭理一切负能量，回应就会与之纠缠受其损耗，拒绝自我折磨受罪。";

    // What the English engine made of that same page.
    private const string ChineseReadByEnglishEngine = "aaaaeæa\no\n(fifi,\n(Räih(+/Åäih,";

    private const string English =
        "Life advice: do not engage with negative energy, it only drains you.";

    public void ChineseTextWinsForTheChineseEngine()
    {
        var chinese = OcrRecognizer.Score("zh-Hans-CN", Chinese);
        var englishReading = OcrRecognizer.Score("en-US", ChineseReadByEnglishEngine);

        Assert.True(chinese > englishReading,
            $"the Chinese answer ({chinese}) must beat the nonsense the English engine produced " +
            $"({englishReading})");
        Assert.Equal(0, OcrRecognizer.CountInScript("zh-Hans-CN", ChineseReadByEnglishEngine),
            "the English answer holds no Han character at all");
        Assert.True(
            OcrRecognizer.CountInScript("zh-Hans-CN", Chinese) >=
            0.8 * OcrRecognizer.CountCharacters(Chinese),
            "while the Chinese answer is mostly Han (the rest is punctuation)");
    }

    public void EnglishTextWinsForTheEnglishEngine()
    {
        var english = OcrRecognizer.Score("en-US", English);
        var chinese = OcrRecognizer.Score("zh-Hans-CN", English);

        Assert.True(english > chinese, $"English ({english}) must beat Chinese ({chinese})");
        Assert.True(chinese < 0, "Latin letters count against a Han-script language");
    }

    public void DigitsAndPunctuationScoreTheSameEverywhere()
    {
        const string numbers = "2026-09-19 12:34:56 (5.0.10) + 42%";

        Assert.Equal(0, OcrRecognizer.Score("en-US", numbers), "English");
        Assert.Equal(0, OcrRecognizer.Score("zh-Hans-CN", numbers), "Chinese");
        Assert.Equal(0, OcrRecognizer.Score("ru-RU", numbers), "Russian");
    }

    public void ThePrimarySubtagDecidesTheScript()
    {
        Assert.True(OcrRecognizer.IsInScript("zh-Hant-TW", '去'), "traditional Chinese");
        Assert.True(OcrRecognizer.IsInScript("ja-JP", 'あ'), "Japanese kana");
        Assert.True(OcrRecognizer.IsInScript("ja-JP", '漢'), "Japanese kanji");
        Assert.True(OcrRecognizer.IsInScript("ko-KR", '한'), "Korean hangul");
        Assert.True(OcrRecognizer.IsInScript("ru-RU", 'д'), "Russian Cyrillic");
        Assert.True(OcrRecognizer.IsInScript("el-GR", 'δ'), "Greek");
        Assert.True(OcrRecognizer.IsInScript("en-US", 'a'), "Latin");
        Assert.True(OcrRecognizer.IsInScript("xx-YY", 'a'), "an unknown language falls back to Latin");

        Assert.False(OcrRecognizer.IsInScript("zh-Hans-CN", 'a'), "Latin is foreign to Chinese");
        Assert.False(OcrRecognizer.IsInScript("en-US", '去'), "Han is foreign to English");
    }

    public void ASingleLanguagePackStillWorks()
    {
        // With one pack installed CandidateLanguages yields exactly that one engine, so the score
        // never has to decide anything - the run must not depend on having two.
        Assert.True(OcrRecognizer.MaxLanguagesTried >= 1, "at least one engine is always tried");
        Assert.True(OcrRecognizer.Score("zh-Hans-CN", Chinese) > 0, "the one engine still scores");
    }

    public void ChineseWordsAreJoinedWithoutSpaces()
    {
        // The engine hands Chinese back one glyph per "word"; pasting "不 要 去 回 应" is useless.
        var text = OcrRecognizer.JoinWords(new[] { "不", "要", "去", "回", "应", "负", "能", "量", "。" });

        Assert.Equal("不要去回应负能量。", text, "Chinese is written without spaces");
    }

    public void FullWidthPunctuationStaysAttachedToTheText()
    {
        // The engine also emits ， and 、 as separate words; "能量 ， 回应" must not survive.
        var text = OcrRecognizer.JoinWords(new[] { "能量", "，", "回应", "、", "损耗" });

        Assert.Equal("能量，回应、损耗", text, "full-width punctuation attaches to its text");
    }

    public void LatinWordsKeepTheirSpacesAndPunctuationStaysAttached()
    {
        var text = OcrRecognizer.JoinWords(new[] { "Life", "advice:", "do", "not", "engage." });

        Assert.Equal("Life advice: do not engage.", text, "Latin words stay separated");
    }

    public void MixedTextKeepsTheSpaceBetweenScripts()
    {
        var text = OcrRecognizer.JoinWords(new[] { "使用", "Windows", "10", "的", "设置" });

        Assert.Equal("使用 Windows 10 的设置", text, "CJK - Latin keeps its space");
    }
}
