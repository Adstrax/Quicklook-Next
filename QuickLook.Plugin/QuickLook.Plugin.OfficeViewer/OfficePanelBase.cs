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
/// v5.7.0: the surface is two columns now - a rail on the left for the structure of the file (Word's
/// outline, PowerPoint's slides, Excel's worksheets) and the page on the right, which is how the PDF panel
/// has always looked. Selecting a rail row either scrolls the page to an anchor or asks the panel to render
/// something else (a worksheet, a window of rows).
/// </para>
/// </summary>
public abstract class OfficePanelBase : UserControl, IDisposable
{
    /// <summary>
    /// v5.7.0: how wide the rail is. A preview whose page is portrait (Word) sizes its window around this,
    /// because the rail does not scale with the window while the page beside it does.
    /// </summary>
    internal const double RailWidth = 208;

    /// <summary>
    /// The host name the preview's own pictures are served under (see OfficeImageStore); the converters put
    /// it in front of every image they reference.
    /// </summary>
    protected const string ImageHost = "office-preview.local";

    private readonly Border _paper;
    private readonly Border _railHost;
    private readonly ListBox _rail;
    private readonly WebView2 _webView;
    private readonly System.Drawing.Color _pageColour;
    private readonly string _pageHex;
    private EventHandler<CoreWebView2WebMessageReceivedEventArgs> _messageHandler;
    private string _imageFolder;
    private bool _suppressRail;
    private bool _quiesced;
    private bool _disposed;
    private bool _reportedRenderFailure;

    /// <summary>
    /// v5.7.1: <paramref name="surface"/> is the colour of the page this panel is about to show - the sheet
    /// of paper for Word and Excel, the desk a slide sits on for PowerPoint. The border behind the browser is
    /// painted with it, so the area reads as the page before the browser's own surface has anything to draw.
    /// <para>
    /// v5.7.1: this used to be the app theme's tint (#171717 in dark mode), chosen in v3.26.0 to keep a white
    /// flash out of a dark window. Measured on a repeat preview of 2024论文-V2.docx (warm control, no cold
    /// start left to pay), the content area read 100 % dark - RGB 23,23,23 and then 18,18,18, frames sampled
    /// at 120 ms - between the rail appearing and the page arriving. The rail appears as soon as the document
    /// is parsed, so that band is exactly what a reader sees in the meantime, and it repeats on every
    /// preview, not only the first. A dark placeholder in front of a white page reads as a black hole, and
    /// the page's own colour does not.
    /// </para>
    /// </summary>
    protected OfficePanelBase(WpfColor? surface = null)
    {
        // Opaque, so the v3.17.0 "transparent WebView2 composites to black" issue cannot reappear.
        var page = surface ?? WpfColor.FromRgb(0xFF, 0xFF, 0xFF);
        _pageColour = System.Drawing.Color.FromArgb(page.A, page.R, page.G, page.B);
        _pageHex = $"#{page.R:X2}{page.G:X2}{page.B:X2}";

        // v3.34.0: take a warm control from the pool instead of creating a new
        // Chromium controller for every document (~300-400 ms each).
        _webView = WebView2ControlPool.Acquire();
        // v5.7.1: the page's colour, which is the colour the control is asked to use for the area its page
        // will fill. Measured, this property does not actually reach the surface the browser shows while it
        // has no frame - set to pure red, the frames of that window stayed RGB 18,18,18 with not one red
        // pixel - which is why the panel also lays a sheet and navigates to a document instead of handing
        // the page over as a string (see LaySheetAsync and NavigatePage).
        _webView.DefaultBackgroundColor = _pageColour;

        _paper = new Border
        {
            Background = new SolidColorBrush(page),
            Child = _webView,
        };

        _rail = BuildRail();
        _railHost = BuildRailHost(_rail);

        // v5.7.0: the rail runs the full height of the window on purpose. The caption bar fades in over its
        // top, so the rail's first row can be read but not clicked while the bar is showing - reserving the
        // caption's own row above the rail (a 32 DIP margin, as the pages of this preview used to) fixes the
        // click and breaks the look, because the rail then starts below the sheet beside it. Measured both
        // ways on 2024论文-V2.docx; the trade the preview ships with is the look.
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

        // v5.7.1: the controller is brought up here, while the document is still being parsed, instead of
        // waiting for the page to be ready. The two used to be strictly serial: on 2024论文-V2.docx the parse
        // took 195 ms and only then did EnsureCoreWebView2Async cost another 287 ms, with an idle UI thread
        // the whole time. The rail appears the moment the parse is done, so those 287 ms were exactly the
        // black gap between the rail and the page - starting the controller at construction puts that work
        // behind the parse instead of after it, and the page then has nothing to wait for but its own load.
        // The call waits for the control to be loaded because a WebView2 cannot create its browser before it
        // has a window; if this panel is already loaded, the head start begins now.
        if (IsLoaded)
            WarmUpCore();
        else
            Loaded += (_, _) => WarmUpCore();
    }

    /// <summary>
    /// An optional last-resort renderer supplied by the owning plugin. A
    /// presentation uses it when the self-rendered page cannot be parsed or
    /// when WebView2 itself cannot start.
    /// </summary>
    internal Action<Exception> RenderFailureFallback { get; set; }

    /// <summary>
    /// v5.7.1: brings the Chromium controller up ahead of the page. This is only a head start - a failure is
    /// left alone, because <see cref="NavigateWhenReadyAsync"/> owns the retry and the fallback message, and
    /// by the time it runs the controller is normally already there.
    /// </summary>
    private async void WarmUpCore()
    {
        try
        {
            await WebView2ControlPool.EnsureInitializedAsync(_webView);
        }
        catch (Exception e)
        {
            // Never let the head start become the failure; the navigation path reports what went wrong.
            Debug.WriteLine(e.Message);
        }
    }

    /// <summary>
    /// v5.7.1: gives the browser a frame of its own before the document's page is asked for - an empty sheet
    /// in the page's own colour - and returns when that frame is up.
    /// <para>
    /// This is what the black band was. A control taken from the pool still shows the page it was parked on,
    /// and the pool parks its controls on <c>about:blank</c> with a transparent background, so that page is
    /// the window's own backdrop showing through - measured on a repeat preview, RGB 18,18,18 for the 500 ms
    /// between the browser's surface coming up and the document's page painting. Everything tried against the
    /// surface itself failed for one reason or another: a WPF layer above the browser cannot paint over it (a
    /// WebView2 is an HWND of its own - the covered frames still read 18,18,18), <c>DefaultBackgroundColor</c>
    /// is the colour Chromium paints a document with rather than what an empty surface shows, and keeping the
    /// control hidden until its page arrived stopped it loading at all (the sheet stayed blank).
    /// <para>
    /// Small enough to load in a frame or two. Chromium holds the frame it already has while the next
    /// navigation loads - the same reason a browser does not flash white between pages - so the document's
    /// page then replaces this sheet without the empty surface ever being on screen.
    /// </para>
    /// </summary>
    private async Task LaySheetAsync()
    {
        var painted = new TaskCompletionSource<bool>();

        void OnCompleted(object _, CoreWebView2NavigationCompletedEventArgs __) => painted.TrySetResult(true);

        try
        {
            _webView.CoreWebView2.NavigationCompleted += OnCompleted;
            _webView.NavigateToString(
                $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head>" +
                $"<body style=\"margin:0;background:{_pageHex}\"></body></html>");

            // Never let the head start hold up the document: if the blank page has not reported back by now,
            // the page is navigated anyway and the worst case is what happened before this existed.
            await Task.WhenAny(painted.Task, Task.Delay(400));
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
        }
        finally
        {
            if (_webView?.CoreWebView2 is not null)
                _webView.CoreWebView2.NavigationCompleted -= OnCompleted;
        }
    }

    /// <summary>What the rail currently shows, so a selection can be acted on.</summary>
    protected IReadOnlyList<OfficeNavEntry> Navigation { get; private set; } = [];

    /// <summary>
    /// v5.7.0: the left rail. Rows are indented by their level so a Word outline reads as a hierarchy; the
    /// rail itself is transparent and its rows keep the app's own colours (see BuildRailHost).
    /// </summary>
    private ListBox BuildRail()
    {
        var rail = new ListBox
        {
            // The background lives on the host border, so the rail is one surface rather than two.
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
        Width = RailWidth,
        // v5.7.0: transparent, so the window's own backdrop shows through the rail the way it does behind the
        // PDF panel's thumbnails, and the rail and the colours of its rows follow the app's theme. The page
        // area beside it is an opaque grey (a transparent WebView2 page composites to black), which is what
        // makes the rail read as a surface of its own instead of another grey band.
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Child = rail,
    };

    /// <summary>
    /// v5.7.0: a plain row - padding for a comfortable target, and a size that fits the rail. It deliberately
    /// keeps the stock item visuals: a custom template here turned out to be the one thing between the outline
    /// and the reader.
    /// </summary>
    private static Style RailItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 10, 5)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.5d));
        return style;
    }

    /// <summary>
    /// v5.7.0: a rail row was picked. The default scrolls the page to the row's anchor, which is what Word's
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

    /// <summary>v5.7.0: a message from the page itself (the spreadsheet's row pager).</summary>
    protected virtual void OnWebMessage(string message)
    {
    }

    /// <summary>
    /// v6.3.1: the page can report which slide is on screen while the user
    /// scrolls. Selecting that row here does not navigate back into the page.
    /// </summary>
    protected void SelectNavigation(int index)
    {
        if (index < 0 || index >= _rail.Items.Count || _rail.SelectedIndex == index)
            return;

        _suppressRail = true;
        try
        {
            _rail.SelectedIndex = index;
            _rail.ScrollIntoView(_rail.Items[index]);
        }
        finally
        {
            _suppressRail = false;
        }
    }

    /// <summary>v5.7.0: runs JavaScript in the page, ignoring failures (the surface is decoration).</summary>
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
        _ = NavigateWhenReadyAsync(html, null);
    }

    /// <summary>
    /// v5.7.1: hands the page over as a document on the preview's own virtual host rather than as a string,
    /// which is what keeps the sheet that is already on screen up until the page can paint (see
    /// OfficeImageStore.SaveHtml). Falls back to the string hand-over when the file cannot be written.
    /// </summary>
    private void NavigatePage(string html)
    {
        // A page that carries no pictures (the spreadsheet) arrives without a folder of its own; one is made
        // here so that page still gets the document navigation, which is what keeps the black band away.
        if (string.IsNullOrEmpty(_imageFolder))
            _imageFolder = OfficeImageStore.CreateFolder();

        var name = OfficeImageStore.SaveHtml(_imageFolder, html);
        if (name is null)
        {
            Navigate(html);
            return;
        }

        _ = NavigateWhenReadyAsync(null, $"https://{ImageHost}/{name}");
    }

    private async Task NavigateWhenReadyAsync(string html, string url)
    {
        const int attempts = 3;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (_disposed || _quiesced)
                return;

            try
            {
                await WebView2ControlPool.EnsureInitializedAsync(_webView);
            }
            catch (Exception e)
            {
                ProcessHelper.WriteLog(
                    $"[OfficePanel] CoreWebView2 init failed (attempt {attempt}/{attempts}): {e.GetBaseException().Message}");

                if (attempt == attempts)
                {
                    ReportRenderFailure(e, initializationFailure: true);
                    return;
                }

                if (_disposed || _quiesced)
                    return;

                // v3.36.0: restart our Chromium process group before retrying.
                // v3.40.0: repairs the profile first, rebuilds it in place second.
                WebView2Lifecycle.RecoverFromFailedInitialization(attempt);
                await Task.Delay(attempt == 1 ? 300 : 800);
                continue;
            }

            if (_disposed || _quiesced)
                return;

            // v5.7.1: the sheet goes down first, and this waits for it, because the order between the two is
            // what the whole thing depends on - the sheet is only worth anything if it paints before the
            // document's page takes the surface (see LaySheetAsync). It costs what a blank page costs to load.
            await Dispatcher.InvokeAsync(LaySheetAsync).Task.Unwrap();

            if (_disposed || _quiesced)
                return;

            // Touching CoreWebView2 requires the UI thread.
            await Dispatcher.InvokeAsync(() =>
            {
                if (_disposed || _quiesced || _webView.CoreWebView2 == null)
                    return;

                // v5.7.0: the pictures this page references live in a temp folder; the page asks for them
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

                if (url is null)
                    _webView.NavigateToString(html);
                else
                    _webView.CoreWebView2.Navigate(url);
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
    /// v5.7.0: the task also produces the structure for the left rail, which is why the result is a
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
                if (_disposed || _quiesced)
                    return;

                if (t.IsFaulted || t.Result is null)
                {
                    ReportRenderFailure(t.Exception);
                    return;
                }

                ApplyContent(t.Result);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// v5.7.0: puts the rail and the page in place. The picture folder from the previous page is dropped
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

        NavigatePage(content.Html);
    }

    private void ReportRenderFailure(Exception error, bool initializationFailure = false)
    {
        if (_disposed || _reportedRenderFailure)
            return;

        _reportedRenderFailure = true;
        var fallback = RenderFailureFallback;
        if (fallback is not null)
        {
            try
            {
                fallback(error);
                return;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[OfficePanel] fallback renderer failed: {e.Message}");
            }
        }

        if (initializationFailure)
            ShowInitializationFailure();
        else
            Navigate(ErrorHtml(error));
    }

    public void Dispose()
    {
        _disposed = true;

        var control = _webView;
        var folder = _imageFolder;
        _imageFolder = null;

        if (!string.IsNullOrEmpty(folder))
            OfficeImageStore.Cleanup(folder);
        if (control?.CoreWebView2 is not null)
        {
            try
            {
                if (_messageHandler is not null)
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

    /// <summary>
    /// Stops the old document when another preview is requested. The last
    /// rendered sheet stays on screen until the host swaps in the next preview.
    /// </summary>
    public void Quiesce()
    {
        if (_disposed || _quiesced)
            return;

        _quiesced = true;

        try
        {
            _webView.CoreWebView2?.Stop();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[OfficePanel] stopping the old page failed: {e.Message}");
        }
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
