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
using System;
using System.Threading.Tasks;

namespace QuickLook.Plugin.Shared;

/// <summary>
/// v6.1.2: one WebView2 environment is shared by every default web preview.
/// <para>
/// A pooled <see cref="Microsoft.Web.WebView2.Wpf.WebView2"/> removes controller creation from the
/// repeat path, but initializing a new control still used to create its own
/// <see cref="CoreWebView2Environment"/> first - several hundred milliseconds on
/// the first Markdown or Office preview. Creating the environment through one
/// single-flight task keeps concurrent previews on the same browser/process
/// group and lets later previews skip that work.
/// </para>
/// <para>
/// Controls that need their own Chromium switches keep the old path: an
/// environment's options are process-wide, so a force-dark control must not
/// silently inherit a default environment. The cache is forgotten whenever the
/// browser is reaped or a failed initialization asks for a clean profile.
/// </para>
/// </summary>
internal static class WebView2EnvironmentCache
{
    private static readonly SingleFlightCache<CoreWebView2Environment> Default =
        new(CreateDefault);

    /// <summary>
    /// Starts creating the default environment without making the caller wait.
    /// Used when a pooled control is created and when the first navigation needs
    /// the controller, so environment setup overlaps the page work.
    /// </summary>
    internal static void Prepare()
    {
        Observe(Default.GetOrStart());
    }

    internal static Task<CoreWebView2Environment> GetDefaultAsync()
        => Default.GetOrStart();

    /// <summary>
    /// Drops the environment after the browser process group is gone, or after
    /// initialization failed and the recovery is about to repair/replace the
    /// profile. A later preview creates a fresh one.
    /// </summary>
    internal static void Forget()
        => Default.Forget();

    private static Task<CoreWebView2Environment> CreateDefault()
        => CoreWebView2Environment.CreateAsync(
            null,
            WebView2EnvironmentProvider.UserDataFolder,
            new CoreWebView2EnvironmentOptions());

    private static void Observe(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }
}

/// <summary>
/// Starts one asynchronous operation and hands the same task to every caller
/// until it finishes. A faulted or cancelled operation is not cached: the next
/// caller gets a fresh attempt, which is what the WebView2 recovery path needs.
/// </summary>
internal sealed class SingleFlightCache<T> where T : class
{
    private readonly Func<Task<T>> _factory;
    private readonly object _sync = new();
    private Task<T> _task;

    internal SingleFlightCache(Func<Task<T>> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    internal Task<T> GetOrStart()
    {
        lock (_sync)
        {
            if (_task is { IsFaulted: false, IsCanceled: false })
                return _task;

            try
            {
                _task = _factory() ??
                    Task.FromException<T>(new InvalidOperationException("The factory returned no task."));
            }
            catch (Exception e)
            {
                _task = Task.FromException<T>(e);
            }

            return _task;
        }
    }

    internal void Forget()
    {
        lock (_sync)
            _task = null;
    }
}
