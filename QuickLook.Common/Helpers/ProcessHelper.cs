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

using QuickLook.Common.NativeMethods;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace QuickLook.Common.Helpers;

public static class ProcessHelper
{
    private const int ErrorInsufficientBuffer = 0x7A;
    private static readonly object GcLock = new();
    private static readonly object LogLock = new();
    private static long _lastAggressiveGcTicks;

    public static void PerformAggressiveGC()
    {
        // Throttle: rapid preview open/close (spacebar toggling) would
        // otherwise schedule a full blocking GC for every close. Run at most
        // one full collection per 30 seconds.
        lock (GcLock)
        {
            var now = Environment.TickCount64;
            if (now - _lastAggressiveGcTicks < TimeSpan.FromSeconds(30).TotalMilliseconds)
                return;
            _lastAggressiveGcTicks = now;
        }

        // delay some time to make sure that all windows are closed
        Task.Delay(2000).ContinueWith(t => GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false));
    }

    public static bool IsRunningAsUWP()
    {
        if (Environment.OSVersion.Version < new Version(6, 2)) // Windows 8
            return false;

        try
        {
            uint len = 0;
            var r = Kernel32.GetCurrentPackageFullName(ref len, null);

            return r == ErrorInsufficientBuffer;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static bool IsOnWindows10S()
    {
        const uint PRODUCT_CLOUD = 0x000000B2; // Windows 10 S
        const uint PRODUCT_CLOUDN = 0x000000B3; // Windows 10 S N

        Kernel32.GetProductInfo(Environment.OSVersion.Version.Major,
            Environment.OSVersion.Version.Minor, 0, 0, out var osType);

        return osType == PRODUCT_CLOUD || osType == PRODUCT_CLOUDN;
    }

    public static bool IsShuttingDown()
    {
        var isShuttingDownProperty =
            typeof(Application).GetProperty("IsShuttingDown", BindingFlags.NonPublic | BindingFlags.Static);
        if (isShuttingDownProperty == null)
            throw new Exception("Unable to detect Application.IsShuttingDown.");
        return (bool)isShuttingDownProperty.GetValue(Application.Current);
    }

    public static void WriteLog(string msg)
    {
        Debug.WriteLine(msg);

        var logFilePath = LogPath;

        lock (LogLock)
        {
            try
            {
                RotateLogIfTooLarge(logFilePath);
            }
            catch
            {
                // Logging must never fail the caller - if the rotation cannot happen (the file is
                // held by another process), keep appending to the existing file.
            }

            using var writer = new StreamWriter(new FileStream(logFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite));
            writer.BaseStream.Seek(0, SeekOrigin.End);

            writer.WriteLine($"========{DateTime.Now}========");
            writer.WriteLine(msg);
            writer.WriteLine();
        }
    }

    /// <summary>The diagnostic log, and the one rotated copy kept next to it.</summary>
    public const string LogFileName = "QuickLookNext.Exception.log";

    public const string PreviousLogFileName = LogFileName + ".1";

    public static string LogPath => Path.Combine(SettingHelper.DataRoot, LogFileName);

    public static string PreviousLogPath => Path.Combine(SettingHelper.DataRoot, PreviousLogFileName);

    /// <summary>
    /// v5.1.0: the log was append-only and nothing ever trimmed it - a run of unplayable videos
    /// (each one writes a stack trace) could grow it without bound, and the "data &amp; cache" panel
    /// could only show the size. Past this size the file is moved aside once, so what the log holds
    /// is always the current session plus the one before it.
    /// </summary>
    private const long MaxLogBytes = 1024 * 1024;

    private static void RotateLogIfTooLarge(string logFilePath)
    {
        var info = new FileInfo(logFilePath);
        if (!info.Exists || info.Length < MaxLogBytes)
            return;

        // The older copy is only kept for the session that just ended.
        File.Move(logFilePath, PreviousLogPath, overwrite: true);
    }
}
