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
using System.IO;

namespace QuickLook.Tests;

internal class OpenWithHelperTests
{
    public void PathsWithSpacesStayOneArgument()
    {
        var path = @"C:\Users\Tester\My Documents\Quarterly report 2026.pdf";

        var start = OpenWithHelper.CreateStartInfo(path);

        Assert.Equal("rundll32.exe", start.FileName, "the shell association dialog is used");
        Assert.Equal(2, start.ArgumentList.Count, "no argument is split by the path spaces");
        Assert.Equal("shell32.dll,OpenAs_RunDLL", start.ArgumentList[0], "the dialog command");
        Assert.Equal(path, start.ArgumentList[1], "the path reaches the dialog unchanged");
        Assert.Equal(string.Empty, start.WorkingDirectory, "the caller's current directory is irrelevant");
    }

    public void UnicodeAndUncPathsArePreserved()
    {
        var path = @"\\server\共享目录\年度 报告.txt";

        var start = OpenWithHelper.CreateStartInfo(path);

        Assert.Equal(path, start.ArgumentList[1], "the UNC path and its spaces are preserved");
    }

    public void RelativePathsAreMadeAbsolute()
    {
        var relative = Path.Combine("folder with spaces", "报告.txt");

        var start = OpenWithHelper.CreateStartInfo(relative);

        Assert.True(Path.IsPathFullyQualified(start.ArgumentList[1]),
            "the shell receives an absolute path even if the caller passed a relative one");
    }

    public void EmptyPathsAreRejectedBeforeStartingAProcess()
    {
        Assert.Throws<ArgumentException>(() => OpenWithHelper.CreateStartInfo(" "),
            "an empty path must not open an unrelated association dialog");
    }
}
