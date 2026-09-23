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

namespace QuickLook.Common.Helpers;

/// <summary>
/// v5.2.0: one place that knows the display scaling the app is currently rendering at, and tells
/// whoever cares when it changes.
///
/// <para>
/// The preview window is the only component that is *guaranteed* to be told about a scaling change
/// (WPF raises <c>Window.DpiChanged</c>), but the things that must react to it live further down:
/// a <c>WebView2</c> control keeps the scale it was created with, and the plugin host keeps such
/// controls parked for reuse - which is how a Markdown preview ended up laying its content out for
/// the screen scaling that was in effect when the control was born (upstream #1956, "the display
/// area is smaller than the window area").
/// </para>
///
/// <para>
/// The window reports its scale here; the WebView2 pool drops its parked controls when it hears
/// about a change, so the next preview builds a control for the scale that is in effect now. This
/// is deliberately a plain counter + event rather than a WPF event so both sides can use it
/// without a project reference between the app and the plugin host library.
/// </para>
/// </summary>
public static class DisplayScale
{
    private static readonly object Sync = new();

    /// <summary>The scale last reported, or 0 while nothing has reported yet.</summary>
    public static double Current { get; private set; }

    /// <summary>Raised (on the reporting thread) when the scale really changed.</summary>
    public static event Action Changed;

    /// <summary>
    /// Reports the scale the app is rendering at now. Nothing happens when it is the same value
    /// again - the window reports on every DPI notification, and a change is a rare event.
    /// </summary>
    public static void Notify(double pixelsPerDip)
    {
        if (pixelsPerDip <= 0d || double.IsNaN(pixelsPerDip))
            return;

        lock (Sync)
        {
            if (Math.Abs(Current - pixelsPerDip) < 0.001d)
                return;

            Current = pixelsPerDip;
        }

        Changed?.Invoke();
    }

    /// <summary>Forgets the current scale - used by the tests.</summary>
    internal static void Reset()
    {
        lock (Sync)
            Current = 0d;
    }
}
