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
using System.Windows;
using System.Windows.Media;

namespace QuickLookNext.Helpers;

/// <summary>
/// The single material for the app's menu-like surfaces - the tray menu, the plugin
/// manager and the update dialogs. The preview window is deliberately not part of this:
/// it keeps the WCA acrylic it has always used.
/// <para>
/// v5.0.2: the original recipe, WCA acrylic (ACCENT_ENABLE_ACRYLICBLURBEHIND) plus the
/// app's own double tint - an accent tint at 30% (<see cref="TintOpacity"/>) under a
/// brush at 55% dark / 72% light (see ThemePalette.Tint), about 70% opacity in dark mode.
/// </para>
/// <para>
/// 5.0.0 had switched these four surfaces to the Windows 11 host backdrop
/// (ACCENT_ENABLE_HOSTBACKDROP, accent state 5) and taken both tint layers down with it
/// (accent 12% + a ~20% brush, ~30% opacity). On screen the menu then read as a fully
/// transparent pane - the wallpaper went straight through and the surface stopped looking
/// like a material at all. 5.0.1 kept that recipe; v5.0.2 puts the acrylic and its tint
/// back.
/// </para>
/// </summary>
internal static class MenuSurface
{
    /// <summary>Tint opacity handed to the accent policy.</summary>
    private const double TintOpacity = 0.3d;

    /// <summary>
    /// Applies the menu material to <paramref name="window"/> and reports whether the
    /// accent policy call succeeded. Call from OnSourceInitialized and again from
    /// OnContentRendered, like the surfaces did before.
    /// </summary>
    internal static bool Apply(Window window, bool isDark)
    {
        var tint = isDark
            ? Color.FromRgb(0x2A, 0x24, 0x20)
            : Color.FromRgb(0xF8, 0xF6, 0xF4);

        WindowHelper.DisableDwmBlur(window); // clears a previous material, restores corners
        return WindowHelper.EnableAcrylicBlur(window, tint, isDark, TintOpacity);
    }

    /// <summary>
    /// Reported by the surfaces' DiagnoseBackdrop test hooks. The shape is kept from
    /// v5.0.0 so the smoke test still fails if a surface ends up asking for no material
    /// at all.
    /// </summary>
    internal static string Diagnose() => "material=acrylic";
}
