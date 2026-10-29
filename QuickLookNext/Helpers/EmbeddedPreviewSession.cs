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
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace QuickLookNext.Helpers;

/// <summary>
/// Runs a normal viewer for a host-owned panel such as an archive entry. It keeps
/// the plugin lifecycle separate from the main preview window so switching an
/// entry cannot interfere with the preview that owns the archive itself.
/// </summary>
internal sealed class EmbeddedPreviewSession : IEmbeddedPreviewSession
{
    private readonly string _path;
    private readonly object _source;
    private readonly object _sync = new();

    private ContextObject _context;
    private IViewer _plugin;
    private object _content;
    private object _pendingContent;
    private string _error;
    private bool _started;
    private bool _disposed;
    private bool _completed;

    internal EmbeddedPreviewSession(string path, object source)
    {
        Path = path;
        _path = path;
        _source = source;
    }

    public string Path { get; }

    public object ViewerContent
    {
        get
        {
            lock (_sync)
                return _content;
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_sync)
                return !_completed;
        }
    }

    public string Error
    {
        get
        {
            lock (_sync)
                return _error;
        }
    }

    public event EventHandler Changed;

    public async Task StartAsync()
    {
        lock (_sync)
        {
            if (_started || _disposed)
                return;

            _started = true;
        }

        IViewer plugin = null;
        try
        {
            plugin = await PluginManager.GetInstance().FindMatchAsync(_path).ConfigureAwait(true);

            if (IsDisposed)
            {
                CleanupUnusedPlugin(plugin);
                return;
            }

            await Dispatcher.InvokeAsync(() => BeginView(plugin));
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog(e.ToString());
            SetError("This file could not be previewed.");
            CleanupPlugin();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        if (Dispatcher.CheckAccess())
            CleanupPlugin();
        else
            Dispatcher.BeginInvoke(new Action(CleanupPlugin));
    }

    private bool IsDisposed
    {
        get
        {
            lock (_sync)
                return _disposed;
        }
    }

    private Dispatcher Dispatcher =>
        (_source as DispatcherObject)?.Dispatcher ??
        Application.Current?.Dispatcher ??
        System.Windows.Threading.Dispatcher.CurrentDispatcher;

    private void BeginView(IViewer plugin)
    {
        if (IsDisposed)
        {
            CleanupUnusedPlugin(plugin);
            return;
        }

        var context = new ContextObject
        {
            Source = _source,
            Theme = Themes.None,
            IsBusy = true,
        };

        context.PropertyChanged += Context_PropertyChanged;

        lock (_sync)
        {
            _plugin = plugin;
            _context = context;
        }

        try
        {
            plugin.Prepare(_path, context);

            if (context.IsBlocked)
            {
                context.ViewerContent = new System.Windows.Controls.TextBlock
                {
                    Text = "This file type is blocked.",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 14,
                };
                context.IsBusy = false;
                return;
            }

            context.IsBusy = true;
            plugin.View(_path, context);
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog(e.ToString());
            SetError("This file could not be previewed.");
            CleanupPlugin();
        }
    }

    private void Context_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _context))
            return;

        var changed = false;

        lock (_sync)
        {
            if (_disposed || sender != _context)
                return;

            switch (e.PropertyName)
            {
                case nameof(ContextObject.ViewerContent):
                    if (_context.ViewerContent != null &&
                        !ReferenceEquals(_content, _context.ViewerContent))
                    {
                        _content = _context.ViewerContent;
                        changed = true;
                    }
                    break;

                case nameof(ContextObject.PendingViewerContent):
                    _pendingContent = _context.PendingViewerContent;
                    if (_context.IsBusy)
                        break;

                    if (_pendingContent != null && !ReferenceEquals(_content, _pendingContent))
                    {
                        _content = _pendingContent;
                        changed = true;
                    }
                    break;

                case nameof(ContextObject.IsBusy):
                    if (_context.IsBusy)
                        break;

                    _completed = true;
                    changed = true;
                    if (_pendingContent != null && !ReferenceEquals(_content, _pendingContent))
                    {
                        _content = _pendingContent;
                    }
                    break;
            }
        }

        if (changed)
            PublishChanged();
    }

    private void SetError(string message)
    {
        lock (_sync)
        {
            _error = message;
            _completed = true;
        }

        PublishChanged();
    }

    private void PublishChanged()
    {
        if (IsDisposed)
            return;

        if (Dispatcher.CheckAccess())
            Changed?.Invoke(this, EventArgs.Empty);
        else
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsDisposed)
                    Changed?.Invoke(this, EventArgs.Empty);
            }));
    }

    private void CleanupUnusedPlugin(IViewer plugin)
    {
        if (plugin == null)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => CleanupUnusedPlugin(plugin)));
            return;
        }

        try
        {
            plugin.Cleanup();
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog(e.ToString());
        }
    }

    private void CleanupPlugin()
    {
        IViewer plugin;
        ContextObject context;

        lock (_sync)
        {
            plugin = _plugin;
            context = _context;
            _plugin = null;
            _context = null;
            _content = null;
            _pendingContent = null;
        }

        if (context != null)
            context.PropertyChanged -= Context_PropertyChanged;

        CleanupUnusedPlugin(plugin);
    }
}
