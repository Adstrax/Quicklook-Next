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

using QuickLook.Plugin.Shared;
using System.IO;

namespace QuickLook.Tests;

/// <summary>
/// The video viewer keeps metadata and header answers in this cache across
/// previews. A cache that ignores the file version is worse than no cache, and
/// one that clears itself loses every warm entry at once - both are pinned here.
/// </summary>
internal class BoundedFileCacheTests : SettingsFixture
{
    public void AnUnchangedFileIsServedFromTheCache()
    {
        var cache = Cache();
        var stamp = Stamp(1);
        cache.Set("a", stamp, "value");

        Assert.True(cache.TryGet("a", stamp, out var value), "the unchanged entry is a hit");
        Assert.Equal("value", value, "the cached value is returned");
    }

    public void AChangedFileInvalidatesItsOldEntry()
    {
        var cache = Cache();
        cache.Set("a", Stamp(1), "old");

        Assert.False(cache.TryGet("a", Stamp(2), out _), "a newer file cannot read the old answer");
        Assert.Equal(0, cache.Count, "the stale entry is dropped immediately");
        Assert.Equal(0L, cache.ApproximateBytes, "and its size leaves the budget with it");
    }

    public void TheLeastRecentlyUsedEntryIsEvictedFirst()
    {
        var cache = new BoundedFileCache<string>(3, 100, static value => value.Length);
        cache.Set("a", Stamp(1), "a");
        cache.Set("b", Stamp(1), "b");
        cache.Set("c", Stamp(1), "c");

        Assert.True(cache.TryGet("a", Stamp(1), out _), "touching a makes it the most recent");

        cache.Set("d", Stamp(1), "d");

        Assert.False(cache.TryGet("b", Stamp(1), out _), "the untouched entry is evicted");
        Assert.True(cache.TryGet("a", Stamp(1), out _), "the touched entry survives");
        Assert.True(cache.TryGet("c", Stamp(1), out _), "the last entry survives");
        Assert.True(cache.TryGet("d", Stamp(1), out _), "the new entry is present");
    }

    public void TheByteBudgetEvictsUntilItIsSatisfied()
    {
        var cache = new BoundedFileCache<string>(10, 6, static value => value.Length);
        cache.Set("a", Stamp(1), "aaa");
        cache.Set("b", Stamp(1), "bbb");

        Assert.True(cache.TryGet("a", Stamp(1), out _), "a becomes the most recent entry");

        cache.Set("c", Stamp(1), "ccc");

        Assert.False(cache.TryGet("b", Stamp(1), out _), "the oldest entry pays for the new one");
        Assert.True(cache.TryGet("a", Stamp(1), out _), "the recently used entry survives");
        Assert.Equal(6L, cache.ApproximateBytes, "the cache stays exactly at its byte budget");
    }

    public void AnOversizedValueIsUsedButNotRetained()
    {
        var cache = new BoundedFileCache<string>(10, 100, static value => value.Length, maxSingleBytes: 3);

        cache.Set("a", Stamp(1), "1234");

        Assert.False(cache.TryGet("a", Stamp(1), out _), "one huge value is not cached");
        Assert.Equal(0, cache.Count, "and it does not displace ordinary entries");
    }

    public void ReplacingAValueAdjustsTheByteTotal()
    {
        var cache = new BoundedFileCache<string>(10, 100, static value => value.Length);
        cache.Set("a", Stamp(1), "aa");
        cache.Set("a", Stamp(1), "aaaa");

        Assert.Equal(1, cache.Count, "a path has one entry");
        Assert.Equal(4L, cache.ApproximateBytes, "the old size is subtracted");
        Assert.True(cache.TryGet("a", Stamp(1), out var value), "the replacement is readable");
        Assert.Equal("aaaa", value, "the newer value wins");
    }

    public void ClearingRemovesEntriesAndResetsTheBudget()
    {
        var cache = Cache();
        cache.Set("a", Stamp(1), "value");

        cache.Clear();

        Assert.Equal(0, cache.Count, "no entries remain");
        Assert.Equal(0L, cache.ApproximateBytes, "the byte counter is reset");
        Assert.False(cache.TryGet("a", Stamp(1), out _), "a cleared entry is gone");
    }

    public void AnEvictedValueIsReportedForCleanup()
    {
        var evicted = new System.Collections.Generic.List<string>();
        var cache = new BoundedFileCache<string>(
            2, 100, static value => value.Length, onEvicted: evicted.Add);

        cache.Set("a", Stamp(1), "a");
        cache.Set("b", Stamp(1), "b");
        cache.Set("c", Stamp(1), "c");

        Assert.Equal(1, evicted.Count, "the least recently used value is reported");
        Assert.Equal("a", evicted[0], "and it is the value that left the cache");
    }

    public void AStaleValueIsReportedBeforeItsReplacementIsUsed()
    {
        var evicted = new System.Collections.Generic.List<string>();
        var cache = new BoundedFileCache<string>(
            2, 100, static value => value.Length, onEvicted: evicted.Add);
        cache.Set("a", Stamp(1), "old");

        Assert.False(cache.TryGet("a", Stamp(2), out _), "the old stamp is a miss");
        Assert.Equal("old", evicted[0], "the stale value is cleaned up");
    }

    public void AnOversizedValueReportsThatItWasNotStored()
    {
        var cache = new BoundedFileCache<string>(
            10, 100, static value => value.Length, maxSingleBytes: 3);

        Assert.False(cache.Set("a", Stamp(1), "1234"), "the value is too large to retain");
        Assert.True(cache.Set("b", Stamp(1), "123"), "an ordinary value is stored");
    }

    public void AFileStampTracksTheFileOnDisk()
    {
        var path = Path.Combine(Root, "stamp.bin");
        File.WriteAllBytes(path, new byte[7]);

        Assert.True(FileCacheStamp.TryCapture(path, out var stamp), "an existing file has a stamp");
        Assert.Equal(7L, stamp.Length, "the stamp records the length");

        File.Delete(path);
        Assert.False(FileCacheStamp.TryCapture(path, out _), "a missing file has no stamp");
    }

    private static BoundedFileCache<string> Cache()
        => new(8, 100, static value => value.Length);

    private static FileCacheStamp Stamp(long ticks)
        => new(1, ticks);
}
