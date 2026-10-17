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
using System.Threading;
using System.Windows;

namespace QuickLook.Plugin.ImageViewer.Helpers;

/// <summary>
/// v6.1.0: one large decode at a time. Arrow-key browsing pays a decode per file, and without
/// this several 20–60 MP decodes could run at once when the user moves faster than the decoder,
/// multiplying the peak memory - the same peak the on-demand decode is meant to keep down.
/// Small thumbnails are not gated, so ordinary previews and folder browsing keep their speed.
/// </summary>
internal static class LargeImageDecodeGate
{
    internal const long ThresholdPixels = 16_000_000L;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static bool IsLarge(Size size)
        => !size.IsEmpty && size.Width * size.Height > ThresholdPixels;

    internal static bool TryEnter(Size fullSize, CancellationToken cancellationToken, out IDisposable lease)
    {
        lease = null;
        if (cancellationToken.IsCancellationRequested)
            return false;

        if (!IsLarge(fullSize))
            return true;

        try
        {
            Gate.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Gate.Release();
            return false;
        }

        lease = new Lease();
        return true;
    }

    private sealed class Lease : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Gate.Release();
        }
    }
}
