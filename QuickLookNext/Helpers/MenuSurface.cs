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

using QuickLook.Common.Helpers;
using System;
using System.Windows;
using System.Windows.Media;

namespace QuickLookNext.Helpers;

/// <summary>
/// v5.0.0: the single material for the app's menu-like surfaces - the tray menu,
/// the plugin manager and the update dialogs. The preview window is deliberately not
/// part of this: it keeps the WCA acrylic it has always used.
/// <para>
/// The surface is the same recipe the tray menu always used (borderless window, WCA
/// accent policy, our own tint brush on top), except that it now asks for
/// <c>ACCENT_ENABLE_HOSTBACKDROP</c> where the OS supports it. That is the state
/// TranslucentTB documents as "allows desktop apps to use
/// Compositor.CreateHostBackdropBrush" - the backdrop Windows 11 menus are made of -
/// and, unlike the DWM system backdrops, it does render on a window that never takes
/// focus. Measured side by side against the real Windows 11 context menu, the host
/// backdrop is noticeably softer/blurrier than the old acrylic state, which is the
/// difference that was visible in the tray menu.
/// </para>
/// <para>
/// The tint layers had to come down with it: the menu used to be tinted twice (accent
/// ~30% plus a ~55% brush), which is why the wallpaper barely showed through. The
/// brush now sits at ~20% (see ThemePalette.Tint), close to the system menu.
/// </para>
/// </summary>
internal static class MenuSurface
{
    /// <summary>Tint opacity handed to the accent policy.</summary>
    private const double TintOpacity = 0.12d;

    /// <summary>Whether the last <see cref="Apply"/> got the host backdrop.</summary>
    internal static bool IsHostBackdrop { get; private set; }

    /// <summary>
    /// Applies the menu material to <paramref name="window"/>. Call from
    /// OnSourceInitialized and again from OnContentRendered, like the surfaces did
    /// before.
    /// </summary>
    internal static void Apply(Window window, bool isDark)
    {
        var tint = isDark
            ? Color.FromRgb(0x2A, 0x24, 0x20)
            : Color.FromRgb(0xF8, 0xF6, 0xF4);

        WindowHelper.DisableDwmBlur(window); // clears a previous material, restores corners

        IsHostBackdrop = App.IsWin11
                         && Environment.OSVersion.Version >= new Version(10, 0, 22621)
                         && WindowHelper.EnableHostBackdropBlur(window, tint, isDark, TintOpacity);

        if (!IsHostBackdrop)
            WindowHelper.EnableAcrylicBlur(window, tint, isDark, TintOpacity);
    }

    /// <summary>Reported by the surfaces' DiagnoseBackdrop test hooks.</summary>
    internal static string Diagnose()
        => IsHostBackdrop ? "material=host-backdrop" : "material=acrylic";
}
