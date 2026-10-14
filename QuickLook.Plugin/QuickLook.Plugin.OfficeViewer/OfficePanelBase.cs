// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WpfColor = System.Windows.Media.Color;

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v3.16.0/v3.17.0: shared surface for the self-rendered Office previews.
/// The content always sits on a plain white paper-like surface (never
/// theme-adapted) so real documents stay readable in dark mode too.
/// <para>
/// v5.6.19: the surface is two columns now - a rail on the left for the structure of the file (Word's
/// outline, PowerPoint's slides, Excel's worksheets) and the page on the right, which is how the PDF panel
/// has always looked. Selecting a rail row either scrolls the page to an anchor or asks the panel to render
/// something else (a worksheet, a window of rows).
/// </para>
/// </summary>
public abstract class OfficePanelBase : UserControl, IDisposable
{
    /// <summary>
    /// The host name the preview's own pictures are served under (see OfficeImageStore); the converters put
    /// it in front of every image they reference.
    /// </summary>
    protected const string ImageHost = "office-preview.local";

    private readonly Border _paper;
    private readonly Border _railHost;
    private readonly ListBox _rail;
    private readonly WebView2 _webView;
    private EventHandler<CoreWebView2WebMessageReceivedEventArgs> _messageHandler;
    private string _imageFolder;
    private bool _suppressRail;
    private bool _disposed;

    protected OfficePanelBase()
    {
        // v3.26.0: the panel used to be opaque white from the moment it was
        // created, so switching to an Office file flashed a big white box
        // before the page loaded (especially jarring in dark mode). Match the
        // loading surface to the app theme; the white "paper" only appears
        // together with the rendered content. Stays opaque, so the v3.17.0
        // "transparent WebView2 composites to black" issue cannot reappear.
        var tint = IsDarkTheme()
            ? WpfColor.FromRgb(0x17, 0x17, 0x17)
            : WpfColor.FromRgb(0xF2, 0xF2, 0xF2);

        // v3.34.0: take a warm control from the pool instead of creating a new
        // Chromium controller for every document (~300-400 ms each).
        _webView = WebView2ControlPool.Acquire();
        // The control background only shows before the page paints; the page
        // itself is an opaque white paper surface (v3.17.0).
        _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
            tint.A, tint.R, tint.G, tint.B);
        _paper = new Border
        {
            Background = new SolidColorBrush(tint),
            Child = _webView,
        };

        _rail = BuildRail();
        _railHost = BuildRailHost(_rail);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_railHost, 0);
        Grid.SetColumn(_paper, 1);
        layout.Children.Add(_railHost);
        layout.Children.Add(_paper);
        Content = layout;

        // v3.29.0: track the control so the idle recycler can shut the
        // Chromium process group down after the last Office preview closes.
        WebView2Lifecycle.Register(_webView);
    }

    /// <summary>What the rail currently shows, so a selection can be acted on.</summary>
    protected IReadOnlyList<OfficeNavEntry> Navigation { get; private set; } = [];

    private static bool IsDarkTheme()
    {
        var theme = (Themes)SettingHelper.Get("LastTheme", (int)Themes.None, "QuickLookNext");
        return theme switch
        {
            Themes.Dark => true,
            Themes.Light => false,
            _ => OSThemeHelper.AppsUseDarkTheme(),
        };
    }

    /// <summary>
    /// v5.6.19: the left rail. Rows are indented by their level so a Word outline reads as a hierarchy; the
    /// colours are fixed (light) like the paper surface next to them.
    /// </summary>
    private ListBox BuildRail()
    {
        var rail = new ListBox
        {
            // The background lives on the host border: the app's theme dictionaries give ListBox a
            // transparent background of their own, which is not what a paper-like rail wants.
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 6, 0, 6),
            Focusable = false,
        };

        ScrollViewer.SetHorizontalScrollBarVisibility(rail, ScrollBarVisibility.Disabled);
        rail.ItemContainerStyle = RailItemStyle();

        rail.SelectionChanged += (_, _) =>
        {
            if (!_suppressRail && rail.SelectedIndex >= 0)
                OnNavigationSelected(rail.SelectedIndex);
        };

        return rail;
    }

    private static Border BuildRailHost(ListBox rail) => new()
    {
        Width = 208,
        Background = new SolidColorBrush(WpfColor.FromRgb(0xF7, 0xF7, 0xF7)),
        BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xE3, 0xE3, 0xE3)),
        BorderThickness = new Thickness(0, 0, 1, 0),
        Child = rail,
    };

    /// <summary>
    /// v5.6.19: a plain, light row. The rail sits next to the white page, so it does not follow the app
    /// theme - and it deliberately keeps the stock item visuals: a custom template here turned out to be the
    /// one thing between the outline and the reader.
    /// </summary>
    private static Style RailItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            new SolidColorBrush(WpfColor.FromRgb(0x1A, 0x1A, 0x1A))));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 10, 5)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.5d));
        return style;
    }

    /// <summary>
    /// v5.6.19: a rail row was picked. The default scrolls the page to the row's anchor, which is what Word's
    /// outline and PowerPoint's slide list want; the spreadsheet panel overrides it to switch worksheets.
    /// </summary>
    protected virtual void OnNavigationSelected(int index)
    {
        var entry = index >= 0 && index < Navigation.Count ? Navigation[index] : null;
        if (entry is null || string.IsNullOrEmpty(entry.Target))
            return;

        RunScript(
            $"document.getElementById('{entry.Target}')?.scrollIntoView({{behavior:'smooth',block:'start'}})");
    }

    /// <summary>v5.6.19: a message from the page itself (the spreadsheet's row pager).</summary>
    protected virtual void OnWebMessage(string message)
    {
    }

    /// <summary>v5.6.19: runs JavaScript in the page, ignoring failures (the surface is decoration).</summary>
    protected void RunScript(string script)
    {
        if (_disposed || string.IsNullOrEmpty(script))
            return;

        var core = _webView?.CoreWebView2;
        if (core is null)
            return;

        static void Execute(CoreWebView2 core, string script)
        {
            _ = core.ExecuteScriptAsync(script).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Debug.WriteLine(t.Exception?.GetBaseException().Message);
            }, TaskScheduler.Default);
        }

        if (Dispatcher.CheckAccess())
            Execute(core, script);
        else
            Dispatcher.BeginInvoke(() => Execute(core, script));
    }

    /// <summary>
    /// v3.32.0: only navigate when the controller really came up - a faulted
    /// EnsureCoreWebView2Async (WebView2 runtime missing or being restarted)
    /// used to surface as an unhandled "CoreWebView2 prior to being
    /// initialized" exception. Chromium also fails transiently with
    /// 0x8007139F right after a controller was torn down, so one retry is
    /// enough to keep the preview working. The CoreWebView2 check runs on the
    /// UI thread because the WebView2 control is a DispatcherObject.
    /// </summary>
    protected void Navigate(string html)
    {
        _ = NavigateWhenReadyAsync(html);
    }

    private async Task NavigateWhenReadyAsync(string html)
    {
        const int attempts = 3;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await _webView.EnsureCoreWebView2Async();
            }
            catch (Exception e)
            {
                ProcessHelper.WriteLog(
                    $"[OfficePanel] CoreWebView2 init failed (attempt {attempt}/{attempts}): {e.GetBaseException().Message}");

                if (attempt == attempts)
                {
                    ShowInitializationFailure();
                    return;
                }

                // v3.36.0: restart our Chromium process group before retrying.
                // v3.40.0: repairs the profile first, rebuilds it in place second.
                WebView2Lifecycle.RecoverFromFailedInitialization(attempt);
                await Task.Delay(attempt == 1 ? 300 : 800);
                continue;
            }

            if (_disposed)
                return;

            // Touching CoreWebView2 requires the UI thread.
            await Dispatcher.InvokeAsync(() =>
            {
                if (_disposed || _webView.CoreWebView2 == null)
                    return;

                // v5.6.19: the pictures this page references live in a temp folder; the page asks for them
                // by https://office-preview.local/... rather than carrying them as base64.
                if (!string.IsNullOrEmpty(_imageFolder))
                {
                    try
                    {
                        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                            ImageHost, _imageFolder, CoreWebView2HostResourceAccessKind.DenyCors);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e.Message);
                    }
                }

                if (_messageHandler is null)
                {
                    _messageHandler = (_, e) =>
                    {
                        try
                        {
                            OnWebMessage(e.TryGetWebMessageAsString());
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex.Message);
                        }
                    };
                    _webView.CoreWebView2.WebMessageReceived += _messageHandler;
                }

                _webView.NavigateToString(html);
            });

            return;
        }
    }

    /// <summary>
    /// v3.36.0: a readable message instead of an empty sheet when WebView2 could not
    /// be initialized at all.
    /// </summary>
    private void ShowInitializationFailure()
    {
        if (_disposed)
            return;

        try
        {
            Content = new TextBlock
            {
                Text = TranslationHelper.Get("WEBVIEW2_INIT_FAILED",
                    failsafe: "The WebView2 component failed to initialize.\nRestart the app; if that does not help, delete UserData\\WebView2_Data in the program folder and try again.",
                    domain: System.Reflection.Assembly.GetExecutingAssembly().GetName().Name),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24),
            };
        }
        catch
        {
            // Never let the fallback throw.
        }
    }

    /// <summary>
    /// v3.23.0: builds the page on a background thread (OOXML parsing can take
    /// hundreds of ms on large documents) and navigates back on the UI thread
    /// when ready, so the preview window stays responsive while the document
    /// parses.
    /// <para>
    /// v5.6.19: the task also produces the structure for the left rail, which is why the result is a
    /// <see cref="OfficePanelContent"/> rather than a bare string.
    /// </para>
    /// </summary>
    protected void NavigateAsync(Func<OfficePanelContent> build)
    {
        var dispatcher = Dispatcher;

        _ = Task.Run(build).ContinueWith(t =>
        {
            dispatcher.BeginInvoke(() =>
            {
                if (_disposed)
                    return;

                if (t.IsFaulted || t.Result is null)
                {
                    Navigate(ErrorHtml(t.Exception));
                    return;
                }

                ApplyContent(t.Result);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// v5.6.19: puts the rail and the page in place. The picture folder from the previous page is dropped
    /// here - each preview owns its folder for as long as it is on screen.
    /// </summary>
    private void ApplyContent(OfficePanelContent content)
    {
        var previousFolder = _imageFolder;
        _imageFolder = content.ImageFolder;
        if (!string.IsNullOrEmpty(previousFolder) && previousFolder != _imageFolder)
            OfficeImageStore.Cleanup(previousFolder);

        Navigation = content.Navigation ?? [];

        _suppressRail = true;
        try
        {
            _rail.Items.Clear();
            foreach (var entry in Navigation)
            {
                _rail.Items.Add(new TextBlock
                {
                    Text = entry.Label,
                    // Set on the row itself: the app's theme dictionaries give list items a light foreground
                    // that is meant for a dark surface, and this rail is light.
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(0x1A, 0x1A, 0x1A)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(entry.Level * 12, 0, 0, 0),
                });
            }

            _railHost.Visibility = Navigation.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var selected = Math.Clamp(content.SelectedIndex, 0, Math.Max(0, Navigation.Count - 1));
            _rail.SelectedIndex = Navigation.Count > 0 ? selected : -1;
        }
        finally
        {
            _suppressRail = false;
        }

        Navigate(content.Html);
    }

    public void Dispose()
    {
        _disposed = true;

        var control = _webView;
        var folder = _imageFolder;
        _imageFolder = null;

        if (!string.IsNullOrEmpty(folder))
            OfficeImageStore.Cleanup(folder);
        if (control?.CoreWebView2 is not null && _messageHandler is not null)
        {
            try
            {
                control.CoreWebView2.WebMessageReceived -= _messageHandler;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
            }
        }

        _messageHandler = null;

        if (_paper.Child != null && ReferenceEquals(_paper.Child, control))
            _paper.Child = null;

        if (control == null)
            return;

        // v3.29.0/v3.34.0: stop tracking so the idle recycler counts this control
        // as gone, then hand it back to the pool.
        WebView2Lifecycle.Unregister(control);
        WebView2ControlPool.Release(control);
    }

    private static string ErrorHtml(Exception error)
    {
        return
            """
            <!DOCTYPE html><html><head><meta charset="utf-8">
            <style>body{margin:24px;font-family:'Segoe UI',sans-serif;font-size:14px;color:#C42B1C}</style>
            </head><body><div>This document cannot be read (the file may be corrupt or in an unsupported format).</div></body></html>
            """ + (error is null ? string.Empty : $"<!-- {error.Message} -->");
    }
}
