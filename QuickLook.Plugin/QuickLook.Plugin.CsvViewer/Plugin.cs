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
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace QuickLook.Plugin.CsvViewer;

public sealed partial class Plugin : IViewer, IMoreMenu
{
    private CsvViewerPanel _panel;
    private string _currentPath;
    private CancellationTokenSource _loadCancellation;

    public int Priority => 0;

    public void Init()
    {
    }

    public bool CanHandle(string path)
    {
        if (Directory.Exists(path))
            return false;

        return path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".psv", StringComparison.OrdinalIgnoreCase);
    }

    public void Prepare(string path, ContextObject context)
    {
        context.PreferredSize = new Size { Width = 1000, Height = 700 };
    }

    public void View(string path, ContextObject context)
    {
        CancelPendingLoad();

        _currentPath = path;
        var panel = new CsvViewerPanel();
        _panel = panel;
        context.Title = $"{Path.GetFileName(path)}";

        // A switch keeps the previous preview on screen until the first rows
        // are parsed; the first preview has nothing to hide behind and shows
        // the spinner over the empty grid instead.
        if (context.ViewerContent == null)
        {
            context.ViewerContent = panel;
            context.IsBusy = true;
        }

        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _ = LoadAsync(panel, path, context, cancellation);
    }

    public void Cleanup()
    {
        GC.SuppressFinalize(this);

        CancelPendingLoad();
        _panel = null;
    }

    private async Task LoadAsync(
        CsvViewerPanel panel,
        string path,
        ContextObject context,
        CancellationTokenSource cancellation)
    {
        try
        {
            await panel.LoadFileAsync(path, cancellation.Token);

            if (cancellation.IsCancellationRequested || !ReferenceEquals(_panel, panel))
                return;

            context.PendingViewerContent = panel;
            context.IsBusy = false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer preview or Close superseded this load.
        }
        catch (Exception e)
        {
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_panel, panel))
                return;

            ProcessHelper.WriteLog($"CSV preview failed for \"{path}\": {e}");
            panel.ShowMessage(TranslationHelper.Get(
                "Preview_LoadFailed",
                failsafe: "This spreadsheet could not be previewed.",
                domain: "QuickLook.Plugin.CsvViewer"));

            context.PendingViewerContent = panel;
            context.IsBusy = false;
        }
        finally
        {
            // Clear the field before disposing: Cleanup may race with this
            // completion and must never cancel a disposed source.
            Interlocked.CompareExchange(ref _loadCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void CancelPendingLoad()
    {
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation == null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The parse finished between the exchange and the cancellation.
        }
    }
}
