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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using UnblockZoneIdentifier;

namespace QuickLook.Plugin.OfficeViewer;

public sealed class Plugin : IViewer, IPreviewQuiesce
{
    private static readonly string[] Extensions =
    [
        ".doc", ".docx", ".docm", ".odt",
        ".xls", ".xlsx", ".xlsm", ".xlsb", ".ods",
        ".ppt", ".pptx", ".pptm", ".odp",
        ".vsd", ".vsdx",
    ];

    // Presentations keep the self-rendered rail and continuous scrolling. If
    // that renderer cannot parse the deck, use PowerPoint's system preview
    // handler as a last resort because it understands the complete OOXML
    // drawing model.
    private static readonly HashSet<string> PresentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pptx",
        ".pptm",
    };

    // v3.12.0-v3.15.0: OOXML formats rendered by our own panels instead of the
    // Windows system preview component (rounded corners / acrylic / theme).
    private static readonly HashSet<string> SelfRenderedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx",
        ".xlsm",
        ".docx",
        ".docm",
    };

    private object _panel;

    public int Priority => -1;

    public void Init()
    {
        // A crash leaves its image/page folder behind; drop the ones no live
        // preview can still own.
        _ = Task.Run(OfficeImageStore.CleanupStaleFolders);
    }

    public bool CanHandle(string path)
    {
        if (Directory.Exists(path))
            return false;

        if (!Extensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return false;

        // These formats always have a working viewer: presentations fall back
        // to their system handler only when our renderer cannot open the deck.
        var extension = Path.GetExtension(path);
        if (SelfRenderedExtensions.Contains(extension) ||
            PresentationExtensions.Contains(extension))
            return true;

        var previewHandler = ShellExRegister.GetPreviewHandlerGUID(extension);
        if (previewHandler == Guid.Empty)
            return false;

        var checkPreviewHandler = SettingHelper.Get("CheckPreviewHandler", true, "QuickLook.Plugin.OfficeViewer");
        if (!checkPreviewHandler)
            return true;

        if (!string.IsNullOrWhiteSpace(CLSIDRegister.GetName(previewHandler.ToString("B"))))
        {
            return true;
        }
        else
        {
            // Legacy: No more recovering registries for MS Office.
            // TODO: Add a setting page to let users choose the fallback preview handler if the current one is not working.
#if false
            // To restore the preview handler CLSID to MS Office
            // if running with administrative privileges
            if (ShellExRegister.IsRunAsAdmin())
            {
                var fileExtension = Path.GetExtension(path);
                var fallbackHandler = fileExtension switch
                {
                    ".doc" or ".docx" or ".docm" or ".odt" => CLSIDRegister.MicrosoftWord,
                    ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".ods" => CLSIDRegister.MicrosoftExcel,
                    ".ppt" or ".pptx" or ".odp" => CLSIDRegister.MicrosoftPowerPoint,
                    ".vsd" or ".vsdx" => CLSIDRegister.MicrosoftVisio,
                    _ => null,
                };

                if (fallbackHandler == null)
                    return false;

                if (!string.IsNullOrWhiteSpace(CLSIDRegister.GetName(fallbackHandler)))
                {
                    // Admin requested
                    ShellExRegister.SetPreviewHandlerGUID(fileExtension, new Guid(fallbackHandler));
                    return true;
                }
            }
#endif
        }

        return false;
    }

    public void Prepare(string path, ContextObject context)
    {
        // v6.1.3: low memory mode must not be defeated by the parsed-page cache.
        OfficePanelCache.RetainAcrossPreviews = !context.IsLowMemoryMode;
        if (context.IsLowMemoryMode)
            OfficePanelCache.Clear();

        // v6.7.1: a Word document is drawn as one sheet of its own paper, and the sheet spans the page area
        // (see DocxToHtml), so the window is asked for exactly that sheet. The outline now floats inside the
        // page, which gives the paper the full width the old fixed rail used to consume.
        var extension = Path.GetExtension(path);
        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".docm", StringComparison.OrdinalIgnoreCase))
        {
            var paper = DocxToHtml.ReadPaperSize(path);
            if (paper is not null)
            {
                context.SetPreferredSizeFit(new Size { Width = paper.Width, Height = paper.Height }, 0.9d);
                return;
            }
        }

        // v5.7.0: the self-rendered slide and spreadsheet previews use a landscape window.
        context.SetPreferredSizeFit(new Size { Width = 1410, Height = 800 }, 0.8d);
    }

    public void View(string path, ContextObject context)
    {
        var extension = Path.GetExtension(path);
        if (SelfRenderedExtensions.Contains(extension) ||
            PresentationExtensions.Contains(extension))
        {
            ShowSelfRendered(path, extension, context);
            return;
        }

        // MS Office interface does not allow loading of protected view (It's also possible that I haven't found a way)
        // Therefore, we need to predict in advance and then let users choose whether to lift the protection
        if (ZoneIdentifierManager.IsZoneBlocked(path))
        {
            context.Title = $"[PROTECTED VIEW] {Path.GetFileName(path)}";
            var alwaysUnblockProtectedView = SettingHelper.Get("AlwaysUnblockProtectedView", false, "QuickLook.Plugin.OfficeViewer");

            if (alwaysUnblockProtectedView)
            {
                _ = ZoneIdentifierManager.UnblockZone(path);
            }
            else
            {
                MessageBoxResult result = MessageBox.Show(
                    """
                    Be careful - files from the Internet can contain viruses.
                    The Office interface prevents loading in Protected View.

                    Would you like OfficeViewer-Native to unblock the ZoneIdentifier of Internet?
                    """,
                    "PROTECTED VIEW",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result == MessageBoxResult.Yes)
                {
                    _ = ZoneIdentifierManager.UnblockZone(path);
                }
                else
                {
                    context.ViewerContent = new Label()
                    {
                        Content = "The Office interface prevents loading in Protected View.",
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    };
                    context.Title = $"[PROTECTED VIEW] {Path.GetFileName(path)}";
                    context.IsBusy = false;
                    return;
                }
            }
        }

        try
        {
            var previewPanel = new PreviewPanel();
            _panel = previewPanel;
            context.ViewerContent = previewPanel;
            context.Title = Path.GetFileName(path);
            if (!previewPanel.PreviewFile(path, context))
            {
                throw new InvalidOperationException(
                    "The registered preview handler could not open this file.");
            }
        }
        catch (Exception e)
        {
            context.ViewerContent = new Label()
            {
                Content = e.ToString(),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }

        context.IsBusy = false;
    }

    private void ShowSelfRendered(string path, string extension, ContextObject context)
    {
        if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            _panel = new SpreadsheetPanel(path);
        }
        else if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".docm", StringComparison.OrdinalIgnoreCase))
        {
            _panel = new DocumentPanel(path);
        }
        else
        {
            PresentationPanel presentation = null;
            presentation = new PresentationPanel(
                path,
                _ =>
                {
                    if (ReferenceEquals(_panel, presentation))
                        ShowNativePresentationFallback(path, context);
                });
            _panel = presentation;
        }

        context.ViewerContent = (UIElement)_panel;
        context.Title = Path.GetFileName(path);

        // Parsing runs off the UI thread; the rendered page simply appears
        // when ready instead of holding a busy spinner over the preview.
        context.IsBusy = false;
    }

    private void ShowNativePresentationFallback(string path, ContextObject context)
    {
        if (_panel is PresentationPanel failedPanel)
            failedPanel.Dispose();

        _panel = null;

        try
        {
            var previewPanel = new PreviewPanel();
            _panel = previewPanel;
            context.ViewerContent = previewPanel;
            context.Title = Path.GetFileName(path);

            if (!previewPanel.PreviewFile(path, context))
                throw new InvalidOperationException(
                    "Neither the self-rendered nor the native preview could open this presentation.");
        }
        catch (Exception e)
        {
            if (_panel is PreviewPanel failedPreview)
                failedPreview.Dispose();

            _panel = null;
            context.ViewerContent = new Label()
            {
                Content = e.ToString(),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }

        context.IsBusy = false;
    }

    public void Cleanup()
    {
        if (_panel is PreviewPanel previewPanel)
            previewPanel.Dispose();
        else if (_panel is SpreadsheetPanel spreadsheetPanel)
            spreadsheetPanel.Dispose();
        else if (_panel is DocumentPanel documentPanel)
            documentPanel.Dispose();
        else if (_panel is PresentationPanel presentationPanel)
            presentationPanel.Dispose();
        _panel = null;

        if (!OfficePanelCache.RetainAcrossPreviews)
            OfficePanelCache.Clear();
    }

    public void Quiesce()
    {
        if (_panel is OfficePanelBase officePanel)
            officePanel.Quiesce();
    }
}
