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
using System.Linq;

namespace QuickLookNext.Helpers;

/// <summary>
/// v5.6.4: the whole low memory mode as one choice.
///
/// <para>
/// The mode used to be two menu items that depended on each other - a "low memory mode"
/// checkbox in Options and a separate "release memory" group with the wait - which meant
/// the second one was meaningless while the first was off. The menu now offers complete
/// states instead: off, release after 90 seconds / 5 minutes / 15 minutes / 1 hour, or
/// never. The setting behind it is unchanged (<c>LowMemoryMode</c> plus
/// <c>LowMemoryReleaseSeconds</c>), so nothing has to be migrated.
/// </para>
///
/// <para>
/// "Never" is a real state rather than an off switch: the startup warm-up stays off, and
/// because the parked preview window is never released, previews are warm from the first
/// one onwards - from the second preview on the mode behaves like the normal mode, with
/// the memory the normal mode keeps.
/// </para>
///
/// <para>
/// The wait exists because the memory cannot be given back in-process: after one preview
/// the managed heap is about 9 MB while the private bytes sit ~80 MB above the cold
/// baseline, because the preview loads native modules that stay loaded for the life of the
/// process (ImageMagick ~23 MB, and the GPU driver's shader compiler ~74 MB when WPF
/// renders on the GPU). So the release is a restart: the tray process starts itself again
/// with <c>/autorun</c> (silent - no "started" notification) and comes back at the cold
/// baseline. Nothing is lost; settings and the portable data folder are on disk.
/// </para>
/// </summary>
internal static class LowMemoryMode
{
    private const string Setting = "LowMemoryReleaseSeconds";

    /// <summary>Longest wait the setting accepts; anything longer is treated as a typo.</summary>
    private const int MaximumSeconds = 24 * 60 * 60;

    /// <summary>
    /// One state of the mode: whether it is on, how long it waits before releasing, and
    /// the text that goes with it. <see cref="Seconds"/> is ignored when it is off.
    /// <para>
    /// <see cref="ShortName"/> is what the menu's group header shows ("Low memory mode:
    /// 90 s"): the menu is sized to its content, and "Release after 90 seconds" there made
    /// the whole menu noticeably wider than a Windows context menu.
    /// </para>
    /// </summary>
    internal readonly record struct Choice(
        bool Enabled, int Seconds, string Key, string Name, string ShortKey, string ShortName);

    /// <summary>The states the tray menu offers, in the order it shows them.</summary>
    internal static readonly Choice[] Choices =
    [
        new(false, 0, "Icon_LowMemory_Off", "Off (keep warm-up)",
            "Icon_LowMemory_Off_Short", "Off"),
        new(true, 90, "Icon_Release_90s", "Release after 90 seconds",
            "Icon_Release_90s_Short", "90 s"),
        new(true, 300, "Icon_Release_5m", "Release after 5 minutes",
            "Icon_Release_5m_Short", "5 min"),
        new(true, 900, "Icon_Release_15m", "Release after 15 minutes",
            "Icon_Release_15m_Short", "15 min"),
        new(true, 3600, "Icon_Release_1h", "Release after 1 hour",
            "Icon_Release_1h_Short", "1 h"),
        new(true, 0, "Icon_Release_Never", "Never (like normal mode)",
            "Icon_Release_Never_Short", "Never"),
    ];

    internal static int DefaultSeconds => 90;

    /// <summary>
    /// The configured wait, in seconds; <c>0</c> means "never release". A stored value
    /// that is not a sane duration falls back to the default rather than leaving the mode
    /// with no wait at all.
    /// </summary>
    internal static int Seconds
    {
        get
        {
            var stored = SettingHelper.Get(Setting, DefaultSeconds, "QuickLookNext");
            return stored is < 0 or > MaximumSeconds ? DefaultSeconds : stored;
        }
    }

    /// <summary>The state the menu should show as selected.</summary>
    internal static Choice Current => ChoiceFor(StartupWarmUp.IsLowMemoryMode, Seconds);

    /// <summary>
    /// The choice that matches a mode flag and a wait. An unseen wait (a hand-edited
    /// config) reports the default one, so the menu always has something to tick.
    /// </summary>
    internal static Choice ChoiceFor(bool enabled, int seconds)
    {
        if (!enabled)
            return Choices[0];

        return Choices.FirstOrDefault(choice => choice.Enabled && choice.Seconds == seconds, Choices[1]);
    }

    /// <summary>Applies a choice: turns the mode on or off and stores the wait.</summary>
    internal static void Apply(Choice choice)
    {
        StartupWarmUp.Set(choice.Enabled);

        if (choice.Enabled)
            SettingHelper.Set(Setting, choice.Seconds, "QuickLookNext");
    }
}
