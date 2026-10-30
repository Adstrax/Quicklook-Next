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
/// v6.5.0: the low-memory policy controls when warm memory is handed back, not
/// whether the application gets a head start.
///
/// <para>
/// Both startup warm-ups exist to make the first preview fast, and both pay for it with
/// memory that stays resident for the life of the tray process. Measured on this machine
/// (single 200 % display, nothing previewed):
/// </para>
/// <list type="table">
///   <item><description>low memory mode, after an idle release: 61 MB private / 122 MB working set</description></item>
///   <item><description>preview window warmed off-screen: +51 MB (112-118 MB)</description></item>
///   <item><description>window plus every preview family: 217 MB private / 320 MB working set</description></item>
/// </list>
/// <para>
/// The window warm-up buys ~200 ms, the family warm-up 100-200 ms per family (text
/// 289-&gt;96 ms, image 170-&gt;100 ms). The app now warms on every normal launch,
/// because the first file is commonly opened immediately after startup. Once a complete
/// fixed window contains no preview it restarts into a deliberate cold-idle process,
/// which skips both warm-ups until the next real preview.
/// </para>
///
/// <para>
/// The release restart passes the internal <c>/lowmem-idle</c> switch. That process
/// enters <see cref="IsColdIdle"/> and does no warm-up, so the release timer cannot
/// restart it forever. A real preview clears the state and starts a fresh warm session.
/// </para>
/// </summary>
internal static class StartupWarmUp
{
    private const string Setting = "LowMemoryMode";

    /// <summary>Whether the low memory mode is on (read fresh - the tray menu flips it).</summary>
    internal static bool IsLowMemoryMode => SettingHelper.Get(Setting, true, "QuickLookNext");

    /// <summary>
    /// True only in the process started after an idle release. It deliberately has no
    /// timer or warm-up of its own; the next preview leaves this state.
    /// </summary>
    internal static bool IsColdIdle { get; private set; }

    /// <summary>
    /// Whether the startup warm-ups may run. Both of them ask this, so they cannot
    /// disagree about whether this launch should be cold.
    /// </summary>
    internal static bool IsEnabled => !IsColdIdle;

    /// <summary>
    /// Marks the process restarted by the idle-memory release. Called once while the
    /// command line is parsed, before the preview manager and warm-ups are created.
    /// </summary>
    internal static void SetColdIdle(bool coldIdle) => IsColdIdle = coldIdle;

    internal static void Toggle()
    {
        SettingHelper.Set(Setting, !IsLowMemoryMode, "QuickLookNext");
    }

    /// <summary>
    /// v5.6.4: the menu picks a complete mode state rather than toggling a checkbox, so it
    /// sets the flag directly.
    /// </summary>
    internal static void Set(bool on)
    {
        SettingHelper.Set(Setting, on, "QuickLookNext");
    }
}
