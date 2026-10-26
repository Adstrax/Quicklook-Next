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

namespace QuickLook.Tests;

/// <summary>
/// v3.40.0: the update script swaps the installed files after the app exits.
/// <para>
/// It used to back the folder up with <c>xcopy /EXCLUDE:"&lt;file&gt;"</c>, and xcopy
/// cannot read an exclusion file whose path is quoted - the backup "failed", the
/// update aborted, and the package that had just been downloaded was thrown away.
/// That went unnoticed for several releases because a failed update only looked
/// like "the version number did not change". These tests pin the script to the
/// working copy mechanism and to the cleanup order that keeps %TEMP% from filling
/// up with one package per attempt.
/// </para>
/// </summary>
internal class UpdateScriptTests
{
    private const string AppDir = @"C:\Program Files\QuickLook Next";
    private const string WorkDir = @"C:\Users\tester\AppData\Local\Temp\QuickLookNext.Update";

    private static string Script() => Updater.BuildUpdateScriptForTest(
        AppDir,
        WorkDir + @"\new",
        WorkDir,
        WorkDir + @"\update.log");

    private static string InstallerScript() => Updater.BuildInstallerUpdateScriptForTest(
        AppDir,
        WorkDir + @"\QuickLook-Next-Setup-6.5.0.exe",
        WorkDir,
        WorkDir + @"\update.log");

    public void TheInstallerReleaseAssetIsRecognized()
    {
        Assert.True(Updater.IsInstallerAssetName("QuickLook-Next-Setup-6.5.0.exe"),
            "the release's Setup.exe is an update package");
        Assert.False(Updater.IsInstallerAssetName("QuickLook-Next-6.5.0.zip"),
            "the portable archive is not the preferred installer");
        Assert.True(Updater.IsPortableAssetName("QuickLook-Next-6.5.0.zip"),
            "older releases remain a compatibility fallback");
    }

    public void TheSetupUpdateWaitsThenInstallsIntoTheCurrentDirectory()
    {
        var script = InstallerScript();

        Assert.True(script.Contains("tasklist", StringComparison.OrdinalIgnoreCase),
            "Setup must not replace files while the old process is running");
        Assert.True(script.Contains("/VERYSILENT", StringComparison.Ordinal),
            "the in-app update runs Setup without a wizard");
        Assert.True(script.Contains("/DIR=\"%QL_APP%\"", StringComparison.Ordinal),
            "Setup keeps the user's selected installation path");
        Assert.True(script.Contains("update ok", StringComparison.Ordinal),
            "a successful setup is recorded for the next-start result check");
    }

    public void TheSetupUpdateCleansUpBeforeDeletingItself()
    {
        var script = InstallerScript();
        var cleanup = script.IndexOf("rd /S /Q \"%QL_WORK%\"", StringComparison.Ordinal);
        var selfDelete = script.IndexOf("del \"%~f0\"", StringComparison.Ordinal);

        Assert.True(cleanup >= 0, "the downloaded installer is removed");
        Assert.True(selfDelete > cleanup,
            "a batch file that deletes itself stops executing, so cleanup comes first");
    }

    public void TheScriptDoesNotUseXcopyExclusions()
    {
        var script = Script();

        Assert.False(
            script.Contains("/EXCLUDE:", StringComparison.OrdinalIgnoreCase),
            "xcopy cannot read an exclusion file whose path is quoted");
        Assert.True(
            script.Contains("robocopy", StringComparison.OrdinalIgnoreCase),
            "the copy has to go through robocopy");
    }

    public void TheScriptKeepsThePortableUserDataOutOfTheSwap()
    {
        var script = Script();

        Assert.True(
            script.Contains("/XD \"%QL_APP%\\UserData\"", StringComparison.Ordinal),
            "the backup has to exclude UserData");
        Assert.True(
            script.Contains("if /I not \"%%~nxd\"==\"UserData\"", StringComparison.Ordinal),
            "the cleanup has to keep UserData");
    }

    public void TheScriptClearsOldRootFilesBeforeCopyingTheNewBuild()
    {
        var script = Script();

        var clear = script.IndexOf(
            "for %%f in (\"%QL_APP%\\*\") do del", StringComparison.Ordinal);
        var copy = script.IndexOf(
            "robocopy \"%QL_SRC%\"", StringComparison.Ordinal);

        Assert.True(clear >= 0, "old root files are removed");
        Assert.True(copy > clear,
            "the replacement copy must happen after obsolete files such as the old WinRT runtime are gone");
    }

    public void TheScriptKeepsTheInstallerUninstallerAfterAnUpdate()
    {
        var script = Script();

        var copy = script.IndexOf(
            "robocopy \"%QL_SRC%\"", StringComparison.Ordinal);
        var restoreUninstaller = script.IndexOf(
            "copy /Y \"%QL_BAK%\\unins*.*\"",
            StringComparison.Ordinal);

        Assert.True(restoreUninstaller > copy,
            "the uninstaller is cleared with the old files and must be restored after the new build is copied");
    }

    public void TheScriptWaitsForTheAppToExitBeforeReplacingFiles()
    {
        Assert.True(
            Script().Contains("tasklist", StringComparison.OrdinalIgnoreCase),
            "the script has to wait for the running app");
    }

    public void TheScriptRemovesItsWorkFolderBeforeDeletingItself()
    {
        var script = Script();

        var cleanup = script.IndexOf("rd /S /Q \"%QL_WORK%\"", StringComparison.Ordinal);
        var selfDelete = script.IndexOf("del \"%~f0\"", StringComparison.Ordinal);

        Assert.True(cleanup >= 0, "the work folder is removed");
        Assert.True(selfDelete > cleanup,
            "a batch file that deletes itself stops executing, so the cleanup has to come first");
    }

    public void TheScriptFailsTheUpdateWhenTheCopyFails()
    {
        var script = Script();

        // robocopy reports success below 8, so the script must not test errorlevel 1.
        Assert.True(script.Contains("if errorlevel 8 goto rollback", StringComparison.Ordinal),
            "a failed copy rolls back");
        Assert.False(script.Contains("if errorlevel 1 goto rollback", StringComparison.Ordinal),
            "robocopy exits with 1..7 for success");
    }
}
