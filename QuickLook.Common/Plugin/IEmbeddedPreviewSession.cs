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
using System.Threading.Tasks;

namespace QuickLook.Common.Plugin;

/// <summary>
/// A host-owned preview rendered inside another panel. The session matches and
/// runs a normal viewer for an extracted file without opening another window.
/// </summary>
public interface IEmbeddedPreviewSession : IDisposable
{
    string Path { get; }

    object ViewerContent { get; }

    bool IsBusy { get; }

    string Error { get; }

    /// <summary>
    /// Raised on the UI thread whenever content, busy state or error changes.
    /// </summary>
    event EventHandler Changed;

    Task StartAsync();
}
