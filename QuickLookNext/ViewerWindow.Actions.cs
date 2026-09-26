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
using QuickLook.Common.Plugin;
using QuickLook.Common.Plugin.MoreMenu;
using QuickLookNext.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Wpf.Ui.Violeta.Controls;
using WinForms = System.Windows.Forms;
using MenuItem = System.Windows.Controls.MenuItem;

namespace QuickLookNext;

public partial class ViewerWindow
{
    // v1.2.9: plugin-provided More menu items, snapshotted into the unified
    // Mica menu (TrayMenuWindow) every time the menu is opened.
    private readonly List<TrayMenuEntry> _pluginMoreMenuEntries = new();

    internal void Run()
    {
        if (string.IsNullOrEmpty(_path))
            return;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(_path)
            {
                WorkingDirectory = Path.GetDirectoryName(_path)
            });
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
        }
    }

    internal void RunAndClose()
    {
        Run();
        Close();
    }

    internal void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            // Exit fullscreen
            _isFullscreen = false;

            // Restore window properties
            WindowStyle = _preFullscreenWindowStyle;
            ResizeMode = _preFullscreenResizeMode;

            // Restore position and size
            Left = _preFullscreenBounds.Left;
            Top = _preFullscreenBounds.Top;
            Width = _preFullscreenBounds.Width;
            Height = _preFullscreenBounds.Height;

            // Restore window state last to avoid flicker
            WindowState = _preFullscreenWindowState;

            // Restore caption height and resize border thickness saved before fullscreen
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                chrome.CaptionHeight = _preFullscreenCaptionHeight;
                chrome.ResizeBorderThickness = _preFullscreenResizeBorderThickness;
            }

            // Restore rounded corners on Windows 11
            WindowHelper.SetWindowCorner(this, Dwmapi.WindowCornerStyle.Round);
            ApplyLayeredWindowRegion();
        }
        else
        {
            // Enter fullscreen
            _isFullscreen = true;

            // Save current window properties before any changes
            _preFullscreenWindowState = WindowState;
            _preFullscreenWindowStyle = WindowStyle;
            _preFullscreenResizeMode = ResizeMode;

            // Get current bounds (account for maximized state)
            if (WindowState == WindowState.Maximized)
            {
                _preFullscreenBounds = RestoreBounds;
            }
            else
            {
                _preFullscreenBounds = new Rect(Left, Top, Width, Height);
            }

            // Get the screen bounds where the window is currently located
            var screen = WinForms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var screenBounds = screen.Bounds;

            // Get DPI scale factor for proper coordinate conversion
            // scale.Horizontal and scale.Vertical contain the DPI scaling ratios (e.g., 1.5 for 150%)
            var scale = DisplayDeviceHelper.GetScaleFactorFromWindow(this);

            // Set to normal state first to allow manual positioning
            WindowState = WindowState.Normal;

            // Save and remove caption height and resize border so title area is not draggable and window is not resizable in fullscreen
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                _preFullscreenCaptionHeight = chrome.CaptionHeight;
                _preFullscreenResizeBorderThickness = chrome.ResizeBorderThickness;
                chrome.CaptionHeight = 0d;
                chrome.ResizeBorderThickness = new Thickness(0d);
            }

            // Hide window chrome for true fullscreen
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;

            // Convert screen bounds from physical pixels to DIPs for WPF
            var dipWidth = screenBounds.Width / scale.Horizontal;
            var dipHeight = screenBounds.Height / scale.Vertical;

            // Use MoveWindow to set position and size with proper DPI handling
            this.MoveWindow(screenBounds.Left, screenBounds.Top, dipWidth, dipHeight);

            // Remove rounded corners on Windows 11 for true fullscreen
            WindowHelper.SetWindowCorner(this, Dwmapi.WindowCornerStyle.DoNotRound);
            // Layered window: the rounded region would clip the fullscreen
            // surface, so drop it for the duration of fullscreen.
            WindowHelper.ClearWindowRegion(this);
        }
    }

    private void PositionWindow(Size size)
    {
        // If the window is now maximized, do not move it
        if (WindowState == WindowState.Maximized)
            return;

        size = new Size(Math.Max(MinWidth, size.Width), Math.Max(MinHeight, size.Height));

        // v1.2.36: the first real preview after the off-screen warm-up must
        // still use new-window centering (the warm-up rect is off-screen and
        // must not become the "old centre" for the resize math).
        var newRect = IsLoaded && !_warmShown
            ? ResizeAndCentreExistingWindow(size)
            : ResizeAndCentreNewWindow(size);

        this.MoveWindow(newRect.Left, newRect.Top, newRect.Width, newRect.Height);

        WriteWindowRectDiag(size, DesktopSizeForNextPlacement());
    }

    /// <summary>
    /// v5.0.10 (QL-Win/QuickLook#827): the monitor a window-sized request is
    /// measured against - which is the monitor the placement below is going to
    /// use. A new-window placement centres on the monitor of the foreground
    /// window (the source the user is previewing from), while an existing
    /// window keeps its position and is only pulled back onto its own monitor.
    /// </summary>
    private Size DesktopSizeForNextPlacement()
    {
        return IsLoaded && !_warmShown
            ? GetDesktopSizeInDip(new WindowInteropHelper(this).Handle)
            : GetTargetDesktopSizeInDip();
    }

    /// <summary>
    /// v5.0.10 (QL-Win/QuickLook#827): the monitor the preview is going to be
    /// shown on, in DIP. The preview never activates, so the foreground window
    /// is normally the Explorer (or other source) window the user is previewing
    /// from - the same window <see cref="ResizeAndCentreNewWindow"/> centres on.
    /// Falls back to the preview window's own monitor, then to the primary one.
    /// </summary>
    private Size GetTargetDesktopSizeInDip()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            hwnd = new WindowInteropHelper(this).Handle;

        if (hwnd == IntPtr.Zero)
            return new Size(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);

        return GetDesktopSizeInDip(hwnd);
    }

    /// <summary>
    /// v5.0.10: working area of the monitor that hosts <paramref name="hwnd"/>,
    /// converted from pixels to DIP with that same monitor's scale factor.
    /// </summary>
    private static Size GetDesktopSizeInDip(nint hwnd)
    {
        var desktop = WindowHelper.GetDesktopRectFromWindowInPixel(hwnd);
        var scale = DisplayDeviceHelper.GetScaleFactorFromWindow(hwnd);

        if (scale.Horizontal <= 0f || scale.Vertical <= 0f)
            return new Size(desktop.Width, desktop.Height);

        return new Size(desktop.Width / scale.Horizontal, desktop.Height / scale.Vertical);
    }

    private Rect ResizeAndCentreExistingWindow(Size size)
    {
        // Align window just like in macOS ...
        //
        // |10%|    80%    |10%|
        // |---|-----------|---|---
        // |TL |     T     |TR |10%
        // |---|-----------|---|---
        // |   |           |   |
        // |L  |     C     | R |80%
        // |   |           |   |
        // |---|-----------|---|---
        // |LB |     B     |RB |10%
        // |---|-----------|---|---

        // v5.4.1: the layout rule moved to Helpers/WindowPlacement (pure, unit tested) - the
        // window keeps the DIP size and only takes the location from it.
        var scale = DisplayDeviceHelper.GetScaleFactorFromWindow(this);
        var location = WindowPlacement.PlaceNearExisting(
            this.GetWindowRectInPixel(),
            size,
            scale,
            WindowHelper.GetDesktopRectFromWindowInPixel(this));

        // Return absolute location and relative size
        return new Rect(location, size);
    }

    private Rect ResizeAndCentreNewWindow(Size size)
    {
        var desktopRect = WindowHelper.GetCurrentDesktopRectInPixel();
        var scale = DisplayDeviceHelper.GetCurrentScaleFactor();
        var pxSize = new Size(scale.Horizontal * size.Width, scale.Vertical * size.Height);

        var pxLocation = new Point(
            desktopRect.X + (desktopRect.Width - pxSize.Width) / 2,
            desktopRect.Y + (desktopRect.Height - pxSize.Height) / 2);

        // Return absolute location and relative size
        return new Rect(pxLocation, size);
    }

    /// <summary>
    /// v3.31.0: handles <see cref="ContextObject.ApplyPreferredSizeNow"/> - a
    /// plugin that measured its content asks the window to follow the new
    /// <see cref="ContextObject.PreferredSize"/> immediately. Replaces the
    /// reflection call plugins used to make into this window.
    /// </summary>
    private void ApplyResizeRequest(Size size)
    {
        // A maximized window keeps its size (same rule as PositionWindow).
        if (!IsLoaded || WindowState == WindowState.Maximized)
            return;

        // v5.0.10 (#827): the plugin measured its content for the monitor the
        // preview was opened on, and this window is that window - keep it on
        // that screen instead of letting it grow past the edges.
        size = PreviewWindowSizing.ClampToDesktop(
            size, GetDesktopSizeInDip(new WindowInteropHelper(this).Handle));

        var newRect = ResizeAndCentreExistingWindow(size);

        // v5.0.4: this resize comes from the plugin (PDF asks for the measured page size),
        // not from the user. Without the flag the SizeChanged handler would store it as the
        // user's custom size, so every later preview would come up at whatever size the last
        // document happened to need.
        if (Math.Abs(newRect.Width - Width) > 0.5 || Math.Abs(newRect.Height - Height) > 0.5)
            _ignoreNextWindowSizeChange = true;

        this.MoveWindow(newRect.Left, newRect.Top, newRect.Width, newRect.Height);
    }

    /// <summary>
    /// v5.0.10 (QL-Win/QuickLook#827): Windows keeps a window's *physical* size
    /// when it moves to a monitor with a different scale factor, so the size in
    /// DIP changes on the way - and a preview that was comfortably sized on a
    /// 250% 4K panel becomes a window several monitors wide the moment the same
    /// physical size lands on a 100% 1080p screen. Whenever the DPI of this
    /// window changes, pull it back onto the monitor it is now on.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // v5.2.0: the one place that is guaranteed to hear about a scaling change (see
        // DisplayScale) - WebView2 hosts and their parked controls read it from here.
        DisplayScale.Notify(newDpi.PixelsPerDip);

        if (!IsLoaded || WindowState == WindowState.Maximized || _isFullscreen)
            return;

        // The size and position Win32 proposed arrive with this same message;
        // re-fit once the layout has settled on them.
        Dispatcher.BeginInvoke(new Action(RefitForCurrentScreen), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Which monitor the window was last seen on, so a move to another one can be told apart
    /// from the stream of <see cref="Window.LocationChanged"/> events a drag produces.
    /// </summary>
    private string _lastMonitorDevice;

    /// <summary>
    /// v5.2.0: the window may have been dragged to another monitor. Only that (or the very first
    /// report) is interesting - the event fires on every pixel of a drag.
    /// </summary>
    private void CheckForMonitorChange()
    {
        if (!IsLoaded || WindowState == WindowState.Maximized || _isFullscreen)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var device = WinForms.Screen.FromHandle(hwnd)?.DeviceName;
        if (string.IsNullOrEmpty(device))
            return;

        if (_lastMonitorDevice == null)
        {
            _lastMonitorDevice = device;
            return;
        }

        if (string.Equals(_lastMonitorDevice, device, StringComparison.OrdinalIgnoreCase))
            return;

        _lastMonitorDevice = device;
        RefitForCurrentScreen();
    }

    /// <summary>
    /// v5.2.0: the screen the window sits on changed - another monitor, another scale factor or
    /// another resolution. The size the plugin asked for was an answer about the *old* screen, so
    /// the question is asked again (see <see cref="ContextObject.RefitToHostDesktop"/>) and the
    /// window follows, clamped so it can never hang over the edge of the screen it is on.
    /// <para>
    /// v5.0.10: before that, this only pulled an oversized window back (the #827 "spans three
    /// monitors" case), which is still the last step here.
    /// </para>
    /// </summary>
    private void RefitForCurrentScreen()
    {
        if (!IsLoaded || WindowState == WindowState.Maximized || _isFullscreen)
            return;

        // The warm-up window is "shown" off-screen before the first preview; there is nothing to
        // re-fit for it.
        if (string.IsNullOrEmpty(_path))
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var desktop = GetDesktopSizeInDip(hwnd);

        // The plugin's fit measured the old screen; hand it the new one and ask again. A plugin
        // that set a fixed size returns false and is simply clamped below, and a size the user
        // dragged themselves wins over both (see ComputeWindowSize).
        ContextObject.HostDesktopSize = desktop;
        ContextObject.RefitToHostDesktop();

        var size = PreviewWindowSizing.ClampToDesktop(ComputeWindowSize(clampToDesktop: true), desktop);

        if (Math.Abs(size.Width - Width) <= 0.5d && Math.Abs(size.Height - Height) <= 0.5d)
            return;

        // Same rule as ApplyResizeRequest: this is the host correcting itself,
        // not the user choosing a size, so it must not be remembered as one.
        _ignoreNextWindowSizeChange = true;

        var newRect = ResizeAndCentreExistingWindow(size);

        this.MoveWindow(newRect.Left, newRect.Top, newRect.Width, newRect.Height);

        WriteWindowRectDiag(size, desktop);
    }

    /// <summary>
    /// v5.0.10 (#827): test hook - record where the preview window ended up
    /// after a placement, together with the monitor it was measured against.
    /// On a single-monitor machine these numbers must be exactly the ones from
    /// before the fix; on a mixed-DPI machine the window rect has to stay
    /// inside <c>monitorPx</c>. Enabled by the /test-preview-diag switch.
    /// </summary>
    private void WriteWindowRectDiag(Size size, Size desktop)
    {
        if (!App.IsPreviewDiagEnabled)
            return;

        try
        {
            var rect = this.GetWindowRectInPixel();
            var hwnd = new WindowInteropHelper(this).Handle;
            var monitor = hwnd == IntPtr.Zero
                ? new Rect()
                : WindowHelper.GetDesktopRectFromWindowInPixel(hwnd);

            Directory.CreateDirectory(App.SmokeDir);
            File.WriteAllText(Path.Combine(App.SmokeDir, "preview-rect.txt"),
                $"dip={size.Width:0.##}x{size.Height:0.##}{Environment.NewLine}" +
                $"px={rect.Width:0}x{rect.Height:0} at=({rect.Left:0},{rect.Top:0}){Environment.NewLine}" +
                $"monitorPx=({monitor.Left:0},{monitor.Top:0},{monitor.Width:0},{monitor.Height:0}){Environment.NewLine}" +
                $"desktopDip={desktop.Width:0.##}x{desktop.Height:0.##}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    internal void UnloadPlugin()
    {
        // The focused element will not processed by GC: https://stackoverflow.com/questions/30848939/memory-leak-due-to-window-efectivevalues-retention
        FocusManager.SetFocusedElement(this, null);
        Keyboard.DefaultRestoreFocusMode =
            RestoreFocusMode.None; // WPF will put the focused item into a "_restoreFocus" list ... omg
        Keyboard.ClearFocus();

        _canOldPluginResize = ContextObject.CanResize;

        // v1.2.14: keep the old content fully rendered until the next preview's
        // content takes over. Cleanup is deferred when this plugin's content is
        // on screen; a plugin that never got shown (superseded while loading)
        // can be disposed right away.
        if (_contentPlugin != null && ReferenceEquals(_contentPlugin, Plugin))
        {
            _pendingPluginCleanup = Plugin;
            _staleViewerContent = ContextObject.ViewerContent;
        }
        else
        {
            try
            {
                Plugin?.Cleanup();
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
        }

        ContextObject.Reset();

        if (_staleViewerContent != null)
            ContextObject.ViewerContent = _staleViewerContent;

        if (_autoReloadWatcher != null)
        {
            _autoReloadWatcher.EnableRaisingEvents = false;
            _autoReloadWatcher.Dispose();
            _autoReloadWatcher = null;
        }

        Plugin = null;

        _path = string.Empty;
    }

    internal void BeginShow(IViewer matchedPlugin, string path,
        Action<string, ExceptionDispatchInfo> exceptionHandler)
    {
        _path = path;
        Plugin = matchedPlugin;

        // v5.0.11: text recognition belongs to the toolbar now, and only the image
        // viewer can offer it - every other preview hides the button again.
        buttonOcr.Visibility = IsImagePreview() && !string.IsNullOrEmpty(path)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ContextObject.Reset();

        // v5.0.10 (#827): hand the plugin the monitor this preview is going to
        // be shown on, so that the size it asks for and the screen the window is
        // placed on are the same one - the mismatch between the two is what made
        // a large landscape image open several monitors wide on mixed-DPI setups.
        // A live lookup is used rather than a cached one: this runs on every
        // preview, right before the first placement and before Prepare below.
        ContextObject.HostDesktopSize = GetTargetDesktopSizeInDip();

        // v1.2.14: keep the previous content visible until the new one takes
        // over (avoids the blank gray window during switches); _staleViewerContent
        // is cleared by the ViewerContent change handler at the swap.
        if (_staleViewerContent != null)
            ContextObject.ViewerContent = _staleViewerContent;

        // Assign monitor color profile
        ContextObject.ColorProfileName = DisplayDeviceHelper.GetMonitorColorProfileFromWindow(this);

        // v1.2.27: show the window right away (before the potentially slow
        // Prepare) so previews feel instant. Prepare can still block briefly,
        // but the window is already on screen.
        // v1.2.36: after the off-screen warm-up the window is already
        // "visible"; still run the initial centering, but as a new-window
        // placement so it lands on screen centered, not on the warm-up spot.
        if (!IsVisible || _warmShown)
        {
            if (_customWindowSize != Size.Empty)
            {
                PositionWindow(_customWindowSize);
            }
            else
            {
                ContextObject.PreferredSize = new Size(800, 600);
                PositionWindow(ComputeWindowSize(clampToDesktop: true));
            }

            _warmShown = false;

            if (!IsVisible)
            {
                // v5.3.0: also honours the system's animation setting - see Helpers.Motion.
                if (!Helpers.Motion.WindowTransitionsEnabled)
                    this.ShowWithoutTransition();
                else
                    Show();
            }
        }

        // v3.0.1: raise the preview above other windows on every open or
        // switch. The 1.2.36 off-screen warm-up makes IsVisible true before
        // the first real preview, so the old `if (!IsVisible)` guard skipped
        // BringToFront and the preview could open behind other windows.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            this.BringToFront(Topmost);
            if (SettingHelper.Get("FocusWindowOnOpen", false))
                Activate();
        }), DispatcherPriority.Render);

        // Get the content size (and do any slow prepare work).
        try
        {
        Plugin.Prepare(path, ContextObject);
        }
        catch (Exception e)
        {
            exceptionHandler(path, ExceptionDispatchInfo.Capture(e));
            return;
        }

        if (ContextObject.IsBlocked)
        {
            ContextObject.ViewerContent = new System.Windows.Controls.TextBlock
            {
                Text = TranslationHelper.Get("MW_FileBlocked", failsafe: "This file type is blocked."),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
            };
            ContextObject.IsBusy = false;
            return;
        }

        // v3.22.0: the "Open / Open with" tooltip and share-button visibility
        // come from the shell-association lookup (AssocQueryString /
        // FileVersionInfo), which can take a few milliseconds on a cold
        // registry/disk cache. Defer it to Background priority so it never
        // delays the spinner / first content frame; the click handlers still
        // resolve the association themselves, so the buttons stay fully
        // usable while the hint updates.
        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && _path == path)
                SetOpenWithButtonAndPath();
        }, DispatcherPriority.Background);

        // Show the spinner only for plugins that did not opt out (images and
        // videos set ShowBusyIndicator=false in Prepare to avoid the spinner).
        ContextObject.ShowBusyIndicator &= _staleViewerContent == null;
        ContextObject.IsBusy = true;

        var newSize = ComputeWindowSize(clampToDesktop: true);
        if (_customWindowSize == Size.Empty)
            _ignoreNextWindowSizeChange = true;

        // v1.2.14: when switching to an image, keep the window (and the old
        // image) at its current size until the new first frame is decoded -
        // resizing early re-fits the old image into the new aspect ratio and
        // shows gray letterbox bands during the load.
        var deferResize = ContextObject.DeferResizeUntilReady && _staleViewerContent != null;
        if (!deferResize)
        {
            PositionWindow(newSize);
            ContextObject.DeferResizeUntilReady = false;
        }

        // v1.2.14: re-apply the backdrop so a change made from the tray menu
        // takes effect on the current preview (the setting is read here, not
        // cached at startup).
        ApplyWindowBackgroundEffects();

        // v1.3.5: hidden test hook (/test-preview-diag) - write the backdrop
        // render path so automated checks can assert acrylic is really on.
        if (App.IsPreviewDiagEnabled)
        {
            try
            {
                var diagDir = App.SmokeDir;
                Directory.CreateDirectory(diagDir);
                File.WriteAllText(Path.Combine(diagDir, "preview-backdrop.txt"), DiagnoseBackdrop());
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        if (_autoReload && File.Exists(path))
        {
            _autoReloadWatcher?.Dispose();
            _autoReloadWatcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _autoReloadWatcher.Changed += (_, _) =>
                // Executed asynchronously to avoid deadlock
                Dispatcher.BeginInvoke(() => ViewWindowManager.GetInstance().ReloadPreview());
            _autoReloadWatcher.EnableRaisingEvents = true;
        }

        // Load plugin, do not block UI
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                // Avoid main thread scheduling lag
                if (Plugin is null) return;

                Plugin.View(path, ContextObject);

                // Initialize the more menu
                ClearMoreMenuEntries();
                var pluginManager = PluginManager.GetInstance();
                pluginManager.EnsureLoaded();
                foreach (var plugin in
                    pluginManager.LoadedPlugins
                        .GroupBy(x => x.ToString()).Select(g => g.First()) // DistinctBy plugin name
                        .OrderBy(p => p.Priority)) // OrderBy plugin priority
                {
                    if (plugin.ToString() == Plugin.ToString())
                    {
                        if (Plugin is IMoreMenu moreMenu && moreMenu.MenuItems is not null)
                        {
                            AddPluginMoreMenu(moreMenu.MenuItems);
                        }
                        continue;
                    }
                    else
                    {
                        if (plugin is IMoreMenuExtended moreMenu && moreMenu.MenuItems is not null)
                        {
                            AddPluginMoreMenu(moreMenu.MenuItems);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                exceptionHandler(path, ExceptionDispatchInfo.Capture(e));
            }
        }, DispatcherPriority.Input);
    }

    private Size ComputeWindowSize(bool clampToDesktop = false)
    {
        var newHeight = ContextObject.PreferredSize.Height + BorderThickness.Top + BorderThickness.Bottom +
                        (ContextObject.TitlebarOverlap ? 0 : windowCaptionContainer.Height);
        var newWidth = ContextObject.PreferredSize.Width + BorderThickness.Left + BorderThickness.Right;

        var newSize = new Size(newWidth, newHeight);

        // If the user has adjusted the window size, keep it
        // v5.0.4: ...but only for previews that can be resized at all - the audio panel and
        // the info panel ask for a fixed size (CanResize = false) and must not inherit a
        // remembered 1920x1080.
        if (_customWindowSize != Size.Empty && ContextObject.CanResize)
            return _customWindowSize;

        // v5.0.10 (#827): last guard between a plugin's desired size and the
        // screen. A plugin may ask for a fixed size, or ask at a moment when the
        // monitor under the cursor has already changed; whatever the reason,
        // the window must never be larger than the monitor it is placed on -
        // that is exactly how a large landscape image ended up stretching
        // across three monitors. A size the user dragged themselves is left
        // alone (returned above): that is their choice, not a sizing error.
        return clampToDesktop
            ? PreviewWindowSizing.ClampToDesktop(newSize, DesktopSizeForNextPlacement())
            : newSize;
    }

    private void ClearMoreMenuEntries()
    {
        _pluginMoreMenuEntries.Clear();
    }

    private void AddPluginMoreMenu(IEnumerable<IMenuItem> moreMenu)
    {
        foreach (IMenuItem item in moreMenu)
        {
            if (item is null) continue;

            if (item.IsSeparator)
            {
                _pluginMoreMenuEntries.Add(TrayMenuEntry.Separator);
                continue;
            }

            if (!item.IsVisible)
                continue;

            var command = item.Command;
            var parameter = item.CommandParameter;

            _pluginMoreMenuEntries.Add(new TrayMenuEntry
            {
                Header = item.Header?.ToString(),
                Icon = ResolveMenuIcon(item.Icon),
                IsEnabled = item.IsEnabled,
                ToolTip = item.ToolTip,
                Command = () =>
                {
                    if (command?.CanExecute(parameter) == true)
                        command.Execute(parameter);
                },
            });
        }
    }

    private static object ResolveMenuIcon(object icon)
    {
        return icon is string or ImageSource or FrameworkElement ? icon : null;
    }

    private List<TrayMenuEntry> BuildMoreMenuEntries()
    {
        var entries = new List<TrayMenuEntry>
        {
            new()
            {
                Header = TranslationHelper.Get("MW_Reload"),
                Icon = FontSymbols.Refresh,
                Command = () => ViewWindowManager.GetInstance().ReloadPreview(),
            },
            new()
            {
                Header = TranslationHelper.Get("InfoPanelMoreItem_CopyAsPath"),
                Icon = FontSymbols.Copy,
                Command = CopyPathToClipboard,
            },
            // v5.0.4: the custom size is remembered now, so there has to be a way back to
            // the size the current plugin asks for.
            new()
            {
                Header = TranslationHelper.Get("MW_ResetWindowSize", failsafe: "Reset window size"),
                Icon = FontSymbols.FitPage,
                Command = ResetWindowSize,
            },
        };

        if (_pluginMoreMenuEntries.Count > 0)
        {
            entries.Add(TrayMenuEntry.Separator);
            entries.AddRange(_pluginMoreMenuEntries);
        }

        return entries;
    }

    /// <summary>
    /// v5.0.8: whether the image viewer produced the current preview. It is the only plugin that
    /// can offer text recognition, which is implemented in the app because the image plugin does
    /// not reference the WinRT projection the OCR API lives in (see <see cref="OcrRecognizer"/>).
    /// <para>
    /// v5.0.11: this now drives the toolbar button instead of a "More" menu entry - the feature
    /// was two clicks deep in a submenu and users did not find it.
    /// </para>
    /// </summary>
    private bool IsImagePreview()
        => Plugin?.GetType().Assembly.GetName().Name
               ?.Equals("QuickLook.Plugin.ImageViewer", StringComparison.OrdinalIgnoreCase) == true;

    private void ExtractText()
    {
        if (!string.IsNullOrEmpty(_path))
            OcrWindow.ShowWindow(_path);
    }

    /// <summary>
    /// v5.0.4: drops the remembered custom size and goes back to the size the current plugin
    /// asks for (the More menu's "Reset window size" entry). The programmatic resize is
    /// flagged so it does not become the user's new custom size.
    /// </summary>
    private void ResetWindowSize()
    {
        _customWindowSize = Size.Empty;

        var target = ComputeWindowSize(clampToDesktop: true);
        if (Math.Abs(target.Width - Width) > 0.5 || Math.Abs(target.Height - Height) > 0.5)
            _ignoreNextWindowSizeChange = true;

        PositionWindow(target);
    }

    private void CopyPathToClipboard()
    {
        try
        {
            Clipboard.SetText($"\"{(_path.Length >= 260 ? @"\\?\" + _path : _path)}\"");
            Toast.Success(TranslationHelper.Get("InfoPanelMoreItem_CopySucc"));
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    private void ToggleMoreMenu()
    {
        if (TrayMenuWindow.IsOpen)
        {
            TrayMenuWindow.CloseCurrentMenu();
            return;
        }

        ShowMoreMenu();
    }

    private void ShowMoreMenu()
    {
        TrayMenuWindow.ShowMenu(BuildMoreMenuEntries(), CurrentTheme == Themes.Dark, buttonMore);
    }

    internal void ShowMoreMenuForTest()
    {
        TrayMenuWindow.ShowMenu(BuildMoreMenuEntries(), CurrentTheme == Themes.Dark, buttonMore, autoCloseMs: 3000);
    }

    private void SetOpenWithButtonAndPath()
    {
        // Share icon
        buttonShare.Visibility = ShareHelper.IsShareSupported(_path) ? Visibility.Visible : Visibility.Collapsed;

        // Open icon
        if (Directory.Exists(_path))
        {
            buttonOpen.ToolTip = string.Format(TranslationHelper.Get("MW_BrowseFolder"), Path.GetFileName(_path));
            return;
        }

        var isExe = FileHelper.IsExecutable(_path, out var appFriendlyName);
        if (isExe)
        {
            buttonOpen.ToolTip = string.Format(TranslationHelper.Get("MW_Run"), appFriendlyName);
            return;
        }

        // Not an exe
        var found = FileHelper.GetAssocApplication(_path, out appFriendlyName);
        if (found)
        {
            buttonOpen.ToolTip = string.Format(TranslationHelper.Get("MW_OpenWith"), appFriendlyName);
            return;
        }

        // Assoc not found
        buttonOpen.ToolTip = string.Format(TranslationHelper.Get("MW_Open"), Path.GetFileName(_path));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        UnloadPlugin();

        // v1.2.14: no new preview will take over, so dispose the deferred
        // plugin now (its content was kept on screen until the window closes).
        if (_pendingPluginCleanup != null)
        {
            try
            {
                _pendingPluginCleanup.Cleanup();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

        _pendingPluginCleanup = null;
            _staleViewerContent = null;
        }

        base.OnClosing(e);

        if (App.IsMemoryDiagnosticsEnabled)
            Helpers.MemoryDiagnostics.Snapshot("preview-closed");

        ProcessHelper.PerformAggressiveGC();
    }
}
