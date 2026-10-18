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

using System;
using System.IO;
using PdfiumViewer;

namespace QuickLook.Plugin.PDFViewer;

public class PdfDocumentWrapper : IDisposable
{
    public PdfDocumentWrapper(Stream stream, string password = null)
    {
        PdfStream = new MemoryStream((int)stream.Length);
        stream.CopyTo(PdfStream);

        PdfDocument = PdfDocument.Load(PdfStream, password);

        // v6.1.3: an unlocked document must never be handed to a later preview:
        // that preview would show protected content without asking for the
        // password again.
        IsCacheable = string.IsNullOrEmpty(password);
    }

    public PdfDocument PdfDocument { get; private set; }

    public MemoryStream PdfStream { get; private set; }

    public bool IsCacheable { get; }

    /// <summary>
    /// v6.1.3: the thumbnail strip asks for the same small pages again whenever
    /// the virtualized list recycles an item; this keeps them.
    /// </summary>
    internal PdfThumbnailCache ThumbnailCache { get; } = new();

    public void Dispose()
    {
        lock (PdfPageExtension.SyncRoot)
        {
            PdfDocument?.Dispose();
            PdfDocument = null;
            PdfStream?.Dispose();
            PdfStream = null;
        }
    }

}
