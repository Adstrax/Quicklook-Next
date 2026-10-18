// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;

namespace QuickLook.Plugin.PDFViewer;

/// <summary>
/// v6.1.3: keeps at most one parsed document alive between previews.
/// <para>
/// A parsed document is not shareable: the control that shows it owns it and
/// disposes it when the preview closes. This cache therefore hands the document
/// over instead of lending it - <see cref="TryTake"/> removes it, so only one
/// owner can ever hold it - and disposes anything whose file version no longer
/// matches. Keeping exactly one entry means a user flipping between two PDFs
/// alternates between a hit and a parse rather than pinning every file they
/// ever looked at.
/// </para>
/// </summary>
internal sealed class SingleDocumentCache<T> where T : class, IDisposable
{
    private readonly object _sync = new();
    private T _document;
    private string _path;
    private long _length;
    private long _ticks;

    internal bool IsEmpty
    {
        get
        {
            lock (_sync)
                return _document is null;
        }
    }

    /// <summary>
    /// Takes the document when it belongs to exactly this file version. Any
    /// other entry is disposed: it can never be used again, and holding it would
    /// keep a replaced file's memory alive.
    /// </summary>
    internal bool TryTake(string path, long length, long ticks, out T document)
    {
        document = null;

        lock (_sync)
        {
            if (_document is null)
                return false;

            if (!Matches(path, length, ticks))
            {
                DisposeLocked();
                return false;
            }

            document = _document;
            _document = null;
            _path = null;
            _length = 0;
            _ticks = 0;
            return true;
        }
    }

    /// <summary>Stores a document, disposing whichever one was kept before.</summary>
    internal void Store(string path, long length, long ticks, T document)
    {
        if (document is null)
            return;

        lock (_sync)
        {
            DisposeLocked();
            _document = document;
            _path = path;
            _length = length;
            _ticks = ticks;
        }
    }

    internal void Clear()
    {
        lock (_sync)
            DisposeLocked();
    }

    private bool Matches(string path, long length, long ticks)
        => string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) &&
           _length == length &&
           _ticks == ticks;

    private void DisposeLocked()
    {
        var document = _document;
        _document = null;
        _path = null;
        _length = 0;
        _ticks = 0;
        document?.Dispose();
    }
}
