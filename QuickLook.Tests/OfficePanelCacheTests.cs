// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Plugin.OfficeViewer;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLook.Tests;

/// <summary>
/// v6.1.2: Word and PowerPoint pages are expensive to parse. Revisiting an
/// unchanged document must reuse the result, a changed file must be read again,
/// and two simultaneous requests must not parse it twice.
/// </summary>
internal class OfficePanelCacheTests : SettingsFixture
{
    public override void Setup()
    {
        base.Setup();
        OfficePanelCache.ResetForTests();
    }

    public override void Teardown()
    {
        OfficePanelCache.ResetForTests();
        base.Teardown();
    }

    public void AnUnchangedDocumentIsParsedOnlyOnce()
    {
        var path = Path.Combine(Root, "paper.docx");
        File.WriteAllBytes(path, [1, 2, 3]);
        var calls = 0;

        OfficePanelContent Build(string folder)
        {
            calls++;
            File.WriteAllText(Path.Combine(folder, "img.png"), "picture");
            return Content(folder);
        }

        var first = OfficePanelCache.Get(path, Build);
        var second = OfficePanelCache.Get(path, Build);

        try
        {
            Assert.Equal(1, calls, "the second preview comes from the cache");
            Assert.True(first.ImageFolder != second.ImageFolder,
                "each preview owns a separate image folder");
            Assert.True(File.Exists(Path.Combine(second.ImageFolder, "img.png")),
                "the cached pictures are cloned for the new preview");
            Assert.Equal("page", second.Html, "the parsed page is reused");
            Assert.Equal(1, second.Navigation.Count, "the outline is reused");
        }
        finally
        {
            OfficeImageStore.Cleanup(first.ImageFolder);
            OfficeImageStore.Cleanup(second.ImageFolder);
        }
    }

    public void LowMemoryModeDoesNotKeepTheParsedPage()
    {
        var path = Path.Combine(Root, "paper.docx");
        File.WriteAllBytes(path, [1, 2, 3]);
        var calls = 0;

        OfficePanelContent Build(string folder)
        {
            calls++;
            return Content(folder);
        }

        OfficePanelCache.RetainAcrossPreviews = false;

        var first = OfficePanelCache.Get(path, Build);
        var second = OfficePanelCache.Get(path, Build);

        try
        {
            Assert.Equal(2, calls, "each preview parses the document again");
            Assert.True(first.ImageFolder != second.ImageFolder,
                "and each preview keeps its own folder");
        }
        finally
        {
            OfficeImageStore.Cleanup(first.ImageFolder);
            OfficeImageStore.Cleanup(second.ImageFolder);
            OfficePanelCache.RetainAcrossPreviews = true;
        }
    }

    public void AChangedFileIsParsedAgain()
    {
        var path = Path.Combine(Root, "deck.pptx");
        File.WriteAllBytes(path, [1]);
        var calls = 0;

        OfficePanelContent Build(string folder)
        {
            calls++;
            return Content(folder);
        }

        var first = OfficePanelCache.Get(path, Build);
        File.WriteAllBytes(path, [1, 2]);
        var second = OfficePanelCache.Get(path, Build);

        try
        {
            Assert.Equal(2, calls, "the new file version bypasses the old entry");
            Assert.True(first.ImageFolder != second.ImageFolder,
                "the two versions never share a folder");
        }
        finally
        {
            OfficeImageStore.Cleanup(first.ImageFolder);
            OfficeImageStore.Cleanup(second.ImageFolder);
        }
    }

    public void ClosingAPanelDoesNotDamageTheCache()
    {
        var path = Path.Combine(Root, "kept.docx");
        File.WriteAllBytes(path, [1]);
        var calls = 0;

        OfficePanelContent Build(string folder)
        {
            calls++;
            File.WriteAllText(Path.Combine(folder, "img.png"), "picture");
            return Content(folder);
        }

        var first = OfficePanelCache.Get(path, Build);
        OfficeImageStore.Cleanup(first.ImageFolder);

        var second = OfficePanelCache.Get(path, Build);
        try
        {
            Assert.Equal(1, calls, "the cache keeps its own copy of the page");
            Assert.True(File.Exists(Path.Combine(second.ImageFolder, "img.png")),
                "closing the first panel did not remove the cached pictures");
        }
        finally
        {
            OfficeImageStore.Cleanup(second.ImageFolder);
        }
    }

    public void ClonedPicturesHaveIndependentFolderOwnership()
    {
        var source = OfficeImageStore.CreateFolder();
        var destination = OfficeImageStore.CreateFolder();
        var picture = Path.Combine(source, "img.png");
        File.WriteAllText(picture, "picture");

        try
        {
            Assert.True(OfficeImageStore.TryCloneFiles(source, destination),
                "the picture set can be cloned");

            OfficeImageStore.Cleanup(source);

            var clone = Path.Combine(destination, "img.png");
            Assert.True(File.Exists(clone),
                "deleting one owner's folder leaves the cloned picture usable");
            Assert.Equal("picture", File.ReadAllText(clone),
                "the cloned picture keeps its content");
        }
        finally
        {
            OfficeImageStore.Cleanup(source);
            OfficeImageStore.Cleanup(destination);
        }
    }

    public void APageWithoutPicturesStillGetsItsOwnFolder()
    {
        var path = Path.Combine(Root, "plain.docx");
        File.WriteAllBytes(path, [1]);

        var first = OfficePanelCache.Get(path, Content);
        var second = OfficePanelCache.Get(path, Content);

        try
        {
            Assert.True(first.ImageFolder != second.ImageFolder,
                "even an empty page folder must not be shared by two panels");
        }
        finally
        {
            OfficeImageStore.Cleanup(first.ImageFolder);
            OfficeImageStore.Cleanup(second.ImageFolder);
        }
    }

    public void SimultaneousRequestsShareOneParse()
    {
        var path = Path.Combine(Root, "shared.docx");
        File.WriteAllBytes(path, [1, 2, 3]);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;

        OfficePanelContent Build(string folder)
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)), "the test releases the parser");
            File.WriteAllText(Path.Combine(folder, "slide.png"), "picture");
            return Content(folder);
        }

        var firstTask = Task.Run(() => OfficePanelCache.Get(path, Build));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)), "the first parse has started");

        var secondTask = Task.Run(() => OfficePanelCache.Get(path, Build));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (OfficePanelCache.WaitingCountForTests == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        Assert.True(OfficePanelCache.WaitingCountForTests > 0,
            "the second request is waiting on the first parse");
        release.Set();
        var first = firstTask.GetAwaiter().GetResult();
        var second = secondTask.GetAwaiter().GetResult();

        try
        {
            Assert.Equal(1, calls, "the second request joins the first parse");
            Assert.True(first.ImageFolder != second.ImageFolder,
                "joining a parse still gives each panel its own folder");
        }
        finally
        {
            OfficeImageStore.Cleanup(first.ImageFolder);
            OfficeImageStore.Cleanup(second.ImageFolder);
        }
    }

    private static OfficePanelContent Content(string folder) => new()
    {
        Html = "page",
        Navigation = [new OfficeNavEntry("Chapter 1", "chapter-1")],
        ImageFolder = folder,
    };
}
