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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuickLookNext.Helpers;

/// <summary>
/// v5.0.7: how much disk the app's data and its rebuildable caches take, and the safe way to
/// clear the caches - upstream issue
/// <see href="https://github.com/QL-Win/QuickLook/issues/1933">#1933</see> asked for exactly
/// this ("display cache data size and clearing cache").
/// <para>
/// Only whitelisted cache locations are ever deleted: the shader / GPU / HTTP caches inside the
/// WebView2 profile and the leftovers of a cancelled update in %TEMP%. Sign-in data (cookies,
/// local storage), settings, the plugin-usage statistics and the diagnostic log are never
/// touched - they are what the dialog reports as "data".
/// </para>
/// </summary>
internal static class AppDataUsage
{
    /// <summary>Cache folders inside a WebView2 profile (relative to its EBWebView folder).</summary>
    private static readonly string[] WebViewCacheFolders =
    [
        "GrShaderCache",
        "ShaderCache",
        "GPUPersistentCache",
        "BrowserMetrics",
        "component_crx_cache",
        "extensions_crx_cache",
        Path.Combine("Default", "Cache"),
        Path.Combine("Default", "Code Cache"),
        Path.Combine("Default", "GPUCache"),
        Path.Combine("Default", "DawnGraphiteCache"),
        Path.Combine("Default", "DawnWebGPUCache"),
    ];

    /// <summary>Result of a cleanup: how much was freed, and how many items were in use.</summary>
    internal readonly record struct ClearResult(long FreedBytes, int FailedItems)
    {
        internal bool Complete => FailedItems == 0;
    }

    /// <summary>Bytes that can be deleted and rebuilt on demand.</summary>
    internal static long CacheBytes(string dataPath = null, string tempPath = null)
        => CachePaths(dataPath, tempPath).Where(Directory.Exists).Sum(MeasurePath);

    /// <summary>
    /// Bytes that are kept: settings, plugin configurations, usage statistics, the diagnostic log
    /// and the parts of the WebView2 profile that are not caches (cookies, local storage, ...).
    /// </summary>
    internal static long DataBytes(string dataPath = null)
    {
        dataPath ??= SettingHelper.LocalDataPath;
        if (!Directory.Exists(dataPath))
            return 0;

        var cacheInsideData = CachePaths(dataPath, tempPath: null)
            .Where(path => IsUnder(dataPath, path) && Directory.Exists(path))
            .Sum(MeasurePath);

        return Math.Max(0, MeasurePath(dataPath) - cacheInsideData);
    }

    /// <summary>Everything the app occupies: the data folder plus the cache remnants in %TEMP%.</summary>
    internal static long TotalBytes(string dataPath = null, string tempPath = null)
        => CacheBytes(dataPath, tempPath) + DataBytes(dataPath);

    /// <summary>
    /// Deletes the whitelisted caches. Files that are in use (a WebView2 host still running) are
    /// reported instead of throwing - the caller shows a "try again later" hint.
    /// </summary>
    internal static ClearResult ClearCache(string dataPath = null, string tempPath = null)
    {
        long freed = 0;
        var failed = 0;

        foreach (var path in CachePaths(dataPath, tempPath))
        {
            var isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
                continue;

            var size = MeasurePath(path);

            try
            {
                if (isDirectory)
                    Directory.Delete(path, recursive: true);
                else
                    File.Delete(path);

                freed += size;
            }
            catch (Exception e)
            {
                failed++;
                ProcessHelper.WriteLog($"Cache cleanup skipped \"{path}\": {e.Message}");
            }
        }

        return new ClearResult(freed, failed);
    }

    /// <summary>
    /// v5.1.0: deletes the diagnostic log and its rotated copy. This is deliberately its own
    /// action instead of part of <see cref="ClearCache"/>: the log is data, not cache, and it is
    /// what a bug report needs - the button next to it lets someone drop it on purpose.
    /// </summary>
    internal static ClearResult ClearLogs(string dataPath = null)
    {
        dataPath ??= SettingHelper.LocalDataPath;

        long freed = 0;
        var failed = 0;

        foreach (var name in new[] { ProcessHelper.LogFileName, ProcessHelper.PreviousLogFileName })
        {
            var path = Path.Combine(dataPath, name);

            try
            {
                if (!File.Exists(path))
                    continue;

                var size = new FileInfo(path).Length;
                File.Delete(path);
                freed += size;
            }
            catch (Exception e)
            {
                // The app itself writes the log, so it can be open at this very moment.
                failed++;
                ProcessHelper.WriteLog($"Clearing the log file \"{path}\" failed: {e.Message}");
            }
        }

        return new ClearResult(freed, failed);
    }

    private static IEnumerable<string> CachePaths(string dataPath, string tempPath)
    {
        foreach (var profile in WebViewProfiles(dataPath ?? SettingHelper.LocalDataPath))
        {
            foreach (var relative in WebViewCacheFolders)
                yield return Path.Combine(profile, "EBWebView", relative);
        }

        var temp = tempPath ?? Path.GetTempPath();
        // A cancelled or failed update leaves its work folder and script behind; the updater
        // deletes them at the start of the next attempt, so they are pure cache.
        yield return Path.Combine(temp, "QuickLookNext.Update");
        yield return Path.Combine(temp, "QuickLookNext-update.cmd");
    }

    private static IEnumerable<string> WebViewProfiles(string dataPath)
    {
        if (!Directory.Exists(dataPath))
            yield break;

        // v5.0.0 profile repair may leave WebView2_Data_1, _2, ... behind; their caches are
        // just as disposable as the active profile's.
        foreach (var profile in Directory.EnumerateDirectories(dataPath, "WebView2_Data*"))
            yield return profile;
    }

    private static long MeasurePath(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;

            if (!Directory.Exists(path))
                return 0;

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // The file disappeared while walking (WebView2 rotating a cache file).
                }
            }

            return total;
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"Cache size of \"{path}\" could not be read: {e.Message}");
            return 0;
        }
    }

    /// <summary>Guards the subtraction above against a path that is not actually inside the data folder.</summary>
    private static bool IsUnder(string parent, string child)
    {
        var parentFull = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var childFull = Path.GetFullPath(child);
        return childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase);
    }
}
