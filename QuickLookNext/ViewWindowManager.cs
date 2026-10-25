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
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace QuickLookNext;

public class ViewWindowManager : IDisposable
{
    private static ViewWindowManager _instance;

    private string _invokedPath = string.Empty;
    private long _previewRequestVersion;
    private readonly PreviewSwitchCadence _switchCadence = new();
    private ViewerWindow _viewerWindow;

    // v5.6.2: which window gets warmed, and when the parked one is handed back -
    // see PreviewSession for the measurements behind this.
    private readonly PreviewSession _session = new();
    private DispatcherTimer _sessionTimer;

    internal ViewWindowManager()
    {
        // Opening and closing both move the idle deadline. Arming from the
        // session event keeps those two paths in sync instead of relying on
        // every caller to remember the timer after changing the session.
        _session.ActivityChanged += (_, _) => ArmSessionTimer();

        // v3.40.0 used to build the window here, inside RunListener, so the first
        // preview of a session would never wait for it.
        // v6.1.1: measurement showed that construction is ~170 ms on its own and
        // was the whole remaining cold-start cost (plugins finish at ~66 ms, the
        // window at ~234 ms). Nothing can ask for a preview before the message
        // loop is running, so the window is built on the first Background turn
        // instead: the tray icon appears while the window is still being parsed,
        // and any preview requested before it exists builds it on demand through
        // EnsureViewerWindow - the same path a preview uses after a close.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            InitNewViewerWindow();
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            // The app can be asked to exit before this turn runs; building a
            // window after the dispatcher is gone would throw on shutdown.
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;

            EnsureViewerWindow();
            App.RecordStartupPhase("after-deferred-viewer-window");
        }));
    }

    // v6.1.1: the closed instance survives one Background turn before its rebuild;
    // callers must not treat it as the live preview window.
    internal ViewerWindow CurrentViewerWindow =>
        _viewerWindow is { IsClosedWindow: false } ? _viewerWindow : null;

    /// <summary>
    /// v5.6.9: the file being previewed, empty when nothing is. The mouse hook asks this instead of the
    /// preview window's visibility, because the parked warm window is always "visible" off-screen.
    /// </summary>
    internal string PreviewedPath => _invokedPath;

    /// <summary>
    /// v5.6.9: the preview window's handle for the mouse hook, or zero when the window has never been
    /// shown - a handle that does not exist cannot be under the cursor.
    /// </summary>
    internal nint PreviewWindowHandle =>
        _viewerWindow == null || _viewerWindow.IsClosedWindow
            ? IntPtr.Zero
            : new System.Windows.Interop.WindowInteropHelper(_viewerWindow).Handle;

    private ViewerWindow EnsureViewerWindow()
    {
        // v6.1.1: a closed window is rebuilt on the next Background turn (see the
        // Closed handler), so between the close and that turn the field still holds
        // the closed instance. Showing it again would throw.
        if (_viewerWindow == null || _viewerWindow.IsClosedWindow)
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
        _switchCadence.Reset();
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
        {
            // The keyboard and the focus monitor both arrive here. Coalescing the read
            // keeps rapid arrow presses from blocking the UI thread and then replaying
            // a selection that has already moved on.
            if (FocusMonitor.GetInstance().RequestSelectionSwitch())
                return;

            path = NativeMethods.QuickLookNext.GetCurrentSelection();
        }

        if (string.IsNullOrEmpty(path))
            return;

        InvokePreview(path);
    }

    /// <summary>
    /// A side-button step already comes from a serialized navigator. It is a deliberate preview
    /// switch, so it keeps the direct path instead of joining the keyboard's settle window.
    /// </summary>
    internal void SwitchPreviewNow(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        InvokePreviewCore(path, coalesceSelectionBurst: false);
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
        InvokePreviewCore(path, coalesceSelectionBurst: true);
    }

    private void InvokePreviewCore(string path, bool coalesceSelectionBurst)
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

        // v6.1.1: a real preview is the work that matters; the background warm-up
        // must not queue a panel build in front of it (see PreviewWarmUp).
        Helpers.PreviewWarmUp.NotePreviewRequested();
        App.RecordPreviewPhase("request", path);

        var switching = window.IsVisible && !string.IsNullOrEmpty(_invokedPath);
        if (switching)
            window.QuiescePreview();

        var settleDelayMs = switching && coalesceSelectionBurst
            ? _switchCadence.GetDelayMs(Environment.TickCount64)
            : 0;

        _invokedPath = path;
        var requestVersion = ++_previewRequestVersion;

        RunFocusMonitor();

        // v3.31.0: matching can mean loading plugin assemblies (the first
        // preview of a format handled by a rarely-used built-in). FindMatchAsync
        // moves that work to the thread pool; the common case still completes
        // synchronously, so nothing about a normal preview changes.
        _ = BeginShowAsync(path, requestVersion, settleDelayMs);
    }

    /// <summary>
    /// v3.31.0: resolves the plugin for <paramref name="path"/> and shows the
    /// preview. A request that was superseded while the plugin search ran is
    /// dropped, and a failing search is reported like a failing plugin.
    /// </summary>
    private async Task BeginShowAsync(string path, long requestVersion, int settleDelayMs = 0)
    {
        if (settleDelayMs > 0)
        {
            // Let a burst finish before paying for plugin matching and Prepare. A newer request
            // bumps the version, so this one returns below without loading its intermediate file.
            await Task.Delay(settleDelayMs).ConfigureAwait(true);

            if (requestVersion != _previewRequestVersion || _invokedPath != path)
                return;
        }

        IViewer matchedPlugin;

        try
        {
            matchedPlugin = await PluginManager.GetInstance().FindMatchAsync(path).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog(e.ToString());
            CurrentPluginFailed(path, ExceptionDispatchInfo.Capture(e), requestVersion);
            return;
        }

        // A newer preview request (or a close) superseded this one while the
        // search was running on the thread pool.
        if (requestVersion != _previewRequestVersion || _invokedPath != path)
            return;

        App.RecordPreviewPhase("matched", path);
        try
        {
            BeginShowNewWindow(path, matchedPlugin, requestVersion);
        }
        catch (Exception e)
        {
            // BeginShowAsync is fire-and-forget. An exception from Show (a broken
            // template, a compositor failure) used to escape the task and surface
            // later as an unobserved exception on the finalizer thread. Treat it
            // like a plugin failure so the fallback path can recover, and never
            // let the second failure escape either.
            ProcessHelper.WriteLog(e.ToString());
            try
            {
                CurrentPluginFailed(path, ExceptionDispatchInfo.Capture(e), requestVersion);
            }
            catch (Exception fallback)
            {
                ProcessHelper.WriteLog(fallback.ToString());
            }
        }
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

        Helpers.PreviewWarmUp.NotePreviewRequested();

        RunFocusMonitor();

        var pluginManager = PluginManager.GetInstance();
        pluginManager.EnsureLoaded();

        var matchedPlugin = pluginManager.GetLoadedPluginsSnapshot().FirstOrDefault(p =>
            p.GetType().Assembly.GetName().Name == plugin);

        // v3.4.0: rare built-ins (e.g. MediaInfoViewer) are lazy-loaded; a
        // More-menu action can still target them by assembly name.
        matchedPlugin ??= pluginManager.LoadPluginByName(plugin);

        if (matchedPlugin != null)
        {
            pluginManager.EnsurePluginReady(matchedPlugin);
            EnsureViewerWindow().QuiescePreview();
            _invokedPath = path;
            var requestVersion = ++_previewRequestVersion;
            BeginShowNewWindow(path, matchedPlugin, requestVersion);
        }
    }

    public void ReloadPreview()
    {
        var window = EnsureViewerWindow();
        if (!window.IsVisible || string.IsNullOrEmpty(_invokedPath))
            return;

        Helpers.PreviewWarmUp.NotePreviewRequested();

        window.QuiescePreview();
        var requestVersion = ++_previewRequestVersion;
        _ = BeginShowAsync(_invokedPath, requestVersion);
    }

    public void ToggleFullscreen()
    {
        var window = EnsureViewerWindow();
        if (!window.IsVisible)
            return;

        window.ToggleFullscreen();
    }

    private void BeginShowNewWindow(string path, IViewer matchedPlugin, long requestVersion)
    {
        if (requestVersion != _previewRequestVersion || _invokedPath != path)
            return;

        // This is the last authoritative "show" point, including the default-plugin
        // fallback after a failure. Keep the toggle state true even if parking the
        // failed preview cleared it a moment ago.
        _invokedPath = path;
        App.RecordPreviewPhase("show", path);

        if (App.IsMemoryDiagnosticsEnabled)
            Helpers.MemoryDiagnostics.Snapshot("preview-open");

        // v5.6.2: from here on the low memory mode keeps a warm window around, so a
        // folder full of files previews at the same speed as the normal mode does.
        _session.Opened(Environment.TickCount64);

        // v5.8.0: the family warm-up follows the session the same way the window warm-up
        // does. The normal mode has already prepared the families while nobody was
        // waiting (this call is then a no-op); the low memory mode deliberately prepares
        // nothing while idle and starts here instead, on the first preview of the
        // session, so the next file - of any kind - does not pay the one-time cost.
        Helpers.PreviewWarmUp.Start(afterFirstPreview: true);

        // A failed preview fallback parks and clears the focus monitor before it asks
        // for the default plugin; make sure the replacement preview follows selection again.
        RunFocusMonitor();

        EnsureViewerWindow().UnloadPlugin();

        _viewerWindow.BeginShow(matchedPlugin, path,
            (failedPath, exception) => CurrentPluginFailed(failedPath, exception, requestVersion));
    }

    private void CurrentPluginFailed(string path, ExceptionDispatchInfo e, long requestVersion)
    {
        if (requestVersion != _previewRequestVersion || _invokedPath != path)
            return;

        var plugin = _viewerWindow.Plugin?.GetType();

        _viewerWindow.Close();

        TrayIconManager.ShowNotification($"Failed to preview {Path.GetFileName(path)}",
            "Consider reporting this incident to QuickLookNext’s author.", true);

        Debug.WriteLine(e.SourceException.ToString());

        ProcessHelper.WriteLog(e.SourceException.ToString());

        if (plugin != PluginManager.GetInstance().DefaultPlugin.GetType())
        {
            _invokedPath = path;
            BeginShowNewWindow(path, PluginManager.GetInstance().DefaultPlugin, requestVersion);
        }
        else
            e.Throw();
    }

private void InitNewViewerWindow()
{
    _viewerWindow = new ViewerWindow();
    App.RecordStartupPhase("after-viewer-window-ctor");
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

        // v6.1.1: rebuilding here cost ~120 ms of UI thread inside the close, right
        // after Esc. The next window is built on the next Background turn instead;
        // a Space pressed before that builds it through EnsureViewerWindow.
        var closedWindow = w;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            InitNewViewerWindow();
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;

            // A preview already asked for a window in the meantime.
            if (!ReferenceEquals(_viewerWindow, closedWindow))
                return;

            InitNewViewerWindow();
        }));
        };

    // v1.2.36: warm up the first Show during startup idle, off-screen, so the
    // first preview appears instantly instead of waiting ~200 ms.
    // v5.2.0: skipped in the low memory mode - the window (and the rendering stack it
    // pulls in) is then created on the first preview instead, ~+51 MB idle either way.
    // v5.6.2: creation used to happen on every close, so skipping this outright made
    // every preview pay the first-Show cost. It now follows the session: nothing is
    // warmed before the user previews anything, everything is warmed while they keep
    // previewing, and the parked window is released after PreviewSession.IdleTimeout
    // of no previews.
    if (_session.ShouldWarm(Helpers.StartupWarmUp.IsLowMemoryMode))
        _viewerWindow.WarmUp();
    App.RecordStartupPhase("after-viewer-window-warmup");

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
