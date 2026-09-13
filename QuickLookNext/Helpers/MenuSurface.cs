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
/// app's own double tint - the accent tint at 30% (<see cref="TintOpacity"/>) under a
/// panel brush (see ThemePalette.Tint). v5.0.3 settled the pair at a ~30% surface: the
/// accent tint alone, with the brush contributing no extra coverage.
/// </para>
/// <para>
/// 5.0.0 had switched these four surfaces to the Windows 11 host backdrop
/// (ACCENT_ENABLE_HOSTBACKDROP, accent state 5) and taken both tint layers down with it
/// (accent 12% + a ~20% brush, ~30% opacity). On screen the menu then read as a fully
/// transparent pane - the wallpaper went straight through and the surface stopped looking
/// like a material at all. 5.0.1 kept that recipe, v5.0.2 put the acrylic back, and
/// v5.0.3 re-tuned the opacity on top of it.
/// </para>
/// <para>
/// The alternative worth recording: DWM's own Desktop Acrylic
/// (DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW, "the effect for transient
/// windows, also known as Background Acrylic") renders as a flat fallback colour on these
/// WPF windows - measured on both the tray menu (also when forced to the foreground) and
/// the plugin manager. The real material is composited by the composition/XAML stack
/// (DesktopAcrylicController + a composition target, as PowerToys' always-active backdrop
/// does), which a plain WPF window does not have; that is why these surfaces keep using
/// WCA acrylic.
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
        // One definition for both layers: the accent tint and ThemePalette.Tint are the
        // same colour, only the alphas differ (see ThemePalette for the recipe).
        var tint = ThemePalette.TintColor(isDark);

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
