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

namespace QuickLookNext.Helpers;

/// <summary>
/// v5.2.0: the "low memory mode" switch (<c>LowMemoryMode</c>, off by default).
///
/// <para>
/// Both startup warm-ups exist to make the first preview fast, and both pay for it with
/// memory that stays resident for the life of the tray process. Measured on this machine
/// (single 200% display, nothing previewed, 60 s idle):
/// </para>
/// <list type="table">
///   <item><description>nothing warmed: 61 MB private / 122 MB working set</description></item>
///   <item><description>preview window warmed off-screen: +51 MB (112-118 MB)</description></item>
///   <item><description>plus the two most-used preview families: +51 MB (169 MB)</description></item>
/// </list>
/// <para>
/// The window warm-up buys the first preview ~200 ms, the family warm-up 100-200 ms per
/// family (text 289-&gt;96 ms, image 170-&gt;100 ms). Users who would rather have the
/// memory back than those milliseconds turn this on and the app idles at the 61 MB
/// baseline; the price is paid once, on the first preview of each kind.
/// </para>
/// </summary>
internal static class StartupWarmUp
{
    private const string Setting = "LowMemoryMode";

    /// <summary>Whether the low memory mode is on (read fresh - the tray menu flips it).</summary>
    internal static bool IsLowMemoryMode => SettingHelper.Get(Setting, false, "QuickLookNext");

    /// <summary>
    /// Whether the startup warm-ups may run. Both of them ask this, so the two can never
    /// disagree about what the mode means.
    /// </summary>
    internal static bool IsEnabled => !IsLowMemoryMode;

    internal static void Toggle()
    {
        SettingHelper.Set(Setting, !IsLowMemoryMode, "QuickLookNext");
    }
}
