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

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.Shared.NativeMethods;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QuickLook.Plugin.Shared;

public class WebpagePanel : UserControl
{
    protected Uri _currentUri;
    /// <summary>v5.2.0: the last HTML handed to <see cref="NavigateToHtml"/>, so a rebuilt
    /// control can be given the same content back (see <see cref="RecreateForNewScale"/>).</summary>
    protected string _currentHtml;
    protected string _primaryPath;
    protected string _fallbackPath;
    protected WebView2 _webView;
    private readonly WebpageContentLoadState _contentLoadState = new();
    private readonly WebpageNavigationGate _navigationGate = new();
    private bool _disposed;
    private bool _quiesced;
    private string _acceptedContentToken;
    private EventHandler _showReadyHandler;
    private DispatcherTimer _showReadyTimer;

    private const string ReadyMessagePrefix = "quicklook-ready:";

    private const string PauseAnimationsScript = """
        (() => {
          try {
            document.querySelectorAll('video,audio').forEach(media => {
              try { media.pause(); } catch (_) { }
            });
            if (document.getAnimations) {
              document.getAnimations().forEach(animation => {
                try { animation.pause(); } catch (_) { }
              });
            }
            document.querySelectorAll('svg').forEach(svg => {
              try { if (svg.pauseAnimations) svg.pauseAnimations(); } catch (_) { }
            });
            if (!document.getElementById('__quicklook_quiesce')) {
              const style = document.createElement('style');
              style.id = '__quicklook_quiesce';
              style.textContent = '*,*::before,*::after{animation-play-state:paused!important;transition:none!important}';
              (document.head || document.documentElement).appendChild(style);
            }
          } catch (_) { }
        })();
        """;

    private const string DocumentReadyScript = """
        (function () {
          if (!window.__quicklookPostReady) {
            window.__quicklookPostReady = function () {
              if (window.__quicklookReadyPosted)
                return;
              window.__quicklookReadyPosted = true;
              var token = new URLSearchParams(location.search).get('v') || '';
              try {
                window.chrome.webview.postMessage('quicklook-ready:' + token);
              } catch (_) { }
            };
          }

          if (window.__quicklookReadyWatching)
            return;

          window.__quicklookReadyWatching = true;
          var timer = 0;

          function hasContent() {
            var preview = document.getElementById('preview');
            if (preview && (preview.childElementCount > 0 || document.readyState === 'complete'))
              return true;

            var content = document.getElementById('content-html');
            if (content && content.childElementCount > 0)
              return true;

            var svg = document.getElementById('svgWrapper');
            if (svg && svg.childElementCount > 0)
              return true;

            var graphCanvas = document.getElementById('graph-canvas');
            var graphContainer = document.getElementById('graph-container');
            if ((graphCanvas && graphCanvas.childElementCount > 0) ||
                (graphContainer && graphContainer.childElementCount > 0))
              return true;

            var lottie = document.getElementById('bm');
            if (lottie && lottie.childElementCount > 0)
              return true;

            var diagram = document.getElementById('diagram');
            if (diagram && diagram.naturalWidth > 0)
              return true;

            var svga = document.getElementById('canvas');
            if (svga && svga.dataset && svga.dataset.quicklookReady === '1')
              return true;

            return false;
          }

          function schedule() {
            if (window.__quicklookReadyPosted || timer)
              return;
            timer = setTimeout(function () {
              timer = 0;
              if (!hasContent())
                return;

              window.__quicklookPostReady();
            }, 40);
          }

          var observer = new MutationObserver(schedule);
          function start() {
            observer.observe(document.documentElement || document,
              { childList: true, subtree: true, attributes: true, attributeFilter: ['data-quicklook-ready'] });
            schedule();
          }

          if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', start);
          else
            start();
          window.addEventListener('load', schedule);
        })();
        """;

    public string FallbackPath
    {
        get => _fallbackPath;
        set => _fallbackPath = value;
    }

    /// <summary>
    /// True once the panel has released its WebView2 control. A panel in this state
    /// must not be reused for the next document.
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Plain web documents have no script of their own to report a first
    /// content frame, so the panel reports readiness when the main navigation
    /// completes. Structured pages (Markdown, diagrams) leave this false and
    /// signal through <see cref="ContentReady"/> when their content exists.
    /// </summary>
    public bool ReadyWhenNavigationCompleted { get; set; }

    /// <summary>
    /// The generation of the most recently started document load.
    /// </summary>
    public int ContentLoadVersion => _contentLoadState.Version;

    /// <summary>
    /// Raised after the newest document has rendered enough to be shown.
    /// </summary>
    public event EventHandler ContentReady;

    public WebpagePanel()
    {
        if (!Helper.IsWebView2Available())
            Content = CreateDownloadButton();
        else
            InitializeComponent();

    }

    /// <summary>
    /// v5.2.0: Chromium keeps the display scaling it was created with, and the pool hands a parked
    /// control to the next preview unchanged. That is how a Markdown preview ended up laying its
    /// content out for the previous screen scaling - upstream #1956, "the display area is smaller
    /// than the window area". Rebuild for the new scale, and drop the parked controls, which carry
    /// the old one. (<see cref="System.Windows.Media.Visual"/> has no public DPI event - the
    /// protected notification is the hook.)
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        RecreateForNewScale();
    }

    protected virtual void InitializeComponent()
    {
        _webView = AcquireWebView();
        Content = _webView;
    }

    /// <summary>
    /// v3.39.0: takes a warm control from <see cref="WebView2ControlPool"/> (or a
    /// fresh one) and wires this panel to it. Creating a Chromium controller costs
    /// ~300-400 ms, which every web preview paid again and again. Derived panels
    /// that build their own content tree must call this instead of "new WebView2()".
    /// </summary>
    protected WebView2 AcquireWebView()
    {
        var control = WebView2ControlPool.Acquire(BrowserArguments);
        _contentLoadState.Reset();
        _navigationGate.Reset();

        control.NavigationStarting += Webview_NavigationStarting;
        control.NavigationCompleted += WebView_NavigationCompleted;
        control.CoreWebView2InitializationCompleted += WebView_CoreWebView2InitializationCompleted;

        // v3.29.0: track the control so the idle recycler can shut the Chromium
        // process group down after the last web preview closes. A parked control is
        // unregistered again, so the recycler can still fire while it waits in the
        // pool.
        WebView2Lifecycle.Register(control);

        // A reused control is already initialized, so the initialization-completed
        // event never fires for it - re-apply the per-controller state here.
        ApplyControllerState(control);

        return control;
    }

    /// <summary>
    /// Extra Chromium switches this panel's controller needs (the force-dark switch
    /// the PlantUML panel uses, for example). Evaluated before the controller is
    /// created, so it must not depend on instance fields; controls are pooled per
    /// switch set.
    /// </summary>
    protected virtual string BrowserArguments => null;

    private void ApplyControllerState(WebView2 control)
    {
        try
        {
            var core = control?.CoreWebView2;
            if (core == null)
                return; // still initializing: the completed handler calls us again

            // v1.2.1: make the web content follow the app's manual light/dark toggle
            // (prefers-color-scheme inside the page matches this).
            core.Profile.PreferredColorScheme =
                AppThemeState.IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;

            // Re-attach per panel so resource interception (fallback directories,
            // embedded pages) always belongs to the panel that is on screen.
            core.WebResourceRequested -= WebView_WebResourceRequested;
            core.WebResourceRequested += WebView_WebResourceRequested;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

            core.WebMessageReceived -= WebView_WebMessageReceived;
            core.WebMessageReceived += WebView_WebMessageReceived;

            OnControllerReady();

            if (_quiesced)
                PausePageContents(control);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[WebpagePanel] applying controller state failed: {e.Message}");
        }
    }

    /// <summary>
    /// Called whenever this panel gets a usable CoreWebView2 - on a fresh controller
    /// and on a reused one. Derived panels that register controller or control level
    /// handlers override this instead of the one-shot
    /// <c>WebView_CoreWebView2InitializationCompleted</c>.
    /// </summary>
    protected virtual void OnControllerReady()
    {
    }

    /// <summary>
    /// Counterpart of <see cref="OnControllerReady"/>: detach everything added
    /// there. The control outlives this panel in the pool, so a handler left behind
    /// would keep the old panel alive and run against the next document.
    /// </summary>
    protected virtual void OnControllerReleasing()
    {
    }

    /// <summary>
    /// Marks the start of a document load. Only the newest load may report readiness.
    /// </summary>
    public int BeginContentLoad()
    {
        CancelShowWait();
        _acceptedContentToken = null;
        return _contentLoadState.Begin();
    }

    /// <summary>
    /// Shows this panel in <paramref name="context"/> without replacing the
    /// visible preview until the newest document has rendered its first
    /// content frame. The previous content stays on screen meanwhile; a page
    /// that never reports readiness falls back to a swap after three seconds.
    /// </summary>
    public void ShowInContext(ContextObject context)
    {
        if (context == null || _disposed)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ShowInContext(context)));
            return;
        }

        CancelShowWait();

        if (context.ViewerContent == null ||
            ReferenceEquals(context.ViewerContent, this) ||
            IsContentReady(ContentLoadVersion))
        {
            context.ViewerContent = this;
            context.IsBusy = false;
            return;
        }

        var version = ContentLoadVersion;
        context.PendingViewerContent = this;

        EventHandler readyHandler = null;
        DispatcherTimer timer = null;

        void Complete()
        {
            if (readyHandler == null)
                return;

            ContentReady -= readyHandler;
            timer?.Stop();
            if (ReferenceEquals(_showReadyHandler, readyHandler))
            {
                _showReadyHandler = null;
                _showReadyTimer = null;
            }
            readyHandler = null;

            if (_disposed)
                return;

            context.PendingViewerContent = this;
            context.IsBusy = false;
        }

        readyHandler = (_, _) =>
        {
            if (IsContentReady(version))
                Complete();
        };

        timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        timer.Tick += (_, _) => Complete();

        _showReadyHandler = readyHandler;
        _showReadyTimer = timer;
        ContentReady += readyHandler;
        timer.Start();

        // The document may have reported readiness between the first check and
        // the subscription; complete now instead of waiting for the timeout.
        if (IsContentReady(version))
            Complete();
    }

    private void CancelShowWait()
    {
        if (_showReadyHandler != null)
            ContentReady -= _showReadyHandler;

        _showReadyTimer?.Stop();
        _showReadyHandler = null;
        _showReadyTimer = null;
    }

    /// <summary>
    /// Whether <paramref name="version"/> is still the document load that may be shown.
    /// </summary>
    public bool IsContentReady(int version) => _contentLoadState.IsReady(version);

    /// <summary>
    /// v5.2.0: rebuilds this panel's control for the display scaling that is in effect now, and
    /// reloads the content it was showing. Called when the window's DPI changes; the old control
    /// is thrown away rather than parked, because the pool must not hand its scale to anyone else.
    /// </summary>
    private void RecreateForNewScale()
    {
        if (_disposed || _webView == null)
            return;

        var uri = _currentUri ?? _webView.Source;
        var html = _currentHtml;

        WebView2ControlPool.ClearIdle();
        ReleaseWebView(park: false);

        InitializeComponent();

        if (!string.IsNullOrEmpty(html))
            NavigateToHtml(html);
        else if (uri != null)
            NavigateToUri(uri);
    }

    private void ReleaseWebView(bool park = true)
    {
        var control = _webView;

        if (control == null)
            return;

        // Derived panels clean up first - _webView is still valid for them.
        try
        {
            OnControllerReleasing();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[WebpagePanel] releasing controller state failed: {e.Message}");
        }

        _webView = null;

        control.NavigationStarting -= Webview_NavigationStarting;
        control.NavigationCompleted -= WebView_NavigationCompleted;
        control.CoreWebView2InitializationCompleted -= WebView_CoreWebView2InitializationCompleted;

        try
        {
            if (control.CoreWebView2 != null)
            {
                control.CoreWebView2.WebResourceRequested -= WebView_WebResourceRequested;
                control.CoreWebView2.WebMessageReceived -= WebView_WebMessageReceived;
            }
        }
        catch
        {
            // best effort - the controller may already be gone
        }

        WebView2Lifecycle.Unregister(control);

        if (ReferenceEquals(Content, control))
            Content = null;

        if (park)
            WebView2ControlPool.Release(control);
        else
            WebView2ControlPool.Discard(control);
    }

    public void NavigateToFile(string path)
    {
        try
        {
            _primaryPath = Path.GetDirectoryName(path);
        }
        catch (Exception e)
        {
            // Omit logging for less important logs
            Debug.WriteLine(e);
        }

        var uri = Path.IsPathRooted(path) ? Helper.FilePathToFileUrl(path) : new Uri(path);

        NavigateToUri(uri);
    }

    public void NavigateToUri(Uri uri)
    {
        if (_webView == null)
            return;

        _webView.Source = uri;
        _currentUri = _webView.Source;
    }

    public void NavigateToHtml(string html)
    {
        _currentHtml = html;

        // v3.32.0: only navigate when the controller really came up - a faulted
        // EnsureCoreWebView2Async (WebView2 runtime missing or being restarted)
        // used to surface as an unhandled "CoreWebView2 prior to being
        // initialized" exception. Chromium also fails transiently with
        // 0x8007139F right after a controller was torn down, so one retry is
        // enough to keep the preview working. The CoreWebView2 check runs on the
        // UI thread because the WebView2 control is a DispatcherObject.
        _ = NavigateWhenReadyAsync(html);
    }

    private async Task NavigateWhenReadyAsync(string html)
    {
        if (_webView == null)
            return;

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
                    $"[WebpagePanel] CoreWebView2 init failed (attempt {attempt}/{attempts}): {e.GetBaseException().Message}");

                if (attempt == attempts)
                {
                    ShowInitializationFailure();
                    return;
                }

                // v3.36.0: give the browser process group a clean restart before
                // trying again - that is what makes a stale profile recoverable.
                // v3.40.0: the recovery repairs the profile first and only rebuilds
                // it in place when that was not enough (see the method remarks).
                WebView2Lifecycle.RecoverFromFailedInitialization(attempt);
                await Task.Delay(attempt == 1 ? 300 : 800);
                continue;
            }

            if (_disposed || _quiesced)
                return;

            // Touching CoreWebView2 requires the UI thread; marshalling keeps
            // this working when a plugin calls NavigateToHtml off-thread.
            await Dispatcher.InvokeAsync(() =>
            {
                if (_disposed || _webView?.CoreWebView2 == null)
                    return;

                _webView.NavigateToString(html);
            });

            return;
        }
    }

    /// <summary>
    /// v3.36.0: replaces the (blank) panel with an explanation when WebView2 could
    /// not be brought up at all, instead of leaving the user with an empty preview.
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
                    domain: Assembly.GetExecutingAssembly().GetName().Name),
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

    protected virtual void Webview_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith("data:")) // when using NavigateToString
        {
            _acceptedContentToken = string.Empty;
            _navigationGate.Accept(e.NavigationId);
            return;
        }

        var newUri = new Uri(e.Uri);
        if (newUri == _currentUri)
        {
            _acceptedContentToken = GetNavigationToken(newUri);
            _navigationGate.Accept(e.NavigationId);
            return;
        }

        e.Cancel = true;

        // Open in default browser
        try
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
            {
                Debug.WriteLine($"Invalid URI format: {e.Uri}");
                return;
            }

            // Safe schemes can open directly
            if (uri.Scheme == Uri.UriSchemeHttp ||
                uri.Scheme == Uri.UriSchemeHttps ||
                uri.Scheme == Uri.UriSchemeMailto)
            {
                try
                {
                    Process.Start(uri.AbsoluteUri);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }

            // Ask user for unsafe schemes. Use dispatcher to avoid blocking thread.
            string associatedApp = ShlwApi.GetAssociatedAppForScheme(uri.Scheme);
            _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                // TODO: translation
                var result = MessageBox.Show(
                    !string.IsNullOrEmpty(associatedApp) ?
                    $"The following link will open in {associatedApp}:\n{e.Uri}" : $"The following link will open:\n{e.Uri}",
                    !string.IsNullOrEmpty(associatedApp) ?
                    $"Open {associatedApp}?" : "Open custom URI?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        Process.Start(e.Uri);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to open URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open URL: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether this completion belongs to the navigation most recently allowed by this panel.
    /// </summary>
    protected bool IsCurrentNavigation(CoreWebView2NavigationCompletedEventArgs e) =>
        e != null && _navigationGate.IsCurrent(e.NavigationId);

    private void WebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(message) ||
                !message.StartsWith(ReadyMessagePrefix, StringComparison.Ordinal))
                return;

            var token = message.Substring(ReadyMessagePrefix.Length);
            if (string.IsNullOrEmpty(_acceptedContentToken) ||
                !string.Equals(token, _acceptedContentToken, StringComparison.Ordinal))
                return;

            SignalContentReady();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebpagePanel] reading readiness message failed: {ex.Message}");
        }
    }

    protected void SignalContentReady()
    {
        if (_disposed || _quiesced || _contentLoadState.IsReady(_contentLoadState.Version))
            return;

        _contentLoadState.MarkReady();
        ContentReady?.Invoke(this, EventArgs.Empty);
    }

    private static string GetNavigationToken(Uri uri)
    {
        var query = uri?.Query;
        if (string.IsNullOrEmpty(query))
            return string.Empty;

        foreach (var part in query.TrimStart('?').Split('&'))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 &&
                string.Equals(part.Substring(0, separator), "v", StringComparison.Ordinal))
                return Uri.UnescapeDataString(part.Substring(separator + 1));
        }

        return string.Empty;
    }

    protected virtual void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!IsCurrentNavigation(e))
            return;

        if (_quiesced)
        {
            PausePageContents(_webView);
            return;
        }

        if (!e.IsSuccess || ReadyWhenNavigationCompleted)
            SignalContentReady();

        // The document-created hook may have been registered after this page
        // started loading (fresh controller). Install it in the loaded document
        // as well; the script guards against a second observer.
        _ = InstallDocumentReadyObserverAsync();

        // Keep the background transparent so Mica stays visible.
        _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);
    }

    private async Task InstallDocumentReadyObserverAsync()
    {
        try
        {
            var core = _webView?.CoreWebView2;
            if (core != null)
                await core.ExecuteScriptAsync(DocumentReadyScript);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[WebpagePanel] installing readiness observer failed: {e.Message}");
        }
    }

    protected virtual void WebView_CoreWebView2InitializationCompleted(object sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        // v3.29.0: the panel may have been disposed while CoreWebView2 was
        // still initializing; drop the freshly created controller so it
        // does not linger until the idle recycler runs.
        if (_disposed)
        {
            try { _webView?.Dispose(); }
            catch { /* best effort */ }
            return;
        }

        if (e.IsSuccess)
        {
            // v1.2.2/v6.0.0: keep the page background transparent so the window's Mica
            // backdrop shows through. Install the rule as soon as the document exists,
            // not at DOMContentLoaded: waiting until then painted an opaque first frame
            // and flashed to transparent on every Markdown switch.
            // Injected once per controller and inherited by every later document,
            // which is why it stays here rather than in ApplyControllerState.
            _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                "(function(){" +
                "var css='html,body,.markdown-body,#content,.ipynb-notebook,.jp-Notebook,.document{background:transparent!important}" +
                "::-webkit-scrollbar{width:4px;height:4px}" +
                "::-webkit-scrollbar-thumb{background:rgba(128,128,128,0.4);border-radius:2px}" +
                "::-webkit-scrollbar-track{background:transparent}" +
                "::-webkit-scrollbar-corner{background:transparent}';" +
                "function install(){" +
                "if(document.getElementById('__ql_transparency'))return;" +
                "var s=document.createElement('style');s.id='__ql_transparency';s.textContent=css;" +
                "var parent=document.head||document.documentElement;if(parent)parent.appendChild(s);" +
                "}" +
                "try{document.documentElement.style.setProperty('background-color','transparent','important');}catch(_){}" +
                "install();" +
                "if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',install);" +
                "})();");

            _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(DocumentReadyScript);

            // Theme, resource hook and the derived panel's own controller state - the
            // same call the reuse path makes (see AcquireWebView).
            ApplyControllerState(_webView);
        }
        else
        {
            ProcessHelper.WriteLog($"[WebpagePanel] CoreWebView2 init failed: {e.InitializationException}");
        }
    }

    protected virtual void WebView_WebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(_fallbackPath) || !Directory.Exists(_fallbackPath))
        {
            return;
        }

        try
        {
            var requestedUri = new Uri(args.Request.Uri);

            if (requestedUri.Scheme == "file")
            {
                // Check if the request is for a local file
                if (!File.Exists(requestedUri.LocalPath))
                {
                    // Try loading from fallback directory
                    var fileName = Path.GetFileName(requestedUri.LocalPath);
                    var fileDirectoryName = Path.GetDirectoryName(requestedUri.LocalPath);

                    // Convert the primary path to fallback path
                    if (fileDirectoryName.StartsWith(_primaryPath))
                    {
                        var fallbackFilePath = Path.Combine(
                            _fallbackPath.Trim('/', '\\'), // Make it combinable
                            fileDirectoryName.Substring(_primaryPath.Length).Trim('/', '\\'), // Make it combinable
                            fileName
                        );

                        if (File.Exists(fallbackFilePath))
                        {
                            // Serve the file from the fallback directory
                            var fileStream = new FileStream(fallbackFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            var response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                fileStream, 200, "OK", "Content-Type: application/octet-stream");
                            args.Response = response;
                        }
                    }
                }
                // Check if the request exceeds MAX_PATH (260) limitation
                else if (requestedUri.LocalPath.Length >= 260)
                {
                    if (File.Exists(requestedUri.LocalPath))
                    {
                        var fileStream = new FileStream(requestedUri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                            fileStream, 200, "OK", MimeTypes.GetContentType(Path.GetExtension(requestedUri.LocalPath)));
                        args.Response = response;
                    }
                }
            }
        }
        catch (Exception e)
        {
            // We don't need to feel burdened by any exceptions
            Debug.WriteLine(e);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        CancelShowWait();

        // v3.39.0: the control goes back to the pool instead of being disposed - that
        // is what saves the controller creation on the next web preview.
        ReleaseWebView();
    }

    /// <summary>
    /// v6.0.0: another preview is opening. Stop loading the old page and freeze videos,
    /// CSS/Web Animations and SVG SMIL at their current frame. The pooled WebView2 control
    /// itself is left untouched so the normal release path can still park it.
    /// </summary>
    public void Quiesce()
    {
        if (_disposed || _quiesced)
            return;

        _quiesced = true;

        var control = _webView;
        if (control == null)
            return;

        try
        {
            control.CoreWebView2?.Stop();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[WebpagePanel] stopping the old page failed: {e.Message}");
        }

        PausePageContents(control);
    }

    private void PausePageContents(WebView2 control)
    {
        if (control == null)
            return;

        if (control.Dispatcher.CheckAccess())
            _ = PausePageContentsAsync(control);
        else
            control.Dispatcher.BeginInvoke(new Action(() => _ = PausePageContentsAsync(control)));
    }

    private async Task PausePageContentsAsync(WebView2 control)
    {
        try
        {
            // The pool may have handed this control to another panel between the
            // BeginInvoke and this callback. Only the panel that still owns the
            // control is allowed to freeze its document.
            if (_disposed || !ReferenceEquals(_webView, control))
                return;

            var core = control.CoreWebView2;
            if (core != null)
                await core.ExecuteScriptAsync(PauseAnimationsScript);
        }
        catch (Exception e)
        {
            // The panel may already be on its way back to the pool. Freezing it is best effort.
            Debug.WriteLine($"[WebpagePanel] pausing old page animations failed: {e.Message}");
        }
    }

    private object CreateDownloadButton()
    {
        var button = new Button
        {
            Content = TranslationHelper.Get("WEBVIEW2_NOT_AVAILABLE",
                domain: Assembly.GetExecutingAssembly().GetName().Name),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(20, 6, 20, 6)
        };
        button.Click += (sender, e) => Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703");

        return button;
    }

    public static class MimeTypes
    {
        public const string Html = "text/html";
        public const string JavaScript = "application/javascript";
        public const string Css = "text/css";
        public const string Json = "application/json";
        public const string Xml = "application/xml";
        public const string Svg = "image/svg+xml";
        public const string Png = "image/png";
        public const string Jpeg = "image/jpeg";
        public const string Gif = "image/gif";
        public const string Webp = "image/webp";
        public const string Ico = "image/x-icon";
        public const string Avif = "image/avif";
        public const string Woff = "font/woff";
        public const string Woff2 = "font/woff2";
        public const string Ttf = "font/ttf";
        public const string Otf = "font/otf";
        public const string Mp3 = "audio/mpeg";
        public const string Mp4 = "video/mp4";
        public const string Webm = "video/webm";
        public const string Pdf = "application/pdf";
        public const string Binary = "application/octet-stream";
        public const string Text = "text/plain";

        public static string GetContentType(string extension = null) => $"Content-Type: {GetMimeType(extension)}";

        /// <summary>
        /// Only handle known extensions from resources
        /// </summary>
        public static string GetMimeType(string extension = null) => extension?.ToLowerInvariant() switch
        {
            // Core web files
            ".html" or ".htm" => Html,
            ".js" => JavaScript,
            ".css" => Css,
            ".json" => Json,
            ".xml" => Xml,

            // Images
            ".png" => Png,
            ".jpg" or ".jpeg" => Jpeg,
            ".gif" => Gif,
            ".webp" => Webp,
            ".svg" => Svg,
            ".ico" => Ico,
            ".avif" => Avif,

            // Fonts
            ".woff" => Woff,
            ".woff2" => Woff2,
            ".ttf" => Ttf,
            ".otf" => Otf,

            // Media
            ".mp3" => Mp3,
            ".mp4" => Mp4,
            ".webm" => Webm,

            // Documents
            ".pdf" => Pdf,
            ".txt" => Text,

            // Archives
            ".zip" => "application/zip",
            ".gz" => "application/gzip",
            ".rar" => "application/vnd.rar",
            ".7z" => "application/x-7z-compressed",

            // Default
            _ => Binary,
        };
    }
}
