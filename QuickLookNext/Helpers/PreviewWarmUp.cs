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
using QuickLook.Common.Plugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace QuickLookNext.Helpers;

/// <summary>
/// v3.42.0: prepares the preview families in the background, so the first preview of a
/// family does not have to pay the one-time cost itself.
/// <para>
/// Measured on a warm app, the *second* preview of a family is always much faster
/// than the first: text 289 ms -> 96 ms, csv 205 -> 124, png 170 -> 100,
/// mp4 595 -> 413. The difference is one-time work - the plugin's panel XAML and
/// JIT, its native libraries, the WebView2 environment - that every user pays on
/// the first preview of that kind. This class pays it while the user is still
/// doing something else (the app starts at login). Since v6.5.0 this also applies to the
/// default timed low-memory policy: the first preview is commonly opened right after
/// startup, so the head start is prepared before the user asks for it.
/// </para>
/// <para>
/// The usage history decides both the family *order* and the candidates: the families
/// this user opens most are prepared first, so a preview that arrives mid-preparation is
/// most likely to be for one that is already ready. The user chooses how many of those
/// families to prepare from the tray menu. Three remains the balanced default; one is a
/// lower-memory choice and eight prepares every family that can be warmed safely.
/// </para>
/// <para>
/// Video, audio and Office are deliberately left out of the family list - preparing
/// them means starting playback, or synthesising a valid OOXML document and keeping a
/// Chromium environment for it.
/// </para>
/// </summary>
internal static class PreviewWarmUp
{
    private const string FamilyCountSetting = "WarmUpFamilyCount";

    /// <summary>
    /// The default: the two common families plus the most likely third one from the
    /// user's history.
    /// </summary>
    internal const int DefaultFamilyCount = 3;

    /// <summary>
    /// How many families make up "all of them" - the ones a sample file can be
    /// written for (see the class comment for why video, audio and Office are not in
    /// the list). The largest menu choice prepares the whole supported list.
    /// </summary>
    internal static int AllFamilies => Families.Count;

    internal readonly record struct FamilyCountChoice(
        int Count, string Key, string Name, string ShortKey, string ShortName);

    /// <summary>
    /// Four useful operating points rather than every number from one to eight:
    /// minimum footprint, balanced default, broader daily coverage and everything that
    /// can be warmed without starting playback or synthesising an Office document.
    /// </summary>
    internal static readonly FamilyCountChoice[] FamilyCountChoices =
    [
        new(1, "Icon_WarmUpFamilies_1",
            "1 most-used preview family",
            "Icon_WarmUpFamilies_1_Short", "1 family"),
        new(3, "Icon_WarmUpFamilies_3",
            "3 most-used preview families (default)",
            "Icon_WarmUpFamilies_3_Short", "3 families"),
        new(5, "Icon_WarmUpFamilies_5",
            "5 most-used preview families",
            "Icon_WarmUpFamilies_5_Short", "5 families"),
        new(8, "Icon_WarmUpFamilies_8",
            "All 8 supported preview families",
            "Icon_WarmUpFamilies_8_Short", "8 families"),
    ];

    /// <summary>The persisted choice shown by the tray menu and used by the next warm-up.</summary>
    internal static FamilyCountChoice CurrentFamilyCountChoice =>
        ChoiceFor(SettingHelper.Get(FamilyCountSetting, DefaultFamilyCount, "QuickLookNext"));

    internal static FamilyCountChoice ChoiceFor(int count)
    {
        var fallback = FamilyCountChoices.First(choice => choice.Count == DefaultFamilyCount);
        return FamilyCountChoices.FirstOrDefault(choice => choice.Count == count, fallback);
    }

    internal static void SetFamilyCount(int count)
    {
        if (!FamilyCountChoices.Any(choice => choice.Count == count))
            throw new ArgumentOutOfRangeException(nameof(count));

        SettingHelper.Set(FamilyCountSetting, count, "QuickLookNext");
    }

    /// <summary>
    /// Wait before the first family. At startup the app is still settling; when a preview
    /// is what started the preparation, that preview is the thing that must not compete
    /// with it.
    /// </summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(3);

    /// <summary>Pause between two families, so input is never held up for long.</summary>
    private static readonly TimeSpan BetweenFamilies = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a single family may take. The panels load their content
    /// asynchronously (that async load is exactly what makes the first preview of a
    /// family slow), so the warm-up has to wait for it instead of throwing the panel
    /// away mid-load.
    /// </summary>
    private static readonly TimeSpan FamilyTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The plugin -> sample extension map. Only families whose sample can be built
    /// cheaply and safely belong here: video/audio would start playback, Office
    /// needs a real document.
    /// </summary>
    private static readonly Dictionary<string, string> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["QuickLook.Plugin.TextViewer"] = ".txt",
        ["QuickLook.Plugin.MarkdownViewer"] = ".md",
        ["QuickLook.Plugin.HtmlViewer"] = ".html",
        ["QuickLook.Plugin.CsvViewer"] = ".csv",
        ["QuickLook.Plugin.ImageViewer"] = ".png",
        ["QuickLook.Plugin.PDFViewer"] = ".pdf",
        ["QuickLook.Plugin.ArchiveViewer"] = ".zip",
        ["QuickLook.Plugin.FontViewer"] = ".ttf",
    };

    /// <summary>
    /// Used when the usage history is too short to choose from (a fresh install, or
    /// a user whose history was just reset): the formats that make up most previews.
    /// </summary>
    private static readonly string[] DefaultOrder =
    [
        "QuickLook.Plugin.TextViewer",
        "QuickLook.Plugin.ImageViewer",
        "QuickLook.Plugin.MarkdownViewer",
        "QuickLook.Plugin.PDFViewer",
        "QuickLook.Plugin.CsvViewer",
        "QuickLook.Plugin.HtmlViewer",
    ];

    private static readonly string[] _warmed = new string[Families.Count];
    private static int _warmedCount;
    private static bool _started;

    /// <summary>
    /// When the last real preview request arrived. The warm-up builds panels on the
    /// UI thread, so a request that lands just before one of those builds would sit
    /// behind it; the loop below waits for a quiet moment instead.
    /// </summary>
    private static long _lastPreviewRequestMs;

    /// <summary>How long after a preview request the warm-up keeps its hands off the UI thread.</summary>
    private static readonly TimeSpan PreviewQuiet = TimeSpan.FromMilliseconds(1200);

    /// <summary>The longest the warm-up waits for that quiet moment before going ahead.</summary>
    private static readonly TimeSpan PreviewQuietLimit = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PreviewQuietPoll = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Called (on the UI thread) whenever a real preview is about to be shown.
    /// </summary>
    internal static void NotePreviewRequested()
        => Volatile.Write(ref _lastPreviewRequestMs, Environment.TickCount64);

    /// <summary>
    /// Whether a call to <see cref="Start"/> from this point may begin. An ordinary launch
    /// prepares the families immediately. The process restarted after an idle release is
    /// cold and waits for the next real preview, which clears that state first.
    /// </summary>
    internal static bool ShouldStart(bool afterFirstPreview)
        => afterFirstPreview || StartupWarmUp.IsEnabled;

    /// <summary>Whether this session has started preparing the families (test hook).</summary>
    internal static bool HasStarted => _started;

    /// <summary>Keeps unit tests isolated from the static one-start guard.</summary>
    internal static void ResetForTests()
    {
        _started = false;
        _warmedCount = 0;
        Array.Clear(_warmed);
    }

    internal static void Start(bool afterFirstPreview = false)
    {
        if (_started)
            return;

        if (!ShouldStart(afterFirstPreview))
            return;

        _started = true;

        // ApplicationIdle: the work starts only once the UI thread has nothing else
        // to do, and a preview request always wins the race for the dispatcher. The
        // loop itself awaits, so the UI thread stays free while a panel loads.
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                // Reading the usage history and dropping the previous session's
                // samples are file-system work. Keep both off the cold-start UI
                // thread; the first family still waits its normal delay after this.
                List<string> families;
                try
                {
                families = await Task.Run(() => ChooseFamilies(CurrentFamilyCountChoice.Count));
                }
                catch
                {
                    // A warm-up that cannot choose its families simply does nothing.
                    return;
                }

                if (families.Count == 0)
                    return;

                ProcessHelper.WriteLog(
                    $"Preview warm-up: preparing {families.Count} families " +
                    $"({string.Join(", ", families)})");

                await Task.Run(WarmUpSamples.ResetFolder);
                await Task.Delay(FirstDelay);

                foreach (var family in families)
                {
                    await WaitForPreviewQuietAsync();

                    // A request queued while the previous family loaded must be
                    // dispatched before the next panel build: the loop's awaits
                    // otherwise resume at Normal priority, ahead of it.
                    await YieldToDispatcherAsync(DispatcherPriority.Background);

                    try
                    {
                        await WarmOneAsync(family);
                    }
                    catch
                    {
                        // Warming up is best effort; a preview must never be affected.
                    }

                    await Task.Delay(BetweenFamilies);
                }

                WriteDiagnostics();
            }),
            DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Waits until no preview has been requested for <see cref="PreviewQuiet"/>, so a
    /// panel build does not hold the UI thread when someone asks for a preview.
    /// Bounded by <see cref="PreviewQuietLimit"/>: a user who previews continuously
    /// still gets a completed warm-up eventually.
    /// </summary>
    private static async Task WaitForPreviewQuietAsync()
    {
        var deadline = Environment.TickCount64 + (long)PreviewQuietLimit.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            var since = Environment.TickCount64 - Volatile.Read(ref _lastPreviewRequestMs);
            if (since >= (long)PreviewQuiet.TotalMilliseconds)
                return;

            await Task.Delay(PreviewQuietPoll);
        }
    }

    /// <summary>
    /// Finishes once the dispatcher has drained everything at a higher priority than
    /// <paramref name="priority"/>. Runs on the UI thread, so a caller that awaited
    /// from a Normal-priority continuation can let queued input and preview requests
    /// go first.
    /// </summary>
    private static Task YieldToDispatcherAsync(DispatcherPriority priority)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || !dispatcher.CheckAccess() ||
            dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(priority, new Action(() => done.TrySetResult(true)));
        return done.Task;
    }

    /// <summary>
    /// The families to prepare, most used first. Exposed for the tests, which pin that a
    /// count covering the whole list really does choose every family.
    /// </summary>
    internal static List<string> ChooseFamilies(int count)
        => ChooseFamilies(count, PluginUsageTracker.GetAllByUsage());

    /// <summary>
    /// Chooses the requested number of warmable families from a complete usage ranking.
    /// Exposed separately so the tests can prove that an un-warmable plugin does not
    /// consume one of the requested slots.
    /// </summary>
    internal static List<string> ChooseFamilies(int count, IEnumerable<string> usageOrder)
    {
        var chosen = new List<string>();

        foreach (var plugin in usageOrder)
        {
            if (Families.ContainsKey(plugin) && !chosen.Contains(plugin))
                chosen.Add(plugin);
        }

        // Top up with the common formats (a fresh install has no history) and then with
        // whatever is left of the family list, so that a count of AllFamilies really
        // prepares every one of them rather than the six the order happens to name.
        foreach (var plugin in DefaultOrder.Concat(Families.Keys))
        {
            if (chosen.Count >= count)
                break;

            if (!chosen.Contains(plugin))
                chosen.Add(plugin);
        }

        return chosen.Take(count).ToList();
    }

    private static async Task WarmOneAsync(string pluginName)
    {
        var host = ViewWindowManager.GetInstance().CurrentViewerWindow;
        if (host == null)
            return;

        var manager = PluginManager.GetInstance();
        var plugin = manager.GetLoadedPluginsSnapshot().FirstOrDefault(p =>
                         string.Equals(p.GetType().Assembly.GetName().Name, pluginName,
                             StringComparison.OrdinalIgnoreCase))
                     ?? manager.LoadPluginByName(pluginName);

        if (plugin == null)
            return;

        // v5.8.0: the session start means a preview is on screen while the families are
        // prepared - and a plugin keeps its panel in its own fields (View assigns them,
        // Cleanup disposes them), so preparing the family that is on screen right now would
        // take that panel out from under the user. It is the one family that has already
        // paid for itself anyway.
        // The comparison is by name, not by instance: the same plugin can be loaded more than
        // once (the built-in folder is discovered on its own, and a copy of it can be loaded
        // from the data folder as well), and the instance the warm-up resolved is not
        // necessarily the one the window is showing.
        if (IsOnScreen(host, pluginName))
            return;

        manager.EnsurePluginReady(plugin);

        var sample = WarmUpSamples.Create(Families[pluginName]);
        if (sample == null)
            return;

        // A throwaway context: the panel is built and thrown away, so nothing is
        // ever attached to (or shown in) the real preview window.
        var context = new ContextObject { Source = host, IsWarmUp = true };

        try
        {
            plugin.Prepare(sample, context);
            plugin.View(sample, context);

            // Let the panel finish its asynchronous load - that is the work the
            // first real preview would otherwise have to wait for.
            var deadline = DateTime.UtcNow + FamilyTimeout;
            while (context.IsBusy && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            // A short grace period: several panels report "ready" and only then
            // finish reading the file in a trailing background task.
            await Task.Delay(200);

            if (_warmedCount < _warmed.Length)
                _warmed[_warmedCount++] = pluginName;
        }
        finally
        {
            try
            {
                if (context.ViewerContent is IDisposable disposable)
                    disposable.Dispose();
            }
            catch
            {
                // Best effort.
            }

            context.ViewerContent = null;
            context.PendingViewerContent = null;

            // ... and if the user previewed this very family while the sample was loading,
            // the plugin's fields are the user's panel now, so Cleanup must be left to the
            // window that owns it.
            if (!IsOnScreen(host, pluginName))
            {
                try
                {
                    plugin.Cleanup();
                }
                catch
                {
                    // Best effort.
                }
            }
        }
    }

    /// <summary>
    /// Whether the preview on screen right now is handled by this family.
    /// </summary>
    private static bool IsOnScreen(ViewerWindow host, string pluginName)
    {
        var live = host.Plugin;

        return live != null && string.Equals(
            live.GetType().Assembly.GetName().Name, pluginName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Test hook: what the warm-up prepared this session, so the smoke test can
    /// assert the background preparation really ran.
    /// </summary>
    private static void WriteDiagnostics()
    {
        // v5.1.0: this used to write on every start - the guard was "is a smoke directory set?",
        // and App.SmokeDir falls back to %TEMP%\ql-smoke, so the answer was always yes. The file
        // belongs to the run that asked for it (the same trap as the update prompt in 5.0.11).
        if (!App.IsWarmUpDiagEnabled)
            return;

        try
        {
            var dir = App.SmokeDir;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "warmup.txt"),
                string.Join(",", _warmed.Take(_warmedCount)) + Environment.NewLine);
        }
        catch
        {
            // Diagnostics only.
        }
    }
}
