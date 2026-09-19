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
using QuickLook.Common.Helpers;
using System.IO;

namespace QuickLook.Tests;

/// <summary>
/// v5.0.7: the data/cache panel deletes files, so the whitelist is load bearing - sign-in data,
/// settings, statistics and the log have to survive a cleanup, and a file that is in use must be
/// reported rather than throwing.
/// </summary>
internal class CacheUsageTests : SettingsFixture
{
    private string _data;
    private string _temp;

    public override void Setup()
    {
        base.Setup();

        _data = Path.Combine(Root, "data");
        _temp = Path.Combine(Root, "temp");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_temp);
    }

    public void CacheAndDataAreMeasuredSeparately()
    {
        var profile = Path.Combine(_data, "WebView2_Data", "EBWebView");
        WriteFile(Path.Combine(profile, "GrShaderCache", "shader.bin"), 4096);
        WriteFile(Path.Combine(profile, "Default", "Cache", "http.bin"), 2048);
        WriteFile(Path.Combine(profile, "Default", "Cookies"), 1024);      // kept
        WriteFile(Path.Combine(_data, "QuickLookNext.config"), 512);       // kept
        WriteFile(Path.Combine(_data, "QuickLookNext.Exception.log"), 128); // kept
        WriteFile(Path.Combine(_temp, "QuickLookNext.Update", "pkg.zip"), 8192);

        Assert.Equal(4096L + 2048L + 8192L, AppDataUsage.CacheBytes(_data, _temp), "cache bytes");
        Assert.Equal(1024L + 512L + 128L, AppDataUsage.DataBytes(_data), "data bytes");
        Assert.Equal(
            AppDataUsage.CacheBytes(_data, _temp) + AppDataUsage.DataBytes(_data),
            AppDataUsage.TotalBytes(_data, _temp),
            "total bytes");
    }

    public void ClearingRemovesOnlyTheWhitelistedCaches()
    {
        var profile = Path.Combine(_data, "WebView2_Data", "EBWebView");
        var shaderCache = Path.Combine(profile, "GrShaderCache", "shader.bin");
        var httpCache = Path.Combine(profile, "Default", "Cache", "http.bin");
        var cookies = Path.Combine(profile, "Default", "Cookies");
        var config = Path.Combine(_data, "QuickLookNext.config");
        var updatePackage = Path.Combine(_temp, "QuickLookNext.Update", "pkg.zip");
        var updateScript = Path.Combine(_temp, "QuickLookNext-update.cmd");

        WriteFile(shaderCache, 4096);
        WriteFile(httpCache, 2048);
        WriteFile(cookies, 1024);
        WriteFile(config, 512);
        WriteFile(updatePackage, 8192);
        WriteFile(updateScript, 64);

        var result = AppDataUsage.ClearCache(_data, _temp);

        Assert.Equal(4096L + 2048L + 8192L + 64L, result.FreedBytes, "freed bytes");
        Assert.Equal(0, result.FailedItems, "nothing was in use");
        Assert.True(result.Complete, "cleanup complete");

        Assert.False(File.Exists(shaderCache), "shader cache is gone");
        Assert.False(File.Exists(httpCache), "http cache is gone");
        Assert.False(File.Exists(updatePackage), "leftover update package is gone");
        Assert.False(File.Exists(updateScript), "leftover update script is gone");
        Assert.True(File.Exists(cookies), "sign-in data survives");
        Assert.True(File.Exists(config), "settings survive");
    }

    /// <summary>
    /// v5.1.0: the log is data, so "clear cache" keeps it - dropping it is its own button, and it
    /// must take the rotated copy with it and leave everything else alone.
    /// </summary>
    public void ClearingTheLogRemovesTheLogAndItsRotatedCopyOnly()
    {
        var log = Path.Combine(_data, ProcessHelper.LogFileName);
        var previous = Path.Combine(_data, ProcessHelper.PreviousLogFileName);
        var config = Path.Combine(_data, "QuickLookNext.config");

        WriteFile(log, 2048);
        WriteFile(previous, 1024);
        WriteFile(config, 512);

        var result = AppDataUsage.ClearLogs(_data);

        Assert.Equal(2048L + 1024L, result.FreedBytes, "freed bytes");
        Assert.True(result.Complete, "nothing was in use");
        Assert.False(File.Exists(log), "the log is gone");
        Assert.False(File.Exists(previous), "the rotated copy is gone");
        Assert.True(File.Exists(config), "settings survive");
    }

    public void ClearingAnAbsentLogChangesNothing()
    {
        var result = AppDataUsage.ClearLogs(_data);

        Assert.Equal(0L, result.FreedBytes, "nothing freed");
        Assert.Equal(0, result.FailedItems, "nothing failed");
    }

    /// <summary>
    /// v5.1.0: the diagnostic log used to grow forever. Past the cap the file is moved aside once,
    /// so what is being appended to stays small.
    /// </summary>
    public void AnOversizedLogIsRotatedInsteadOfGrowingForever()
    {
        var log = ProcessHelper.LogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(log));
        File.WriteAllBytes(log, new byte[1024 * 1024 + 1]);

        ProcessHelper.WriteLog("rotation probe");

        Assert.True(File.Exists(ProcessHelper.PreviousLogPath), "the oversized log was moved aside");
        Assert.True(new FileInfo(ProcessHelper.PreviousLogPath).Length > 1024 * 1024,
            "and kept its content");
        Assert.True(new FileInfo(log).Length < 4096, "the new log starts small");
        Assert.True(File.ReadAllText(log).Contains("rotation probe"),
            "and holds the message that triggered the rotation");
    }

    public void LeftoverProfilesCountAsCacheButKeepTheProfile()
    {
        // A profile rotation (v5.0.0 repair) can leave WebView2_Data_1 behind; its caches are
        // disposable, but the folder itself is not ours to delete during a cache cleanup.
        var leftover = Path.Combine(_data, "WebView2_Data_1", "EBWebView");
        WriteFile(Path.Combine(leftover, "ShaderCache", "s.bin"), 1024);
        WriteFile(Path.Combine(leftover, "Default", "Cookies"), 256);

        Assert.Equal(1024L, AppDataUsage.CacheBytes(_data, _temp), "leftover cache is counted");

        var result = AppDataUsage.ClearCache(_data, _temp);

        Assert.Equal(1024L, result.FreedBytes, "leftover cache is cleared");
        Assert.True(Directory.Exists(Path.Combine(_data, "WebView2_Data_1")), "the profile folder itself stays");
        Assert.True(File.Exists(Path.Combine(leftover, "Default", "Cookies")), "its data stays");
    }

    public void FilesInUseAreReportedInsteadOfThrowing()
    {
        var locked = Path.Combine(_data, "WebView2_Data", "EBWebView", "Default", "Cache", "locked.bin");
        WriteFile(locked, 2048);

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = AppDataUsage.ClearCache(_data, _temp);

            Assert.Equal(0L, result.FreedBytes, "nothing could be freed");
            Assert.Equal(1, result.FailedItems, "the in-use file is reported");
            Assert.False(result.Complete, "cleanup reports itself as incomplete");
        }

        Assert.True(File.Exists(locked), "the in-use file is still there");
    }

    public void MissingFoldersAreHarmless()
    {
        Assert.Equal(0L, AppDataUsage.CacheBytes(Path.Combine(Root, "nope"), _temp), "no cache anywhere");
        Assert.Equal(0L, AppDataUsage.DataBytes(Path.Combine(Root, "nope")), "no data folder");

        var result = AppDataUsage.ClearCache(Path.Combine(Root, "nope"), Path.Combine(Root, "nope2"));
        Assert.Equal(0L, result.FreedBytes, "nothing to free");
        Assert.Equal(0, result.FailedItems, "and nothing failed");
    }

    private static void WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, new byte[bytes]);
    }
}
