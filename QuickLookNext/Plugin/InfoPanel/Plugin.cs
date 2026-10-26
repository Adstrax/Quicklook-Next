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
using System.Windows;

namespace QuickLook.Plugin.InfoPanel;

public sealed class Plugin : IViewer
{
    private InfoPanel _ip;

    public int Priority => int.MinValue;

    public void Init()
    {
    }

    public bool CanHandle(string sample)
    {
        return true;
    }

    public void Prepare(string path, ContextObject context)
    {
        // v6.3.4: match application previews so switching between a folder and an executable
        // does not resize or restyle the card. The panel owns the complete acrylic client area.
        context.PreferredSize = new Size { Width = 460, Height = 216 };

        context.Title = string.Empty;
        context.TitlebarOverlap = true;
        context.TitlebarBlurVisibility = false;
        context.TitlebarColourVisibility = false;
        context.CanResize = false;
        context.FullWindowDragging = true;
    }

    public void View(string path, ContextObject context)
    {
        _ip = new InfoPanel();
        context.ViewerContent = _ip;

        _ip.DisplayInfo(path);
        context.IsBusy = false;
    }

    public void Cleanup()
    {
        if (_ip == null)
            return;

        _ip.Stop = true;
        _ip = null;
    }
}
