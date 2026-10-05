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
using QuickLookNext.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace QuickLookNext;

public class ViewWindowManager : IDisposable
{
    private static ViewWindowManager _instance;

    private string _invokedPath = string.Empty;
    private ViewerWindow _viewerWindow;

    // v5.6.2: which window gets warmed, and when the parked one is handed back -
    // see PreviewSession for the measurements behind this.
    private readonly PreviewSession _session = new();
    private DispatcherTimer _sessionTimer;

    internal ViewWindowManager()
    {
        // v3.40.0: build and warm the preview window right here, before the keyboard
        // hook, the tray menu and the pipe server start accepting requests, instead
        // of at ApplicationIdle. The window is built either way - the difference is
        // who waits for it. At ApplicationIdle the first preview of a session (the
        // one asked for right after login) arrived while the window was still being
        // created and waited for it; here it happens while nothing can ask yet, at
        // the cost of the tray icon appearing ~0.3 s later.
        InitNewViewerWindow();
    }

    internal ViewerWindow CurrentViewerWindow => _viewerWindow;

    private ViewerWindow EnsureViewerWindow()
    {
        if (_viewerWindow == null)
            InitNewViewerWindow();

        return _viewerWindow;
    }

    public void Dispose()
    {
        StopFocusMonitor();
    }

    public void RunAndClosePreview()
    {
        if (string.IsNullOrEmpty(_invokedPath))
            return;

        var window = EnsureViewerWindow();
        if (!window.IsVisible)
            return;

        // if the current focus is in Desktop or explorer windows, just close the preview window and leave the task to System.
        var focus = NativeMethods.QuickLookNext.GetFocusedWindowType();
        if (focus != NativeMethods.QuickLookNext.FocusedWindowType.Invalid)
        {
            StopFocusMonitor();
            window.Close();
            return;
        }

        // if the focus is in the preview window, run it
        if (!WindowHelper.IsForegroundWindowBelongToSelf())
            return;

        StopFocusMonitor();
        window.RunAndClose();
    }

    public void ClosePreview()
    {
        if (string.IsNullOrEmpty(_invokedPath))
            return;

        var window = EnsureViewerWindow();
        if (!window.IsVisible)
            return;

        StopFocusMonitor();
        window.Close();
    }

    public void TogglePreview(string path = null, string options = null)
    {
        if (string.IsNullOrEmpty(path))
            path = NativeMethods.QuickLookNext.GetCurrentSelection();

        if (!string.IsNullOrEmpty(options))
            InvokePreviewWithOption(path, options);
        else
        {
            // v1.2.36: the warm-shown preview window is always "visible";
            // decide open/close from the tracked preview state instead of
            // window visibility.
            var hasActivePreview = !string.IsNullOrEmpty(_invokedPath)
                && (string.IsNullOrEmpty(path) || path == _invokedPath);
            if (hasActivePreview)
                ClosePreview();
            else
                InvokePreview(path);
        }
    }

    private void RunFocusMonitor()
    {
        // v1.2.14 test hook: automated benches can disable the selection-follow
        // polling (it otherwise reacts to Explorer's current selection).
        if (App.DisableFocusMonitor)
            return;

        FocusMonitor.GetInstance().Start();
    }

    private void StopFocusMonitor()
    {
        FocusMonitor.GetInstance().Stop();
    }

    internal void ForgetCurrentWindow()
    {
        StopFocusMonitor();

        EnsureViewerWindow().Pinned = true;

        InitNewViewerWindow();
    }

    public void SwitchPreview(string path = null)
    {
        var window = EnsureViewerWindow();
        if (!window.IsVisible)
            return;

        if (string.IsNullOrEmpty(path))
            path = NativeMethods.QuickLookNext.GetCurrentSelection();

        if (string.IsNullOrEmpty(path))
            return;

        InvokePreview(path);
    }

    public void InvokePreviewWithOption(string path = null, string options = null)
    {
        InvokePreview(path);

        if (string.IsNullOrWhiteSpace(options)) return;

        var cli = new CommandLineParser(options.Split(','));

        if (cli.Has("top"))
        {
            var window = EnsureViewerWindow();
            window.Topmost = true;
            window.buttonTop.Tag = "Top";
        }
        if (cli.Has("pin"))
        {
            EnsureViewerWindow().Pinned = true;
            ForgetCurrentWindow();
        }
    }

    public void InvokePreview(string path = null)
    {
        if (string.IsNullOrEmpty(path))
            path = NativeMethods.QuickLookNext.GetCurrentSelection();

        if (string.IsNullOrEmpty(path))
            return;

        var window = EnsureViewerWindow();
        if (window.IsVisible && path == _invokedPath)
            return;

        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            if (!path.StartsWith("::")) // CLSID
                return;

        // Check extension filtering before proceeding (skip for directories)
        if (!isDirectory && !ExtensionFilterHelper.IsExtensionAllowed(path))
            return;

        _invokedPath = path;

        RunFocusMonitor();

        // v3.31.0: matching can mean loading plugin assemblies (the first
        // preview of a format handled by a rarely-used built-in). FindMatchAsync
        // moves that work to the thread pool; the common case still completes
        // synchronously, so nothing about a normal preview changes.
        _ = BeginShowAsync(path);
    }

    /// <summary>
    /// v3.31.0: resolves the plugin for <paramref name="path"/> and shows the
    /// preview. A request that was superseded while the plugin search ran is
    /// dropped, and a failing search is reported like a failing plugin.
    /// </summary>
    private async Task BeginShowAsync(string path)
    {
        IViewer matchedPlugin;

        try
        {
            matchedPlugin = await PluginManager.GetInstance().FindMatchAsync(path).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog(e.ToString());
            CurrentPluginFailed(path, ExceptionDispatchInfo.Capture(e));
            return;
        }

        // A newer preview request (or a close) superseded this one while the
        // search was running on the thread pool.
        if (_invokedPath != path)
            return;

        BeginShowNewWindow(path, matchedPlugin);
    }

    public void InvokePluginPreview(string plugin, string path = null)
    {
        if (string.IsNullOrEmpty(path))
            path = _invokedPath;

        if (string.IsNullOrEmpty(path))
            return;

        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            return;

        // Check extension filtering before proceeding (skip for directories)
        if (!isDirectory && !ExtensionFilterHelper.IsExtensionAllowed(path))
            return;

        RunFocusMonitor();

        var pluginManager = PluginManager.GetInstance();
        pluginManager.EnsureLoaded();

        var matchedPlugin = pluginManager.LoadedPlugins.Find(p =>
        {
            return p.GetType().Assembly.GetName().Name == plugin;
        });

        // v3.4.0: rare built-ins (e.g. MediaInfoViewer) are lazy-loaded; a
        // More-menu action can still target them by assembly name.
        matchedPlugin ??= pluginManager.LoadPluginByName(plugin);

        if (matchedPlugin != null)
        {
            pluginManager.EnsurePluginReady(matchedPlugin);
            BeginShowNewWindow(path, matchedPlugin);
        }
    }

    public void ReloadPreview()
    {
        var window = EnsureViewerWindow();
        if (!window.IsVisible || string.IsNullOrEmpty(_invokedPath))
            return;

        _ = BeginShowAsync(_invokedPath);
    }

    public void ToggleFullscreen()
    {
        var window = EnsureViewerWindow();
        if (!window.IsVisible)
            return;

        window.ToggleFullscreen();
    }

    private void BeginShowNewWindow(string path, IViewer matchedPlugin)
    {
        if (App.IsMemoryDiagnosticsEnabled)
            Helpers.MemoryDiagnostics.Snapshot("preview-open");

        // v5.6.2: from here on the low memory mode keeps a warm window around, so a
        // folder full of files previews at the same speed as the normal mode does.
        _session.Opened(Environment.TickCount64);
        ArmSessionTimer();

        EnsureViewerWindow().UnloadPlugin();

        _viewerWindow.BeginShow(matchedPlugin, path, CurrentPluginFailed);
    }

    private void CurrentPluginFailed(string path, ExceptionDispatchInfo e)
    {
        var plugin = _viewerWindow.Plugin?.GetType();

        _viewerWindow.Close();

        TrayIconManager.ShowNotification($"Failed to preview {Path.GetFileName(path)}",
            "Consider reporting this incident to QuickLookNext’s author.", true);

        Debug.WriteLine(e.SourceException.ToString());

        ProcessHelper.WriteLog(e.SourceException.ToString());

        if (plugin != PluginManager.GetInstance().DefaultPlugin.GetType())
            BeginShowNewWindow(path, PluginManager.GetInstance().DefaultPlugin);
        else
            e.Throw();
    }

private void InitNewViewerWindow()
{
    _viewerWindow = new ViewerWindow();
    _viewerWindow.Closed += (sender, e) =>
        {
            if (ProcessHelper.IsShuttingDown())
                return;
            if (sender is not ViewerWindow w)
                return;
            // Only skip if the window was already forgotten by ForgetCurrentWindow,
            // which sets Pinned=true AND replaces _viewerWindow with a new instance.
            if (w.Pinned && _viewerWindow != w)
                return;
        // v1.2.36: the warm-shown preview window is always "visible", so the
        // toggle state must be tracked via _invokedPath; reset it when the
        // window closes so the next Space reopens the preview instead of
        // "closing" the off-screen warm window again.
        _invokedPath = string.Empty;
        _session.Closed(Environment.TickCount64);
        StopFocusMonitor();
        InitNewViewerWindow();
    };

    // v1.2.36: warm up the first Show during startup idle, off-screen, so the
    // first preview appears instantly instead of waiting ~200 ms.
    // v5.2.0: skipped in the low memory mode - the window (and the rendering stack it
    // pulls in) is then created on the first preview instead, ~+51 MB idle either way.
    // v5.6.2: but this runs on *every* close (the window is rebuilt here), so skipping
    // it outright made every preview pay the first-Show cost. It now follows the
    // session: nothing is warmed before the user previews anything, everything is
    // warmed while they keep previewing, and the parked window is released after
    // PreviewSession.IdleTimeout of no previews.
    if (_session.ShouldWarm(Helpers.StartupWarmUp.IsLowMemoryMode))
        _viewerWindow.WarmUp();

    ArmSessionTimer();
}

    /// <summary>
    /// v5.6.2: restarts the idle countdown. Called on every preview open and close, so
    /// it always measures "time since the user last previewed something".
    /// </summary>
    private void ArmSessionTimer()
    {
        // The normal mode keeps its window warm for the whole session - there is
        // nothing to hand back, so no timer.
        if (!Helpers.StartupWarmUp.IsLowMemoryMode || !_session.IsActive)
            return;

        // v5.6.3: the wait is the user's choice (tray menu -> Low memory mode); 0 means
        // they never want the release.
        var seconds = Helpers.LowMemoryMode.Seconds;
        if (seconds <= 0)
            return;

        _sessionTimer ??= new DispatcherTimer(
            TimeSpan.FromSeconds(seconds),
            DispatcherPriority.Background,
            OnSessionIdle,
            System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);

        // The interval is fixed at construction, so re-create it when the choice changes.
        if (Math.Abs(_sessionTimer.Interval.TotalSeconds - seconds) > 0.5)
            _sessionTimer.Interval = TimeSpan.FromSeconds(seconds);

        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void OnSessionIdle(object sender, EventArgs e)
    {
        _sessionTimer?.Stop();

        if (!_session.ShouldRelease(Environment.TickCount64,
                Helpers.StartupWarmUp.IsLowMemoryMode, !string.IsNullOrEmpty(_invokedPath),
                (long)TimeSpan.FromSeconds(Helpers.LowMemoryMode.Seconds).TotalMilliseconds))
            return;

        _session.End();

        // v5.6.3: in-process there is nothing left to give back - the managed heap is
        // ~9 MB after a preview while the private bytes sit ~80 MB above the baseline,
        // because the preview loaded native modules that cannot be unloaded while the
        // process lives (ImageMagick ~23 MB, the GPU driver's shader compiler ~74 MB).
        // Starting over is what actually returns the memory, so that is the release.
        if (CanReleaseByRestarting())
        {
            ProcessHelper.WriteLog(
                "Low memory mode: restarting to release the preview footprint");
            TrayIconManager.GetInstance().Restart(args: "/autorun", forced: true);
            return;
        }

        // Something is on screen (a panel, a dialog) - hand the parked window back and
        // let the next preview open a new session.
        try
        {
            _viewerWindow?.Close();
            ProcessHelper.PerformAggressiveGC();
        }
        catch (Exception ex)
        {
            ProcessHelper.WriteLog($"Releasing the warm preview window failed: {ex}");
        }
    }

    /// <summary>
    /// v5.6.3: a restart is silent and quick, but it must never interrupt something the
    /// user is looking at. The parked preview window is ours and is ignored; anything
    /// else on screen (tray menu, plugin manager, a dialog, the update progress panel)
    /// means "not now".
    /// </summary>
    private static bool CanReleaseByRestarting()
    {
        var application = System.Windows.Application.Current;
        if (application is null)
            return false;

        foreach (System.Windows.Window window in application.Windows)
        {
            if (window is ViewerWindow)
                continue;

            if (window.IsVisible)
                return false;
        }

        return true;
    }

    public static ViewWindowManager GetInstance()
    {
        return _instance ??= new ViewWindowManager();
    }
}
