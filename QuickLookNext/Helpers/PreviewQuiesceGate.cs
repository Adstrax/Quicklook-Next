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

using QuickLook.Common.Plugin;
using System;

namespace QuickLookNext.Helpers;

/// <summary>
/// v6.0.0: quiesces each preview plugin at most once. The switch request path and the later
/// UnloadPlugin path both need to ask, but a paused video or cancelled image decode must not be
/// touched twice while the old frame is still serving as the visual bridge.
/// </summary>
internal sealed class PreviewQuiesceGate
{
    private object _quiesced;

    internal bool TryQuiesce(object plugin)
    {
        if (plugin is not IPreviewQuiesce quiescable || ReferenceEquals(plugin, _quiesced))
            return false;

        // Mark first so a plugin whose implementation calls back into the host cannot be entered
        // twice. The host catches and logs a failing implementation just like Cleanup.
        _quiesced = plugin;
        quiescable.Quiesce();
        return true;
    }

    internal void Reset()
    {
        _quiesced = null;
    }
}
