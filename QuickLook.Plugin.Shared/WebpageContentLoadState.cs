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

namespace QuickLook.Plugin.Shared;

/// <summary>
/// Tracks the one document load that may report its first rendered frame.
/// A reused panel can start several loads before an older page finishes;
/// only the newest load is allowed to complete the visual handoff.
/// </summary>
internal sealed class WebpageContentLoadState
{
    private int _version;
    private bool _ready;

    internal int Version => _version;

    internal int Begin()
    {
        _ready = false;
        return ++_version;
    }

    internal void MarkReady() => _ready = true;

    internal bool IsReady(int version) =>
        version > 0 && version == _version && _ready;

    internal void Reset()
    {
        ++_version;
        _ready = false;
    }
}
