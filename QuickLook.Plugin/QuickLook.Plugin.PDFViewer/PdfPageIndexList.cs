// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections;
using System.Collections.Generic;

namespace QuickLook.Plugin.PDFViewer;

/// <summary>
/// A read-only list whose value at every index is the index itself.
/// <para>
/// The thumbnail strip only needs the numbers 0..pageCount-1. Adding those
/// numbers one at a time to an ObservableCollection produced one UI notification
/// per page before the first page could be shown. This view exposes the same
/// indexed sequence to WPF without allocating or notifying once per page.
/// </para>
/// </summary>
internal sealed class PdfPageIndexList : IList<int>, IReadOnlyList<int>, IList
{
    internal static readonly PdfPageIndexList Empty = new(0);

    internal PdfPageIndexList(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        Count = count;
    }

    public int Count { get; }

    public bool IsReadOnly => true;

    public int this[int index]
    {
        get
        {
            CheckIndex(index);
            return index;
        }
        set => throw new NotSupportedException();
    }

    object IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    bool IList.IsFixedSize => true;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    public int IndexOf(int item) => Contains(item) ? item : -1;

    public bool Contains(int item) => item >= 0 && item < Count;

    public void CopyTo(int[] array, int arrayIndex)
    {
        if (array is null)
            throw new ArgumentNullException(nameof(array));
        if (arrayIndex < 0 || arrayIndex > array.Length)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        if (array.Length - arrayIndex < Count)
            throw new ArgumentException("The destination array is too small.", nameof(array));

        for (var i = 0; i < Count; i++)
            array[arrayIndex + i] = i;
    }

    public IEnumerator<int> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return i;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    int IList.IndexOf(object value) => value is int item ? IndexOf(item) : -1;

    bool IList.Contains(object value) => value is int item && Contains(item);

    void ICollection.CopyTo(Array array, int index)
    {
        if (array is null)
            throw new ArgumentNullException(nameof(array));
        if (array.Rank != 1 || array.GetLowerBound(0) != 0)
            throw new ArgumentException("A one-dimensional zero-based array is required.", nameof(array));
        if (index < 0 || index > array.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (array.Length - index < Count)
            throw new ArgumentException("The destination array is too small.", nameof(array));

        for (var i = 0; i < Count; i++)
            array.SetValue(i, index + i);
    }

    int IList.Add(object value) => throw new NotSupportedException();

    void ICollection<int>.Add(int item) => throw new NotSupportedException();

    void IList.Insert(int index, object value) => throw new NotSupportedException();

    void IList<int>.Insert(int index, int item) => throw new NotSupportedException();

    void IList.Remove(object value) => throw new NotSupportedException();

    bool ICollection<int>.Remove(int item) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void IList<int>.RemoveAt(int index) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void ICollection<int>.Clear() => throw new NotSupportedException();

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }
}
