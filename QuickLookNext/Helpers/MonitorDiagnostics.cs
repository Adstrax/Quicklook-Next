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
using QuickLook.Common.NativeMethods;
using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Forms;

namespace QuickLookNext.Helpers;

/// <summary>
/// v5.5.0: what the preview window would do on each screen the machine currently has.
///
/// <para>
/// The mixed-DPI placement fix (upstream #827, and the "content laid out for the old scale" half of
/// #1956) cannot be exercised on a single-monitor machine, which is where it was written. This
/// report runs the *same* functions the viewer uses - <see cref="PreviewWindowSizing"/> for the
/// size and <see cref="WindowPlacement"/> for the position - once per screen, so anyone with a
/// mixed-DPI setup can see the numbers instead of taking them on trust:
/// </para>
///
/// <code>
/// QuickLook-Next.exe /test-monitor-refit     →  &lt;smokeDir&gt;\monitor-refit.txt
/// </code>
/// </summary>
internal static class MonitorDiagnostics
{
    /// <summary>The shape from the bug report: wide, and much larger than any screen.</summary>
    private static readonly Size SampleContent = new(4289, 631);

    /// <summary>A preview window bigger than a 1080p screen, in DIP.</summary>
    private static readonly Size OversizedWindow = new(2600, 1400);

    internal static string Report()
    {
        var report = new StringBuilder();
        var screens = Screen.AllScreens
            .OrderBy(s => s.Bounds.Left)
            .ThenBy(s => s.Bounds.Top)
            .ToList();

        report.AppendLine($"monitors={screens.Count}");
        report.AppendLine($"primary={Screen.PrimaryScreen?.DeviceName}");
        report.AppendLine();

        for (var i = 0; i < screens.Count; i++)
        {
            var screen = screens[i];
            var bounds = screen.Bounds;
            var work = screen.WorkingArea;

            var scale = DisplayDeviceHelper.GetScaleFactorForMonitor(MonitorOf(screen));
            var workDip = new Size(work.Width / scale.Horizontal, work.Height / scale.Vertical);

            report.AppendLine($"[{i}] {screen.DeviceName} primary={screen.Primary}");
            report.AppendLine(
                $"    boundsPx=({bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}) " +
                $"workPx=({work.Left},{work.Top},{work.Width},{work.Height}) " +
                $"scale={scale.Horizontal:0.##} workDip={workDip.Width:0.#}x{workDip.Height:0.#}");

            // The size a plugin would ask for on this screen, and what it is in pixels here.
            var fitted = PreviewWindowSizing.FitWithin(SampleContent, workDip, 0.8d);
            report.AppendLine(
                $"    image 4289x631 @0.8 -> {fitted.Width:0.#}x{fitted.Height:0.#} dip " +
                $"= {fitted.Width * scale.Horizontal:0}x{fitted.Height * scale.Vertical:0} px");

            // The clamp, and where the window then lands: the same sequence the viewer runs.
            var clamped = PreviewWindowSizing.ClampToDesktop(OversizedWindow, workDip);
            var monitorPx = new Rect(work.Left, work.Top, work.Width, work.Height);
            var oldPx = new Rect(
                work.Left + work.Width * 0.6, work.Top + work.Height * 0.5,
                work.Width * 0.25, work.Height * 0.2);
            var placed = WindowPlacement.PlaceNearExisting(oldPx, clamped, scale, monitorPx);

            var pxWidth = clamped.Width * scale.Horizontal;
            var pxHeight = clamped.Height * scale.Vertical;
            var inside = placed.X >= monitorPx.Left - 0.5 && placed.X + pxWidth <= monitorPx.Right + 0.5 &&
                         placed.Y >= monitorPx.Top - 0.5 && placed.Y + pxHeight <= monitorPx.Bottom + 0.5;

            report.AppendLine(
                $"    window 2600x1400 dip -> clamped {clamped.Width:0.#}x{clamped.Height:0.#} dip " +
                $"= {pxWidth:0}x{pxHeight:0} px");
            report.AppendLine(
                $"    placement from ({oldPx.Left:0},{oldPx.Top:0},{oldPx.Width:0},{oldPx.Height:0}) " +
                $"-> ({placed.X:0},{placed.Y:0}) inside={inside}");
            report.AppendLine();
        }

        return report.ToString();
    }

    private static nint MonitorOf(Screen screen)
    {
        var centre = new User32.POINT(
            screen.Bounds.Left + screen.Bounds.Width / 2,
            screen.Bounds.Top + screen.Bounds.Height / 2);

        return User32.MonitorFromPoint(centre, User32.MonitorDefaults.TONEAREST);
    }
}
