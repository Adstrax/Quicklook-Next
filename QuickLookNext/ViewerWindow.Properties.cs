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

using QuickLook.Common.Annotations;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Wpf.Ui.Appearance;
using Wpf.Ui.Violeta.Appearance;

namespace QuickLookNext;

public partial class ViewerWindow : INotifyPropertyChanged
{
    // v3.11.0: the content fade-in runs once for the first preview only.
    // Applying it on every preview switch made the content dip in opacity and
    // read as a flicker.
    private bool _firstContentFadePending = true;

    private readonly ResourceDictionary _darkDict = new()
    {
        Source = new Uri("pack://application:,,,/QuickLook.Common;component/Styles/MainWindowStyles.Dark.xaml")
    };

    private bool _canOldPluginResize;
    private bool _pinned;
    private bool _isFullscreen;
    private WindowState _preFullscreenWindowState;
    private WindowStyle _preFullscreenWindowStyle;
    private ResizeMode _preFullscreenResizeMode;
    private Rect _preFullscreenBounds;
    private double _preFullscreenCaptionHeight;
    private Thickness _preFullscreenResizeBorderThickness;

    public bool Pinned
    {
        get => _pinned;
        set
        {
            _pinned = value;
            buttonPin.Tag = value ? "Pin" : "Auto";
            OnPropertyChanged();
        }
    }

    public IViewer Plugin { get; private set; }

    public ContextObject ContextObject { get; private set; }

    public Themes CurrentTheme { get; private set; }

    // Hidden test hook state (/test-timing): the path that was busy, so the
    // timing entry is only written on a real busy -> ready transition (the
    // ContextObject.Reset() calls also toggle IsBusy and must be ignored).
    private string _timingBusyPath;

    // The previous preview's content, kept on screen while the next preview
    // loads so switching does not flash an empty gray window (v1.2.13).
    private object _staleViewerContent;

    // v1.2.14: the plugin whose content is currently displayed. Its Cleanup is
    // deferred until the next preview's content takes over, so the old content
    // stays fully rendered instead of being emptied mid-switch.
    private IViewer _pendingPluginCleanup;
    private IViewer _contentPlugin;

    public ICommand CloseCommand { get; private set; }

    public event PropertyChangedEventHandler PropertyChanged;

    [NotifyPropertyChangedInvocator]
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void ContextObject_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ContextObject.IsBusy):
                if (ContextObject.IsBusy)
                {
                    _timingBusyPath = _path;
                    // v1.2.14: the spinner only shows for the initial load of a
                    // preview; during switches the previous content stays on
                    // screen so there is nothing to hide behind it.
                    if (CheckAccess())
                        busyIndicatorLayer.Visibility = ContextObject.ShowBusyIndicator
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    break;
                }

                if (CheckAccess())
                    busyIndicatorLayer.Visibility = Visibility.Collapsed;

                // v1.2.14: apply content that was held back until the first
                // frame decoded (image plugin) - the swap is atomic, so the old
                // content stays on screen until the new one is ready.
                if (ContextObject.PendingViewerContent != null &&
                    !ReferenceEquals(ContextObject.ViewerContent, ContextObject.PendingViewerContent))
                {
                    ContextObject.ViewerContent = ContextObject.PendingViewerContent;
                    ContextObject.PendingViewerContent = null;
                }

                // v1.2.14: the window kept the previous preview's size while
                // the new image decoded; resize it now that the content is
                // ready so no gray letterbox bands are visible during loading.
                if (ContextObject.DeferResizeUntilReady)
                {
                    ContextObject.DeferResizeUntilReady = false;
                    PositionWindow(ComputeWindowSize(clampToDesktop: true));
                }

                // v1.2.14: force the layout synchronously so the just-swapped
                // content renders in the same composition frame - without this
                // the window can show one gray frame before the new panel is
                // measured and drawn. Only on the UI thread: some plugins set
                // IsBusy=false from a background thread.
                if (ContextObject.ViewerContent != null && CheckAccess())
                {
                    UpdateLayout();

                    // v3.9.0/v3.11.0: fade the first preview's content in
                    // softly; preview switches stay at full opacity so they
                    // never flicker. Respects the ShowWindowTransition option.
                    if (_firstContentFadePending &&
                        Helpers.Motion.WindowTransitionsEnabled)
                    {
                        _firstContentFadePending = false;
                        var fade = (Storyboard)FindResource("ContentFadeInStoryboard");
                        fade.Begin(container);
                    }
                }

                // Hidden test hook: record when content actually becomes ready
                // (spinner dismissed) so benches can measure preview latency.
                if (App.IsTimingEnabled && IsVisible && _timingBusyPath != null
                    && _timingBusyPath == _path && !string.IsNullOrEmpty(_path))
                {
                    try
                    {
                        var dir = App.SmokeDir;
                        System.IO.Directory.CreateDirectory(dir);
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(dir, "timing.txt"),
                            $"{DateTime.UtcNow:o}|{_path}{Environment.NewLine}");
                    }
                    catch
                    {
                        // The hook is for measurement only; never break previews.
                    }

                    _timingBusyPath = null;
                }
                break;

            case nameof(ContextObject.ViewerContent):
                // v1.2.14: when real content takes over (the stale restore and
                // the Reset null are ignored here), dispose the old plugin and
                // track the new one as the current content.
                if (ContextObject.ViewerContent == null ||
                    ReferenceEquals(ContextObject.ViewerContent, _staleViewerContent))
                {
                    break;
                }

                var oldPlugin = _pendingPluginCleanup;
                _pendingPluginCleanup = null;
                _staleViewerContent = null;
                _contentPlugin = Plugin;

                if (oldPlugin != null)
                {
                    try
                    {
                        oldPlugin.Cleanup();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                }
                break;

            case nameof(ContextObject.Theme):
                SwitchTheme(ContextObject.Theme);
                break;

            case nameof(ContextObject.Title):
                // v5.3.0: the caption renders the title itself (see UpdateCaptionTitle) so the
                // "6000x4000:" prefix can be secondary while the file name stays primary.
                UpdateCaptionTitle(ContextObject.Title);

                Dispatcher?.Invoke(() =>
                {
                    // We can not update the Title when ShowInTaskbar is false
                    // https://github.com/QL-Win/QuickLook/issues/1628
                    // v3.1.0: reset the title when a plugin (e.g. PEViewer)
                    // provides an empty title, so switching between previews
                    // never leaves the previous file's title behind.
                    Title = string.IsNullOrWhiteSpace(ContextObject.Title)
                        ? "QuickLook-Next"
                        : $"QuickLook-Next - {ContextObject.Title}";
                });
                break;

            default:
                break;
        }
    }

    public void SwitchTheme(Themes theme)
    {
        var isDark = false;

        switch (theme)
        {
            case Themes.None:
                isDark = OSThemeHelper.AppsUseDarkTheme();
                break;

            case Themes.Dark:
            case Themes.Light:
                isDark = theme == Themes.Dark;
                break;
        }

        if (isDark)
        {
            CurrentTheme = Themes.Dark;
            AppThemeState.IsDark = true;

            // Update theme for QuickLookNext controls
            if (!Resources.MergedDictionaries.Contains(_darkDict))
                Resources.MergedDictionaries.Add(_darkDict);

            // Update theme for WPF-UI controls
            ThemeManager.Apply(ApplicationTheme.Dark);
        }
        else
        {
            CurrentTheme = Themes.Light;
            AppThemeState.IsDark = false;

            // Update theme for QuickLookNext controls
            if (Resources.MergedDictionaries.Contains(_darkDict))
                Resources.MergedDictionaries.Remove(_darkDict);

            // Update theme for WPF-UI controls
            ThemeManager.Apply(ApplicationTheme.Light);
        }

        // Theme button: sun = switch to light, moon = switch to dark.
        buttonTheme.Content = isDark ? "\uE706" : "\uE708";

        // v5.3.0: the caption title's secondary run holds a brush from the theme that was active
        // when it was created - rebuild it against the theme that is active now.
        UpdateCaptionTitle(ContextObject.Title);

        if (IsLoaded)
            ApplyWindowBackgroundEffects();
    }
}
