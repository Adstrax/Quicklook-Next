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
using System.Diagnostics;
using System.IO;

namespace QuickLookNext.Helpers;

internal static class OpenWithHelper
{
    internal static void Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            Process.Start(CreateStartInfo(path));
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"Open With failed for \"{path}\": {e}");
        }
    }

    /// <summary>
    /// Builds the shell invocation as separate arguments, so spaces, non-ASCII names
    /// and UNC paths never pass through command-line string quoting. The caller's
    /// working directory is deliberately left alone: an UNC parent or an offline
    /// network location must not prevent the dialog from opening.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path is required.", nameof(path));

        var start = new ProcessStartInfo("rundll32.exe")
        {
            UseShellExecute = false,
        };
        start.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
        start.ArgumentList.Add(Path.GetFullPath(path));
        return start;
    }
}
