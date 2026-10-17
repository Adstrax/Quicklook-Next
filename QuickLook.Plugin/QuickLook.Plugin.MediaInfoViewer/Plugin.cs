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
using QuickLook.Plugin.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickLook.Plugin.MediaInfoViewer;

public sealed partial class Plugin : IViewer, IMoreMenuExtended
{
    private const string Domain = "QuickLook.Plugin.MediaInfoViewer";

    private TextViewerPanel _tvp;
    private CancellationTokenSource _scanCancellation;

    public int Priority => int.MinValue;

    public IEnumerable<IMenuItem> MenuItems => GetMenuItems();

    public void Init()
    {
    }

    public bool CanHandle(string path)
    {
        // We only handle files with specific caller
        return false;
    }

    public void Prepare(string path, ContextObject context)
    {
        context.PreferredSize = new Size { Width = 800, Height = 600 };
    }

    public void View(string path, ContextObject context)
    {
        CancelPendingScan();

        var panel = new TextViewerPanel(
            TranslationHelper.Get("MediaInfo_Loading",
                failsafe: "Reading media info...",
                domain: Domain),
            context);
        _tvp = panel;
        AssignHighlightingManager(panel, context);

        panel.Tag = context;
        panel.Drop += OnDrop;

        context.Title = $"{Path.GetFileName(path)}";

        // MediaInfo can take a noticeable time on large or damaged files. Keep
        // the previous preview on screen until the text is ready instead of
        // scanning on the UI thread.
        if (context.ViewerContent == null)
        {
            context.ViewerContent = panel;
            context.IsBusy = true;
        }

        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        _ = LoadInfoAsync(panel, path, context, cancellation);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] files ||
            files.FirstOrDefault() is not string path ||
            _tvp?.Tag is not ContextObject context)
            return;

        CancelPendingScan();

        var panel = _tvp;
        panel.Text = TranslationHelper.Get("MediaInfo_Loading",
            failsafe: "Reading media info...",
            domain: Domain);
        context.Title = $"{Path.GetFileName(path)}";

        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        _ = LoadInfoAsync(panel, path, context, cancellation);
    }

    public void Cleanup()
    {
        GC.SuppressFinalize(this);

        CancelPendingScan();
        _tvp = null!;
    }

    private async Task LoadInfoAsync(
        TextViewerPanel panel,
        string path,
        ContextObject context,
        CancellationTokenSource cancellation)
    {
        try
        {
            var text = await Task.Run(() => ReadMediaInfo(path, cancellation.Token), cancellation.Token);

            if (cancellation.IsCancellationRequested || !ReferenceEquals(_tvp, panel))
                return;

            panel.Text = text;
            context.PendingViewerContent = panel;
            context.IsBusy = false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer preview or Close superseded this scan.
        }
        catch (Exception e)
        {
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_tvp, panel))
                return;

            ProcessHelper.WriteLog($"Media info failed for \"{path}\": {e}");
            panel.Text = TranslationHelper.Get("MediaInfo_Failed",
                failsafe: "Media info could not be read.",
                domain: Domain);

            context.PendingViewerContent = panel;
            context.IsBusy = false;
        }
        finally
        {
            // Clear the field before disposing: Cleanup may race with this
            // completion and must never cancel a disposed source.
            Interlocked.CompareExchange(ref _scanCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private static string ReadMediaInfo(string path, CancellationToken cancellationToken)
    {
        return MediaInfoSession.ReadText(path, cancellationToken);
    }

    private void CancelPendingScan()
    {
        var cancellation = Interlocked.Exchange(ref _scanCancellation, null);
        if (cancellation == null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The scan finished between the exchange and the cancellation.
        }
    }

    private void AssignHighlightingManager(TextViewerPanel tvp, ContextObject context)
    {
        var isDark = OSThemeHelper.AppsUseDarkTheme();

        if (isDark)
        {
            context.Theme = Themes.Dark;
            tvp.Background = Brushes.Transparent;
            tvp.SetResourceReference(TextBlock.ForegroundProperty, "WindowTextForeground");
        }
        else
        {
            context.Theme = Themes.Light;
            tvp.Background = OSThemeHelper.AppsUseDarkTheme()
                ? new SolidColorBrush(Color.FromArgb(175, 255, 255, 255))
                : Brushes.Transparent;
        }
    }
}
