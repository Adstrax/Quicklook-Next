// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.
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
using System.IO;
using System.Linq;
using System.Windows;

namespace QuickLook.Plugin.PEViewer;

public sealed class Plugin : IViewer
{
    private static readonly string[] _extensions =
    [
        ".exe", ".sys", ".scr", ".ocx", ".cpl",  ".bpl",
        ".dll", ".winmd", ".ax", ".drv", ".vxd",
        ".mui", ".mun",
        ".tlb",
        ".efi", ".mz",
    ];

    private PEInfoPanel _ip;
    private string _path;

    public int Priority => 0;

    public void Init()
    {
    }

    public bool CanHandle(string path)
    {
        return !Directory.Exists(path) &&
            _extensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    public void Prepare(string path, ContextObject context)
    {
        // v6.3.4: the extra height gives the icon card and the three metadata rows the same
        // breathing room as the application-preview concept, without making the window taller
        // than it needs to be.
        context.PreferredSize = new Size { Width = 460, Height = 216 };
        context.Title = string.Empty;
        // v5.6.15: the panel owns the whole client area - see the folder card (QuickLookNext/Plugin/InfoPanel).
        // A reserved caption strip made the window a caption height taller than the content, and because the
        // top bar is hidden in normal use that strip showed up as extra blank space above the content.
        context.TitlebarOverlap = true;
        context.TitlebarBlurVisibility = false;
        context.TitlebarColourVisibility = false;
        context.FullWindowDragging = true;
    }

    public void View(string path, ContextObject context)
    {
        _path = path;
        _ip = new PEInfoPanel();

        _ip.DisplayInfo(_path);
        _ip.Tag = context;

        context.ViewerContent = _ip;
        context.IsBusy = false;
    }

    public void Cleanup()
    {
        GC.SuppressFinalize(this);

        _ip = null;
    }
}
