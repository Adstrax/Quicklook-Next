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
/// v5.6.3: how long the low memory mode waits after the last preview before it hands
/// its memory back - the choice the tray menu offers, and the setting behind it.
///
/// <para>
/// Handing it back in-process was tried and cannot work: after one preview the managed
/// heap is about 9 MB, while the private bytes sit ~80 MB above the cold baseline,
/// because the preview loads native modules that stay loaded for the life of the
/// process (ImageMagick ~23 MB, and the GPU driver's shader compiler ~74 MB when WPF
/// renders on the GPU). So the release is a restart: the tray process starts itself
/// again with <c>/autorun</c> (silent - no "started" notification) and comes back at
/// the cold baseline. Nothing is lost; settings and the portable data folder are on
/// disk.
/// </para>
///
/// <para>
/// The wait is the user's choice because the trade is theirs: a short wait gives the
/// memory back sooner but makes a preview after the wait a cold one, a long wait keeps
/// previews quick for as long as the user is likely to come back. <c>0</c> switches the
/// release off entirely.
/// </para>
/// </summary>
internal static class LowMemoryRelease
{
    private const string Setting = "LowMemoryReleaseSeconds";

    /// <summary>Longest wait the setting accepts; anything longer is treated as a typo.</summary>
    private const int MaximumSeconds = 24 * 60 * 60;

    /// <summary>One choice in the menu: the value, its translation key and its English name.</summary>
    internal readonly record struct Option(int Seconds, string Key, string Name);

    /// <summary>The choices the tray menu offers. 90 seconds is the default.</summary>
    internal static readonly Option[] Options =
    [
        new(90, "Icon_Release_90s", "After 90 seconds"),
        new(300, "Icon_Release_5m", "After 5 minutes"),
        new(900, "Icon_Release_15m", "After 15 minutes"),
        new(3600, "Icon_Release_1h", "After 1 hour"),
        new(0, "Icon_Release_Never", "Never"),
    ];

    internal static int DefaultSeconds => Options[0].Seconds;

    /// <summary>
    /// The configured wait, in seconds; <c>0</c> means "never". A stored value that is
    /// not a sane duration falls back to the default rather than disabling the release.
    /// </summary>
    internal static int Seconds
    {
        get
        {
            var stored = SettingHelper.Get(Setting, DefaultSeconds, "QuickLookNext");
            return stored is < 0 or > MaximumSeconds ? DefaultSeconds : stored;
        }
    }

    internal static void Set(int seconds) => SettingHelper.Set(Setting, seconds, "QuickLookNext");

    /// <summary>The option matching <paramref name="seconds"/>, or the default one.</summary>
    internal static Option OptionFor(int seconds)
        => Options.FirstOrDefault(option => option.Seconds == seconds, Options[0]);

    /// <summary>The current choice, for the menu header.</summary>
    internal static Option Current => OptionFor(Seconds);
}
