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
using Match = QuickLookNext.NativeMethods.QuickLookNext;

namespace QuickLook.Tests;

/// <summary>
/// v5.6.12: the middle button finds the item under the cursor by the name Explorer draws for it, so the
/// matcher has to survive everything the shell does to that name - dropping a known extension, appending
/// the rest of a details-view row, and cutting a long name short with an ellipsis of its own. The last
/// one is what made a middle click on a file with a long name do nothing at all.
/// </summary>
internal class ItemNameMatchTests
{
    private const string LongName = "2026-10-12-产品评审会议纪要-最终版-含附件清单-修订记录-v3.docx";

    public void TheFileNameItselfIsAnExactMatch()
    {
        Assert.Equal(Match.ItemNameMatch.Exact, Match.MatchItemName("photo.jpg", "photo.jpg"), "same name");
        Assert.Equal(Match.ItemNameMatch.Exact, Match.MatchItemName("photo.jpg", "PHOTO.JPG"), "case folded");
    }

    public void TheDisplayNameWithoutItsExtensionStillMatches()
    {
        Assert.Equal(Match.ItemNameMatch.DisplayName, Match.MatchItemName("photo.jpg", "photo"),
            "Explorer hides known extensions");
    }

    public void ADottedNameStillMatchesThroughItsDisplayName()
    {
        // The report drops the extension, and the name has dots of its own - the reason "long names" broke
        // the middle click: stripping "the extension" from the report cut it at the last of those dots.
        Assert.Equal(Match.ItemNameMatch.DisplayName,
            Match.MatchItemName("产品需求文档 v2.1 评审版 最终稿.png", "产品需求文档 v2.1 评审版 最终稿"),
            "version number in the name");
        Assert.Equal(Match.ItemNameMatch.DisplayName,
            Match.MatchItemName("2026.10.12 会议纪要.docx", "2026.10.12 会议纪要"), "date in the name");
    }

    public void AWholeDetailsRowMatchesThroughItsLeadingFileName()
    {
        Assert.Equal(Match.ItemNameMatch.RowPrefix,
            Match.MatchItemName("photo.jpg", "photo.jpg   2026-10-07 10:31   JPEG image"), "details row");
    }

    public void AnEllipsisedLongNameMatches()
    {
        Assert.Equal(Match.ItemNameMatch.TruncatedLabel,
            Match.MatchItemName(LongName, "2026-10-12-产品评审会议纪要-最…"), "unicode ellipsis");
        Assert.Equal(Match.ItemNameMatch.TruncatedLabel,
            Match.MatchItemName(LongName, "2026-10-12-产品评审会议纪要-最终版-含附件清单..."),
            "three dots");
    }

    public void ANameCutShortWithoutAMarkerMatchesToo()
    {
        Assert.Equal(Match.ItemNameMatch.TruncatedLabel,
            Match.MatchItemName(LongName, "2026-10-12-产品评审会议纪要-最终版"), "no marker");
    }

    public void AShortLabelIsNotTreatedAsATruncation()
    {
        // A handful of characters would match nearly anything, so below the length guard it stays a miss.
        Assert.Equal(Match.ItemNameMatch.None, Match.MatchItemName(LongName, "2026"), "too short");
    }

    public void SomethingElseEntirelyDoesNotMatch()
    {
        Assert.Equal(Match.ItemNameMatch.None, Match.MatchItemName("photo.jpg", "video.mp4"), "other file");
        Assert.Equal(Match.ItemNameMatch.None, Match.MatchItemName("", "photo.jpg"), "no item name");
        Assert.Equal(Match.ItemNameMatch.None, Match.MatchItemName("photo.jpg", ""), "no reported name");
    }

    public void AnUnambiguousTruncatedLabelFindsItsItem()
    {
        string[] items = ["a.txt", LongName, "b.txt"];

        Assert.Equal(1, Match.FindItemIndex(items, "2026-10-12-产品评审会议纪要-最…"), "the only candidate");
    }

    public void AnAmbiguousTruncatedLabelFindsNothing()
    {
        // Both files share the visible beginning of the label; previewing the wrong one would be worse
        // than doing nothing, so the press is handed back to Explorer instead.
        string[] items = [LongName, "2026-10-12-产品评审会议纪要-最终版-含附件清单-修订记录-v4.docx"];

        Assert.Equal(-1, Match.FindItemIndex(items, "2026-10-12-产品评审会议纪要-最…"), "ambiguous");
    }

    public void ExactAndDisplayNameMatchesBeatATruncatedOne()
    {
        string[] items = [LongName, "photo.jpg"];

        Assert.Equal(1, Match.FindItemIndex(items, "photo.jpg"), "exact wins");
        Assert.Equal(1, Match.FindItemIndex(items, "photo"), "display name wins");
    }

    public void TwoFilesThatOnlyDifferByTheirExtensionAreNotGuessed()
    {
        // v6.1.3: Explorer hides known extensions, so both files are reported as "2024论文-V2".
        // The middle button used to preview the first one - the Word document - whichever icon
        // was pressed. The full name still identifies either file exactly.
        string[] items = ["2024论文-V2.docx", "2024论文-V2.pdf"];

        Assert.Equal(-1, Match.FindItemIndex(items, "2024论文-V2"),
            "an ambiguous display name is not guessed");
        Assert.Equal(1, Match.FindItemIndex(items, "2024论文-V2.pdf"), "the full name picks the PDF");
        Assert.Equal(0, Match.FindItemIndex(items, "2024论文-V2.docx"), "the full name picks the DOCX");
    }

    public void AFolderAndAZipWithTheSameVisibleNameAreNotGuessed()
    {
        // The folder is an exact name match while Explorer hides ".zip" from the archive. Returning the
        // exact folder immediately would skip position resolution and preview the folder for both icons.
        string[] items = ["附件1-4", "附件1-4.zip"];

        Assert.Equal(-1, Match.FindItemIndex(items, "附件1-4"),
            "the pressed position must distinguish the folder from the ZIP");
        Assert.Equal(1, Match.FindItemIndex(items, "附件1-4.zip"), "shown extensions still identify the ZIP");
    }

    public void AccessibilityIndexResolvesTheZipWhenShellPositionsAreUnavailable()
    {
        string[] items = ["其他文件", "附件1-4", "另一个文件", "附件1-4.zip"];

        Assert.Equal(3, Match.PickAccessibilityCandidate(items, "附件1-4", 3),
            "Explorer's selected accessibility item identifies the ZIP");
        Assert.Equal(-1, Match.PickAccessibilityCandidate(items, "附件1-4", 0),
            "an unrelated accessibility item is not accepted");
    }

    public void AnEmptyFolderFindsNothing()
    {
        Assert.Equal(-1, Match.FindItemIndex(Array.Empty<string>(), "photo.jpg"), "no items");
    }

    public void ThePressedPositionBeatsAnOrderDifferentAccessibilityIndex()
    {
        // Explorer reported the same visible name for both items. Its accessibility index
        // happened to point at the folder, while the ZIP was the item actually under the
        // pointer. The shell-view position must win so the middleware previews the ZIP.
        (int Index, int X, int Y)[] positions =
        [
            (6, 1244, 753),
            (10, 1979, 753),
        ];

        var picked = Match.PickCandidateNearestToPoint(
            positions, 1980, 755, out var nearest, out var second);

        Assert.Equal(10, picked, "the item under the pointer wins");
        Assert.True(nearest < second, "its position is measurably closer than the other candidate");
    }

    public void TwoEquallyClosePositionsAreStillNotGuessed()
    {
        (int Index, int X, int Y)[] positions =
        [
            (1, 100, 100),
            (2, 102, 100),
        ];

        Assert.Equal(-1, Match.PickCandidateNearestToPoint(
            positions, 101, 100, out _, out _), "the margin is too small to choose safely");
    }
}
