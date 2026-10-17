// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.
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

using MsgReader;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace QuickLook.Plugin.MailViewer;

public sealed class Plugin : IViewer, IPreviewQuiesce
{
    private const string TempRootName = "QuickLookNext.Mail";

    private readonly object _tempRootsLock = new();
    private readonly List<string> _tempRoots = [];

    private WebpagePanel _panel;
    private CancellationTokenSource _mailCancellation;

    public int Priority => 0;

    public void Init()
    {
        // A crash leaves its extraction folder behind; drop folders that no
        // running preview can still own (a day is well past any parse).
        _ = Task.Run(CleanupStaleTempRoots);
    }

    public bool CanHandle(string path)
    {
        return !Directory.Exists(path) &&
            (path.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".msg", StringComparison.OrdinalIgnoreCase));
    }

    public void Prepare(string path, ContextObject context)
    {
        context.PreferredSize = new Size { Width = 1000, Height = 600 };
    }

    public void View(string path, ContextObject context)
    {
        CancelPendingExtraction();

        var panel = new WebpagePanel
        {
            ReadyWhenNavigationCompleted = true,
        };
        _panel = panel;
        panel.BeginContentLoad();

        context.Title = Path.GetFileName(path);
        context.IsBusy = context.ViewerContent == null;

        var cancellation = new CancellationTokenSource();
        _mailCancellation = cancellation;

        _ = PrepareMailAsync(path, panel, context, cancellation);
    }

    public void Cleanup()
    {
        GC.SuppressFinalize(this);

        CancelPendingExtraction();

        _panel?.Dispose();
        _panel = null;

        DeleteRegisteredTempRoots(null);
    }

    public void Quiesce()
    {
        _panel?.Quiesce();
    }

    private async Task PrepareMailAsync(
        string path,
        WebpagePanel panel,
        ContextObject context,
        CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        var tempRoot = Path.Combine(Path.GetTempPath(), TempRootName, Guid.NewGuid().ToString("N"));
        var keepTempRoot = false;

        try
        {
            // MsgReader cannot interrupt a parse that already started, so let a
            // burst of arrow-key steps settle first. A quick pass over several
            // messages then only extracts the one the user stopped on.
            await Task.Delay(40, cancellationToken);

            var body = await Task.Run(() => ExtractMailBody(path, tempRoot, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            await panel.Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested ||
                    panel.IsDisposed ||
                    !ReferenceEquals(_panel, panel))
                    return;

                RegisterTempRoot(tempRoot);
                keepTempRoot = true;

                panel.ContentReady += DeleteOlderTempRootsOnReady;
                panel.FallbackPath = Path.GetDirectoryName(body);
                panel.NavigateToFile(body);
                panel.ShowInContext(context, waitForFirstFrame: true);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer preview or Close superseded this extraction.
        }
        catch (Exception e)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ProcessHelper.WriteLog($"Mail preview failed for \"{path}\": {e}");
                await ShowFailureAsync(panel, context, cancellationToken);
            }
        }
        finally
        {
            if (!keepTempRoot)
                TryDeleteDirectory(tempRoot);

            // Clear the field before disposing: a newer preview may have replaced
            // it, and CancelPendingExtraction must never touch a disposed source.
            Interlocked.CompareExchange(ref _mailCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private static string ExtractMailBody(string path, string tempRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(tempRoot);
        var files = new Reader().ExtractToFolder(path, tempRoot, ReaderHyperLinks.Both);

        cancellationToken.ThrowIfCancellationRequested();

        var body = files.FirstOrDefault(file => !string.IsNullOrEmpty(file));
        if (body != null)
            return body;

        throw new InvalidDataException("The message did not contain a previewable body.");
    }

    private async Task ShowFailureAsync(
        WebpagePanel panel,
        ContextObject context,
        CancellationToken cancellationToken)
    {
        try
        {
            await panel.Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested ||
                    panel.IsDisposed ||
                    !ReferenceEquals(_panel, panel))
                    return;

                context.ViewerContent = new TextBlock
                {
                    Text = "This email could not be previewed.",
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(24),
                };
                context.IsBusy = false;
            });
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    private void DeleteOlderTempRootsOnReady(object sender, EventArgs e)
    {
        if (sender is WebpagePanel panel)
            panel.ContentReady -= DeleteOlderTempRootsOnReady;

        DeleteRegisteredTempRoots(GetCurrentTempRoot());
    }

    private string GetCurrentTempRoot()
    {
        lock (_tempRootsLock)
        {
            return _tempRoots.Count == 0 ? null : _tempRoots[^1];
        }
    }

    private void RegisterTempRoot(string tempRoot)
    {
        lock (_tempRootsLock)
            _tempRoots.Add(tempRoot);
    }

    private void DeleteRegisteredTempRoots(string except)
    {
        List<string> roots;
        lock (_tempRootsLock)
        {
            roots = [.. _tempRoots];
            _tempRoots.Clear();
            if (except != null)
                _tempRoots.Add(except);
        }

        foreach (var root in roots)
        {
            if (!string.Equals(root, except, StringComparison.OrdinalIgnoreCase))
                TryDeleteDirectory(root);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    private static void CleanupStaleTempRoots()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), TempRootName);
            if (!Directory.Exists(root))
                return;

            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                        Directory.Delete(directory, true);
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e);
                }
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    private void CancelPendingExtraction()
    {
        var cancellation = Interlocked.Exchange(ref _mailCancellation, null);
        if (cancellation == null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The extraction finished and disposed its source between the exchange
            // and the cancellation.
        }
    }
}
