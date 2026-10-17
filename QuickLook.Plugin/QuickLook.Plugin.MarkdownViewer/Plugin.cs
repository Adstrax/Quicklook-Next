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

using QuickLook.Common.Plugin;
using QuickLook.Plugin.Shared;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace QuickLook.Plugin.MarkdownViewer;

public sealed class Plugin : IViewer, IPreviewQuiesce
{
    private WebpagePanel _panel;
    private EventHandler _contentReadyHandler;
    private DispatcherTimer _contentReadyTimer;

    // v6.0.0: Markdown -> Markdown switches keep the same panel (and the same WebView2).
    // Replacing the panel tore the browser surface out of the window and put it back,
    // which flashed through the transparent page. Warm-up contexts get a private panel;
    // they must never take the one the preview window is showing.
    private static MarkdownPanel _reusableMarkdownPanel;
    private static MermaidPanel _reusableMermaidPanel;
    private static AsciiDocPanel _reusableAsciiDocPanel;
    private static IpynbPanel _reusableIpynbPanel;
    private static RstPanel _reusableRstPanel;

    /// <summary>
    /// Markdown and Markdown-like extensions
    /// It is not guaranteed to support all formats perfectly
    /// </summary>
    private static readonly string[] _markdownExtensions =
    [
        ".md", ".markdown", // The most common Markdown extensions
        ".mdx", // MDX (Markdown + JSX), used in React ecosystems
        ".mmd", // MultiMarkdown (MMD), an extended version of Markdown or Mermaid diagram source file
        ".mkd", ".mdwn", ".mdown", // Early Markdown variants, used by different parsers like Pandoc, Gitit, and Hakyll
        ".mdc", // A Markdown variant used by Cursor AI [Repeated format from ImageViewer]
        ".qmd", // Quarto Markdown, developed by RStudio for scientific computing and reproducible reports
        ".rmd", ".rmarkdown", // R Markdown, mainly used in RStudio
        ".apib", // API Blueprint, a Markdown-based format
        ".mdtxt", ".mdtext", // Less common
    ];

    public int Priority => 0;

    public void Init()
    {
        // v3.24.0: MarkdownPanel's static ctor loads the embedded mermaid /
        // MathJax assets (~5 MB) into a static dictionary. Init runs on the
        // background plugin-load thread, so force the load here and the first
        // markdown preview never pays it on the UI thread.
        _ = MarkdownPanel.ContainsKey("/__preload__");
    }

    public bool CanHandle(string path)
    {
        if (Directory.Exists(path))
            return false;

        return AsciiDocPanel.CanHandle(path)
            || IpynbPanel.CanHandle(path)
            || MermaidPanel.CanHandle(path)
            || RstPanel.CanHandle(path)
            || _markdownExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    public void Prepare(string path, ContextObject context)
    {
        context.PreferredSize = new Size(1000, 600);
    }

    public void View(string path, ContextObject context)
    {
        if (AsciiDocPanel.CanHandle(path))
        {
            var panel = ReuseAsciiDocPanel(context);
            panel.Resume();
            panel.PreviewAsciiDoc(path);
            _panel = panel;
        }
        else if (IpynbPanel.CanHandle(path))
        {
            var panel = ReuseIpynbPanel(context);
            panel.Resume();
            panel.PreviewIpynb(path);
            _panel = panel;
        }
        else if (MermaidPanel.CanHandle(path))
        {
            var panel = ReuseMermaidPanel(context);
            panel.Resume();
            panel.PreviewMermaid(path);
            _panel = panel;
        }
        else if (RstPanel.CanHandle(path))
        {
            var panel = ReuseRstPanel(context);
            panel.Resume();
            panel.PreviewRst(path);
            _panel = panel;
        }
        else
        {
            var panel = ReuseMarkdownPanel(context);
            panel.Resume();
            panel.PreviewMarkdown(path);
            _panel = panel;
        }

        context.Title = Path.GetFileName(path);
        ShowPanelWhenReady(_panel, context);
    }

    private void ShowPanelWhenReady(WebpagePanel panel, ContextObject context)
    {
        var version = panel.ContentLoadVersion;

        // The initial preview and an in-place document replacement have nothing
        // meaningful to hide: attach immediately and keep the existing behavior.
        if (context.ViewerContent == null ||
            ReferenceEquals(context.ViewerContent, panel) ||
            panel.IsContentReady(version))
        {
            context.ViewerContent = panel;
            context.IsBusy = false;
            return;
        }

        // A different panel means a different web document format. Keep the old
        // content on screen until the new page has rendered its first document.
        DetachContentReadyWait();
        context.PendingViewerContent = panel;

        EventHandler readyHandler = null;
        DispatcherTimer timer = null;

        void Complete()
        {
            if (readyHandler == null)
                return;

            panel.ContentReady -= readyHandler;
            timer?.Stop();
            if (ReferenceEquals(_contentReadyHandler, readyHandler))
                _contentReadyHandler = null;
            if (ReferenceEquals(_contentReadyTimer, timer))
                _contentReadyTimer = null;
            readyHandler = null;

            if (panel.IsDisposed)
                return;

            context.PendingViewerContent = panel;
            context.IsBusy = false;
        }

        readyHandler = (_, _) =>
        {
            if (panel.IsContentReady(version))
                Complete();
        };

        // A malformed document or failed navigation must not leave the previous
        // preview on screen forever.
        timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        timer.Tick += (_, _) => Complete();

        _contentReadyHandler = readyHandler;
        _contentReadyTimer = timer;
        panel.ContentReady += readyHandler;
        timer.Start();
    }

    private void DetachContentReadyWait()
    {
        if (_contentReadyHandler != null && _panel != null)
            _panel.ContentReady -= _contentReadyHandler;

        _contentReadyTimer?.Stop();
        _contentReadyHandler = null;
        _contentReadyTimer = null;
    }

    private static MarkdownPanel ReuseMarkdownPanel(ContextObject context)
    {
        var panel = _reusableMarkdownPanel;
        if (!context.IsWarmUp && panel != null && !panel.IsDisposed)
            return panel;

        panel = new MarkdownPanel();
        if (!context.IsWarmUp)
            _reusableMarkdownPanel = panel;
        return panel;
    }

    private static AsciiDocPanel ReuseAsciiDocPanel(ContextObject context)
    {
        var panel = _reusableAsciiDocPanel;
        if (!context.IsWarmUp && panel != null && !panel.IsDisposed)
            return panel;

        panel = new AsciiDocPanel();
        if (!context.IsWarmUp)
            _reusableAsciiDocPanel = panel;
        return panel;
    }

    private static IpynbPanel ReuseIpynbPanel(ContextObject context)
    {
        var panel = _reusableIpynbPanel;
        if (!context.IsWarmUp && panel != null && !panel.IsDisposed)
            return panel;

        panel = new IpynbPanel();
        if (!context.IsWarmUp)
            _reusableIpynbPanel = panel;
        return panel;
    }

    private static RstPanel ReuseRstPanel(ContextObject context)
    {
        var panel = _reusableRstPanel;
        if (!context.IsWarmUp && panel != null && !panel.IsDisposed)
            return panel;

        panel = new RstPanel();
        if (!context.IsWarmUp)
            _reusableRstPanel = panel;
        return panel;
    }

    private static MermaidPanel ReuseMermaidPanel(ContextObject context)
    {
        var panel = _reusableMermaidPanel;
        if (!context.IsWarmUp && panel != null && !panel.IsDisposed)
            return panel;

        panel = new MermaidPanel();
        if (!context.IsWarmUp)
            _reusableMermaidPanel = panel;
        return panel;
    }

    public void Cleanup()
    {
        GC.SuppressFinalize(this);

        DetachContentReadyWait();
        _panel?.Dispose();
        _panel = null;
    }

    public void Quiesce()
    {
        _panel?.Quiesce();
    }
}
