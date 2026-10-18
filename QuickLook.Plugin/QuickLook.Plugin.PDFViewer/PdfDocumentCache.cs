// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Diagnostics;
using System.IO;

namespace QuickLook.Plugin.PDFViewer;

/// <summary>
/// v6.1.3: lets a reopened PDF skip the file copy and Pdfium parse that dominated
/// its first frame on large documents.
/// <para>
/// The cache keeps one document, only for an unencrypted file inside the size
/// budget, and only while the file keeps the same length and timestamp. A
/// password-protected document is never kept: the next preview must ask for the
/// password again instead of silently reusing an unlocked document.
/// </para>
/// </summary>
internal static class PdfDocumentCache
{
    private const long MaxReusableFileBytes = 32L * 1024 * 1024;

    private static readonly SingleDocumentCache<PdfDocumentWrapper> Documents = new();

    internal static bool TryTake(string path, out PdfDocumentWrapper document)
    {
        document = null;

        if (!TryNormalize(path, out var fullPath) ||
            !TryCapture(fullPath, out var length, out var ticks))
        {
            Documents.Clear();
            return false;
        }

        return Documents.TryTake(fullPath, length, ticks, out document);
    }

    internal static void GiveBack(string path, PdfDocumentWrapper document)
    {
        if (document is null)
            return;

        if (!document.IsCacheable ||
            !TryNormalize(path, out var fullPath) ||
            !TryCapture(fullPath, out var length, out var ticks) ||
            length > MaxReusableFileBytes)
        {
            document.Dispose();
            return;
        }

        Documents.Store(fullPath, length, ticks, document);
    }

    internal static void Clear() => Documents.Clear();

    internal static bool IsEmptyForTests => Documents.IsEmpty;

    private static bool TryNormalize(string path, out string fullPath)
    {
        fullPath = null;
        if (string.IsNullOrEmpty(path))
            return false;

        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            return false;
        }
    }

    private static bool TryCapture(string path, out long length, out long ticks)
    {
        length = 0;
        ticks = 0;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;

            length = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            return false;
        }
    }
}
