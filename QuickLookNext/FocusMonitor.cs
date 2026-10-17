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

using QuickLook.Common.NativeMethods;
using QuickLookNext.Helpers;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace QuickLookNext;

/// <summary>
/// v3.33.0: follows Explorer's selection while a preview is open.
/// <para>
/// This used to be a plain 500 ms polling loop, which meant every arrow-key move
/// in Explorer waited 0-500 ms (250 ms on average) before the preview even
/// started switching - by far the biggest cost of the most common interaction,
/// now that a warm preview itself only takes 80-160 ms.
/// </para>
/// <para>
/// Explorer raises EVENT_OBJECT_SELECTION* for its list view, so the selection is
/// read when that event arrives (debounced by 40 ms to coalesce selection
/// bursts), and a slow 1.5 s poll is kept as a fallback for shell views that do
/// not raise it (the desktop, third-party file managers).
/// </para>
/// </summary>
internal class FocusMonitor
{
    private static FocusMonitor _instance;

    private readonly object _sync = new();

    private volatile bool _isRunning;
    private readonly SelectionSwitchGate _switchGate = new();

    private nint _winEventHook;
    private User32.WinEventProc _winEventProc; // keep alive: the OS holds a raw pointer
    private DispatcherTimer _debounce;
    private CancellationTokenSource _pollCancellation;
    private int _pollGeneration;

    public bool IsRunning => _isRunning;

    public void Start()
    {
        CancellationTokenSource pollCancellation;

        lock (_sync)
        {
            if (_isRunning)
                return;

            _isRunning = true;
            _switchGate.Reset(GetSelectionSafe());

            // The WinEvent callback is delivered on the thread that installed the hook
            // (the UI thread, which has the message pump), so a DispatcherTimer is the
            // natural coalescer for the selection bursts Explorer produces.
            _debounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                // Long enough to coalesce the bursts Explorer produces while a
                // selection is being dragged, short enough to be invisible.
                Interval = TimeSpan.FromMilliseconds(25),
            };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                _ = Task.Run(CheckSelection);
            };

            _winEventProc = OnShellSelectionChanged;
            _winEventHook = User32.SetWinEventHook(
                User32.EVENT_OBJECT_SELECTION, User32.EVENT_OBJECT_SELECTIONWITHIN,
                IntPtr.Zero, _winEventProc, 0, 0,
                User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

            pollCancellation = new CancellationTokenSource();
            _pollCancellation = pollCancellation;
            var generation = ++_pollGeneration;
            _ = Task.Run(() => PollLoop(generation, pollCancellation));
        }
    }

    public void Stop()
    {
        CancellationTokenSource pollCancellation;
        DispatcherTimer timer;

        lock (_sync)
        {
            _isRunning = false;
            _pollGeneration++;
            _switchGate.InvalidateReads();

            pollCancellation = _pollCancellation;
            _pollCancellation = null;
            pollCancellation?.Cancel();

            if (_winEventHook != IntPtr.Zero)
            {
                User32.UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }

            _winEventProc = null;

            timer = _debounce;
            _debounce = null;
        }

        timer?.Stop();
    }

    /// <summary>
    /// Keep this callback cheap: Windows silently drops WinEvent hooks whose
    /// handler is slow. The window-type check is pure Win32 (no COM) and the
    /// selection read is deferred to the debounce timer.
    /// </summary>
    private void OnShellSelectionChanged(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        var timer = _debounce;
        if (!_isRunning || timer == null)
            return;

        if (NativeMethods.QuickLookNext.GetFocusedWindowType() ==
            NativeMethods.QuickLookNext.FocusedWindowType.Invalid)
            return;

        RestartSelectionCheck(timer);
    }

    /// <summary>
    /// v6.0.0: an arrow key asks the same coalesced selection read as Explorer's selection event.
    /// Reading the shell synchronously from the UI thread made several key releases queue stale
    /// preview requests behind one another. Restarting the existing timer keeps only the selection
    /// present after the last key press and lets <see cref="SelectionSwitchGate"/> retire older
    /// reads that finish late.
    /// </summary>
    internal bool RequestSelectionSwitch()
    {
        if (!_isRunning)
            return false;

        var timer = _debounce;
        if (timer == null)
            return false;

        try
        {
            if (timer.Dispatcher.CheckAccess())
            {
                RestartSelectionCheck(timer);
            }
            else
            {
                timer.Dispatcher.BeginInvoke(
                    new Action(() => RestartSelectionCheck(timer)),
                    DispatcherPriority.Input);
            }

            return true;
        }
        catch (Exception e)
        {
            // The preview may be closing while the keyboard hook is still delivering a key-up.
            Debug.WriteLine(e.Message);
            return false;
        }
    }

    private void RestartSelectionCheck(DispatcherTimer timer)
    {
        if (!_isRunning || !ReferenceEquals(_debounce, timer))
            return;

        timer.Stop();
        timer.Start();
    }

    private async Task PollLoop(int generation, CancellationTokenSource cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.5));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
            {
                if (!_isRunning ||
                    generation != Volatile.Read(ref _pollGeneration))
                {
                    break;
                }

                CheckSelection();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Expected when the preview closes.
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pollCancellation, cancellation))
                    _pollCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CheckSelection()
    {
        if (!_isRunning)
            return;

        var readVersion = _switchGate.BeginRead();
        var path = GetSelectionSafe();
        if (string.IsNullOrEmpty(path))
            return;

        if (!_isRunning)
            return;

        if (_switchGate.ShouldDispatch(
                readVersion, path, ViewWindowManager.GetInstance().PreviewedPath,
                Environment.TickCount64))
        {
            PipeServerManager.SendMessage(PipeMessages.Switch, path);
        }
    }

    /// <summary>
    /// The mouse already switched to this path directly. Treat Explorer's selection event as an
    /// echo for a short time instead of sending a second switch request.
    /// </summary>
    internal void MarkExternalSwitch(string path)
    {
        _switchGate.MarkExternalSwitch(path, Environment.TickCount64);
    }

    private static string GetSelectionSafe()
    {
        try
        {
            return NativeMethods.QuickLookNext.GetCurrentSelection() ?? string.Empty;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            return string.Empty;
        }
    }

    internal static FocusMonitor GetInstance()
    {
        return _instance ??= new FocusMonitor();
    }
}
