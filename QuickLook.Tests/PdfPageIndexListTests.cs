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
using System.Collections;
using System.Collections.Generic;
using System.Windows.Controls;

namespace QuickLook.Tests;

/// <summary>
/// The thumbnail strip needs an indexed page sequence that WPF can virtualize,
/// not one collection-change notification for every page in a large PDF.
/// </summary>
internal class PdfPageIndexListTests
{
    public void TheCountIsTheDocumentPageCount()
    {
        var pages = new PdfPageIndexList(1234);

        Assert.Equal(1234, pages.Count, "the page count is exposed without materializing entries");
    }

    public void EveryIndexIsItsOwnPageNumber()
    {
        var pages = new PdfPageIndexList(10);

        Assert.Equal(0, pages[0], "the first page is zero");
        Assert.Equal(5, pages[5], "a middle page maps to its index");
        Assert.Equal(9, pages[9], "the last page maps to its index");
    }

    public void ItIsAReadOnlyIndexedListForWpf()
    {
        IList pages = new PdfPageIndexList(4);

        Assert.True(pages.IsReadOnly, "WPF must treat it as a read-only source");
        Assert.True(pages.IsFixedSize, "entries cannot be added or removed");
        Assert.Equal(2, (int)pages[2], "the non-generic indexer works too");
    }

    public void CopyingProducesThePageSequence()
    {
        var pages = new PdfPageIndexList(5);
        var destination = new int[7];

        pages.CopyTo(destination, 1);

        Assert.Equal(0, destination[0], "the prefix is untouched");
        Assert.Equal(0, destination[1], "the first page starts at the requested offset");
        Assert.Equal(4, destination[5], "the last page is copied");
        Assert.Equal(0, destination[6], "the suffix is untouched");
    }

    public void MutationIsRejected()
    {
        var pages = new PdfPageIndexList(3);
        var mutable = (IList<int>)pages;

        Assert.Throws<NotSupportedException>(() => pages[0] = 7, "the sequence is immutable");
        Assert.Throws<NotSupportedException>(() => mutable.Add(3), "pages cannot be appended");
        Assert.Throws<NotSupportedException>(() => mutable.Remove(1), "pages cannot be removed");
        Assert.Throws<NotSupportedException>(() => mutable.Clear(), "the sequence cannot be cleared");
    }

    public void EnumerationMatchesRandomAccess()
    {
        var pages = new PdfPageIndexList(4);
        var seen = new List<int>();

        foreach (var page in pages)
            seen.Add(page);

        Assert.Equal("0,1,2,3", string.Join(",", seen), "enumeration keeps page order");
    }

    public void AWpfListBoxCanSelectTheFirstPage()
    {
        var list = new ListBox
        {
            ItemsSource = new PdfPageIndexList(1000),
            SelectedIndex = 0,
        };

        Assert.Equal(1000, list.Items.Count, "the list box sees every page");
        Assert.Equal(0, list.SelectedIndex, "the first page remains selected");
    }
}
