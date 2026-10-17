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
using QuickLook.Common.Plugin.MoreMenu;
using QuickLook.Plugin.TextViewer.Detectors;
using QuickLook.Plugin.TextViewer.Themes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickLook.Plugin.TextViewer;

public partial class Plugin : IViewer, IMoreMenu
{
    /// <summary>
    /// RichTextBox parses the whole RTF document on the UI thread. Past this
    /// size that parse freezes the preview window, so show a notice instead.
    /// </summary>
    private const long MaxRichTextLength = 2 * 1024 * 1024;

    private static readonly HashSet<string> WellKnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".rtf",
    };

    // v3.29.0: MarkdownViewer is now loaded on first use (lazy). These
    // extensions belong to it by priority, so TextViewer must not claim
    // them - otherwise the eager content-sniff fallback would win .md /
    // .rst / .adoc / ... previews and MarkdownViewer would never load.
    private static readonly HashSet<string> MarkdownViewerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdx", ".mmd", ".mkd", ".mdwn", ".mdown", ".mdc",
        ".qmd", ".rmd", ".rmarkdown", ".apib", ".mdtxt", ".mdtext",
        ".adoc", ".asciidoc", ".asc", ".ad", ".ipynb", ".mermaid", ".rst", ".restructuredtext",
    };

    private TextViewerPanel _tvp;
    private string _currentPath;

    public int Priority => -5;

    public IEnumerable<IMenuItem> MenuItems => GetMenuItems();

    public void Init()
    {
        // v3.30.0: the ~50 MB of compiled syntax-highlighting definitions
        // no longer load at startup. They are compiled in the background on
        // the first text/code preview (HighlightingThemeManager.EnsureLoadedAsync),
        // so PDF/image-only sessions never pay for code highlighting.
    }

    public bool CanHandle(string path)
    {
        if (Directory.Exists(path))
            return false;

        // v3.29.0: hand the Markdown family to MarkdownViewer (lazy).
        if (MarkdownViewerExtensions.Contains(Path.GetExtension(path)))
            return false;

        if (WellKnownExtensions.Contains(Path.GetExtension(path)))
            return true;

        if (FormatDetector.SupportedExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Read the first 16KB, check if we can get something
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        const int bufferLength = 16 * 1024;
        byte[] buffer = new byte[bufferLength];
        int size = s.Read(buffer, 0, bufferLength);

        return IsText(buffer, size);
    }

    public void Prepare(string path, ContextObject context)
    {
        context.PreferredSize = new Size { Width = 800, Height = 600 };
    }

    public void View(string path, ContextObject context)
    {
        _currentPath = path;

        if (path.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase))
        {
            ViewRichText(path, context);
        }
        else
        {
            _tvp = new TextViewerPanel();
            _tvp.LoadFileAsync(path, context);
            context.ViewerContent = _tvp;
        }
        context.Title = Path.GetFileName(path);
    }

    private static void ViewRichText(string path, ContextObject context)
    {
        var noticeDomain = "QuickLook.Plugin.TextViewer";

        if (new FileInfo(path).Length > MaxRichTextLength)
        {
            context.ViewerContent = CreateNotice(TranslationHelper.Get(
                "RTF_PreviewTooLarge",
                failsafe: "This RTF file is too large for a rich preview (limit 2 MB).\nOpen it in the default app to view it.",
                domain: noticeDomain));
            context.IsBusy = false;
            return;
        }

        try
        {
            var rtfBox = new RichTextBox();
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            rtfBox.Background = new SolidColorBrush(Colors.Transparent);
            rtfBox.Selection.Load(fileStream, DataFormats.Rtf);
            rtfBox.IsReadOnly = true;
            rtfBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            rtfBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

            context.ViewerContent = rtfBox;
            context.IsBusy = false;
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"RTF preview failed for \"{path}\": {e}");
            context.ViewerContent = CreateNotice(TranslationHelper.Get(
                "RTF_PreviewFailed",
                failsafe: "This RTF file could not be previewed.",
                domain: noticeDomain));
            context.IsBusy = false;
        }
    }

    private static TextBlock CreateNotice(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
        };
    }

    public void Cleanup()
    {
        _tvp?.Dispose();
        _tvp = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsText(IReadOnlyList<byte> buffer, int size)
    {
        for (var i = 1; i < size; i++)
            if (buffer[i - 1] == 0 && buffer[i] == 0)
                return false;

        return true;
    }
}
