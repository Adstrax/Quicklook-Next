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
using System.Threading;

namespace QuickLookNext;

/// <summary>
/// Fast-path used by <see cref="Program.Main"/>: when another instance is
/// already running, forwarding the requested path through the named pipe is
/// all the second process needs to do. This class deliberately avoids any
/// reference to <see cref="App"/> or WPF types so the type initializer of
/// <see cref="App"/> (and the PresentationFramework assemblies behind it)
/// never runs on this path.
/// </summary>
internal static class StartupForwarder
{
    internal static string MutexName => InstanceIdentity.MutexName;

    internal static bool TryForwardToRunningInstance()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length <= 1)
            return false;

        var path = args[1];

        // Same checks as App.EnsureFirstInstance: only forward a real path
        // (switches like /autorun belong to the first-instance startup).
        if (!Directory.Exists(path) && !File.Exists(path))
            return false;

        // A named mutex held by the first instance is the cheapest reliable
        // "is it running" check that does not load WPF.
        if (!Mutex.TryOpenExisting(MutexName, out var mutex))
            return false;

        using (mutex)
        {
            var options = args.Length > 2 ? args[2..] : null;

            // Short timeout: if the first instance is still starting up (pipe
            // server not listening yet) or crashed, fall through to the normal
            // startup instead of blocking the shell for 2 seconds.
            return PipeServerManager.PostMessage(
                PipeMessages.Toggle, path, options, timeoutMs: 500);
        }
    }
}
