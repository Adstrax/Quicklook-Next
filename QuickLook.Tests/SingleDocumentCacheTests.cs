// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Plugin.PDFViewer;
using System;

namespace QuickLook.Tests;

/// <summary>
/// v6.1.3: one parsed PDF is kept between previews. The document has a single
/// owner at a time - handing it out twice would let one preview dispose the
/// document the next one is still showing - and a changed file must never be
/// served from the old parse.
/// </summary>
internal class SingleDocumentCacheTests
{
    private const string FilePath = @"C:\Documents\manual.pdf";

    public void ADocumentIsHandedOverExactlyOnce()
    {
        var cache = new SingleDocumentCache<FakeDocument>();
        var document = new FakeDocument();
        cache.Store(FilePath, 10, 100, document);

        Assert.True(cache.TryTake(FilePath, 10, 100, out var taken), "the matching document is handed out");
        Assert.True(ReferenceEquals(document, taken), "and it is the stored instance");
        Assert.False(document.Disposed, "the taker now owns the document");
        Assert.False(cache.TryTake(FilePath, 10, 100, out _), "a second preview cannot share it");
    }

    public void AChangedFileIsDroppedInsteadOfHandedOut()
    {
        var cache = new SingleDocumentCache<FakeDocument>();
        var document = new FakeDocument();
        cache.Store(FilePath, 10, 100, document);

        Assert.False(cache.TryTake(FilePath, 10, 101, out _), "a newer file does not get the old parse");
        Assert.True(document.Disposed, "the stale document is released immediately");
        Assert.True(cache.IsEmpty, "and it is not left behind");
    }

    public void AnotherPathIsDroppedInsteadOfHandedOut()
    {
        var cache = new SingleDocumentCache<FakeDocument>();
        var document = new FakeDocument();
        cache.Store(FilePath, 10, 100, document);

        Assert.False(cache.TryTake(@"C:\Documents\other.pdf", 10, 100, out _),
            "a document belongs to the exact file it was parsed from");
        Assert.True(document.Disposed, "the other file's document is released");
    }

    public void StoringAgainReleasesThePreviousDocument()
    {
        var cache = new SingleDocumentCache<FakeDocument>();
        var first = new FakeDocument();
        var second = new FakeDocument();
        cache.Store(FilePath, 10, 100, first);
        cache.Store(@"C:\Documents\other.pdf", 20, 200, second);

        Assert.True(first.Disposed, "the replaced document is released");
        Assert.False(second.Disposed, "the newest document is kept");
        Assert.True(cache.TryTake(@"C:\Documents\other.pdf", 20, 200, out var taken), "the newest document is served");
        Assert.True(ReferenceEquals(second, taken), "and it is the stored instance");
    }

    public void ClearingReleasesTheDocument()
    {
        var cache = new SingleDocumentCache<FakeDocument>();
        var document = new FakeDocument();
        cache.Store(FilePath, 10, 100, document);

        cache.Clear();

        Assert.True(document.Disposed, "clearing releases the memory");
        Assert.True(cache.IsEmpty, "the cache is empty afterwards");
    }

    public void TakingFromAnEmptyCacheIsSafe()
    {
        var cache = new SingleDocumentCache<FakeDocument>();

        Assert.False(cache.TryTake(FilePath, 10, 100, out var taken), "nothing is served from an empty cache");
        Assert.Null(taken, "and no document is produced");
    }

    private sealed class FakeDocument : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
