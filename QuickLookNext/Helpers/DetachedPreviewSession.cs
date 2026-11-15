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
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace QuickLookNext.Helpers;

/// <summary>
/// A full viewer window that lives beside another preview, such as the archive
/// browser. Switching entries updates this window instead of rebuilding a
/// simplified embedded shell.
/// </summary>
internal sealed class DetachedPreviewSession : IDetachedPreviewSession
{
    private readonly Window _owner;
    private readonly ViewWindowManager _manager;
    private readonly ViewerWindow _viewer;
    private long _showVersion;
    private string _currentPath;
    private bool _closed;
    private bool _disposed;

    internal DetachedPreviewSession(
        Window owner,
        object source,
        ViewWindowManager manager)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _viewer = new ViewerWindow
        {
            Owner = owner,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        _viewer.Closed += Viewer_Closed;
        _owner.Closed += Owner_Closed;
    }

    public event EventHandler Closed;

    internal bool IsVisible =>
        !_closed && !_disposed && _viewer.IsVisible;

    internal string CurrentPath => _currentPath;

    internal nint WindowHandle =>
        IsVisible ? new WindowInteropHelper(_viewer).Handle : IntPtr.Zero;

    public Task ShowAsync(string path)
        => ShowAsyncCore(path, forceReload: false);

    internal Task ReloadAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentPath))
            return Task.CompletedTask;

        return ShowAsyncCore(_currentPath, forceReload: true);
    }

    private async Task ShowAsyncCore(string path, bool forceReload)
    {
        if (_disposed || _closed)
            throw new ObjectDisposedException(nameof(DetachedPreviewSession));

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("The preview file no longer exists.", path);

        if (!forceReload &&
            IsVisible &&
            string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var version = ++_showVersion;
        IViewer plugin;
        try
        {
            plugin = await PluginManager.GetInstance()
                .FindMatchAsync(path)
                .ConfigureAwait(true);
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog(e.ToString());
            throw;
        }

        if (_disposed || _closed || version != _showVersion)
        {
            CleanupUnusedPlugin(plugin);
            return;
        }

        if (_viewer.IsVisible)
            _viewer.QuiescePreview();

        _viewer.UnloadPlugin();
        _currentPath = path;
        try
        {
            _viewer.BeginShow(
                plugin,
                path,
                (failedPath, error) => ShowFailed(failedPath, error, version));
        }
        catch
        {
            _viewer.UnloadPlugin();
            throw;
        }
    }

    internal void Close()
    {
        if (!_closed && !_disposed)
            _viewer.Close();
    }

    internal void RunAndClose()
    {
        if (!_closed && !_disposed)
            _viewer.RunAndClose();
    }

    internal void ToggleFullscreen()
    {
        if (!_closed && !_disposed)
            _viewer.ToggleFullscreen();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ++_showVersion;
        _owner.Closed -= Owner_Closed;
        _viewer.Closed -= Viewer_Closed;
        _manager.UnregisterDetachedPreview(this);

        if (!_closed)
        {
            _closed = true;
            _viewer.Close();
        }
    }

    private void ShowFailed(
        string failedPath,
        ExceptionDispatchInfo error,
        long version)
    {
        if (_disposed || _closed ||
            version != _showVersion ||
            !string.Equals(_currentPath, failedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ProcessHelper.WriteLog(error.SourceException.ToString());

        var failedPlugin = _viewer.Plugin?.GetType();
        var defaultPlugin = PluginManager.GetInstance().DefaultPlugin;
        if (failedPlugin != null && failedPlugin != defaultPlugin.GetType())
        {
            try
            {
                _viewer.UnloadPlugin();
                _viewer.BeginShow(
                    defaultPlugin,
                    failedPath,
                    (fallbackPath, fallbackError) =>
                        CloseAfterFailure(fallbackPath, fallbackError, version));
                return;
            }
            catch (Exception fallbackException)
            {
                ProcessHelper.WriteLog(fallbackException.ToString());
            }
        }

        ProcessHelper.WriteLog(
            $"Detached preview failed permanently for \"{failedPath}\".");
        Close();
    }

    private void CloseAfterFailure(
        string failedPath,
        ExceptionDispatchInfo error,
        long version)
    {
        if (_disposed || _closed || version != _showVersion)
            return;

        ProcessHelper.WriteLog(error.SourceException.ToString());
        ProcessHelper.WriteLog(
            $"Detached preview fallback failed for \"{failedPath}\".");
        Close();
    }

    private static void CleanupUnusedPlugin(IViewer plugin)
    {
        if (plugin == null)
            return;

        try
        {
            plugin.Cleanup();
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog(e.ToString());
        }
    }

    private void Owner_Closed(object sender, EventArgs e)
    {
        Close();
    }

    private void Viewer_Closed(object sender, EventArgs e)
    {
        if (_closed)
            return;

        _closed = true;
        ++_showVersion;
        _owner.Closed -= Owner_Closed;
        _viewer.Closed -= Viewer_Closed;
        _manager.UnregisterDetachedPreview(this);
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
