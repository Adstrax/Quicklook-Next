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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.

using System.Threading;

namespace QuickLookNext.Helpers;

/// <summary>
/// v6.0.0: identifies the preview that is allowed to start rendering.
/// <para>
/// <see cref="ViewerWindow.BeginShow"/> posts the plugin's View call so the first frame is not
/// blocked by plugin setup. Several quick switches can therefore queue several View calls before
/// the first one runs. Without a generation check, the newest plugin field would be used to load
/// an older file, mixing a video host with an image path (or the other way around).
/// </para>
/// </summary>
internal sealed class PreviewShowGate
{
    private long _version;

    /// <summary>Starts a new show and returns the generation its posted callback must carry.</summary>
    internal long Begin() => Interlocked.Increment(ref _version);

    /// <summary>Invalidates every posted callback, including the current one.</summary>
    internal void Invalidate() => Interlocked.Increment(ref _version);

    /// <summary>Whether <paramref name="version"/> is still the latest show generation.</summary>
    internal bool IsCurrent(long version) =>
        version > 0 && version == Volatile.Read(ref _version);
}
