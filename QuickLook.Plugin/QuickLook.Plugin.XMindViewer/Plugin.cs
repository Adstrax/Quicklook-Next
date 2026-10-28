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
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace QuickLook.Plugin.XMindViewer;

public sealed class Plugin : IViewer, IPreviewQuiesce
{
    private XMindPanel _panel;
    private CancellationTokenSource _loadCancellation;

    // XMind is a real document preview now, so it must win over the fallback
    // system-thumbnail viewer for the same extension.
    public int Priority => 10;

    public void Init()
    {
    }

    public bool CanHandle(string path)
    {
        return !Directory.Exists(path) &&
            path.EndsWith(".xmind", StringComparison.OrdinalIgnoreCase);
    }

    public void Prepare(string path, ContextObject context)
    {
        context.PreferredSize = new Size(1120, 760);
        context.DeferResizeUntilReady = true;
    }

    public void View(string path, ContextObject context)
    {
        CancelPendingLoad();

        var panel = new XMindPanel();
        _panel = panel;
        context.Title = Path.GetFileName(path);

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

    public void Quiesce()
    {
        CancelPendingLoad();
    }

    private async Task LoadAsync(
        XMindPanel panel,
        string path,
        ContextObject context,
        CancellationTokenSource cancellation)
    {
        try
        {
            var sheets = await Task.Run(
                () => XMindParser.Parse(path, cancellation.Token),
                cancellation.Token);

            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_panel, panel))
                return;

            panel.Apply(sheets);
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

            ProcessHelper.WriteLog($"XMind preview failed for \"{path}\": {e}");
            panel.ShowError(TranslationHelper.Get(
                "XMind_LoadFailed",
                failsafe: "This XMind file could not be previewed.",
                domain: "QuickLook.Plugin.XMindViewer"));

            context.PendingViewerContent = panel;
            context.IsBusy = false;
        }
        finally
        {
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
            // The parse completed between the exchange and the cancellation.
        }
    }
}
