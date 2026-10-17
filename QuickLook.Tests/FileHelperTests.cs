// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Common.Helpers;
using System;
using System.IO;
using System.Linq;

namespace QuickLook.Tests;

internal class FileHelperTests
{
    public void AtomicWriteCreatesMissingFolders()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "a", "b", "settings.json");
            FileHelper.WriteAllTextAtomic(path, "{}");
            Assert.Equal("{}", File.ReadAllText(path), "atomic write should create the missing folders");
        }
        finally
        {
            Cleanup(root);
        }
    }

    public void AtomicWriteReplacesTheWholeDocument()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "state.txt");
            File.WriteAllText(path, "old-content-that-is-longer");
            FileHelper.WriteAllTextAtomic(path, "new");

            Assert.Equal("new", File.ReadAllText(path), "the replacement should not retain old bytes");
        }
        finally
        {
            Cleanup(root);
        }
    }

    public void AtomicWriteLeavesNoTemporaryFiles()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "state.txt");
            FileHelper.WriteAllTextAtomic(path, "first");
            FileHelper.WriteAllTextAtomic(path, "second");

            Assert.Equal(1, Directory.GetFiles(root).Length, "only the destination should remain");
            Assert.Equal(
                "state.txt", Path.GetFileName(Directory.GetFiles(root).Single()),
                "the temporary file should be removed");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "QuickLookNext.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best effort, matching the other test fixtures.
        }
    }
}
