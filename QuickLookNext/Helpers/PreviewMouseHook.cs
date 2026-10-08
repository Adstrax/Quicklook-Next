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
using QuickLook.Common.NativeMethods;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace QuickLookNext.Helpers;

/// <summary>
/// v5.6.9: the low-level mouse hook, and the only place that reads the mouse's extra buttons.
///
/// <para>
/// It exists for two things Windows will not hand over on its own: the side buttons, because the
/// preview never takes focus and Windows would otherwise give them to the folder window, and the middle
/// button, because "preview the file I am pointing at" has to work before anything is being previewed.
/// That second reason is why the hook lives here and is installed at startup next to the keyboard hook
/// (see <see cref="KeystrokeDispatcher"/>): the preview window only exists once something has been
/// previewed, so a hook owned by it cannot see the press that would open the first preview.
/// </para>
///
/// <para>
/// The hook is called on the input thread of <em>the whole desktop</em>, so its body only decides: a
/// few field reads, and everything that talks to the shell is handed to a pool thread. An earlier
/// version did the shell work right there, which stalled every application's mouse and then hung the
/// app.
/// </para>
/// </summary>
internal class PreviewMouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    // The middle button is 0x0207-0x0209 (0x0201-0x0202 is the LEFT button, and taking those made
    // every left click in a folder window disappear).
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MBUTTONDBLCLK = 0x0209;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_XBUTTONDOWN = 0x020B;
    private const uint WM_XBUTTONUP = 0x020C;
    private const uint WM_MOUSEHWHEEL = 0x020E;

    /// <summary>How often the two settings are re-read, so editing OPTIONS.md does not need a restart.</summary>
    private const long SettingRefreshMs = 1000;

    private static PreviewMouseHook _instance;

    private nint _hook;
    private LowLevelMouseProc _proc;
    private bool _mouseButtonNavigation;
    private bool _middleClickPreviews;
    private long _settingsReadTick;

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public User32.POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private PreviewMouseHook()
    {
        Install();
    }

    internal static PreviewMouseHook GetInstance() => _instance ??= new PreviewMouseHook();

    internal bool IsInstalled => _hook != IntPtr.Zero;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _proc = null;
    }

    private void Install()
    {
        if (_hook != IntPtr.Zero)
            return;

        ReadSettings();

        _proc = HookProc;
        var hMod = Kernel32.LoadLibrary("user32.dll");
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
    }

    private void ReadSettings()
    {
        _settingsReadTick = Environment.TickCount64;
        _mouseButtonNavigation = SettingHelper.Get("MouseButtonNavigation", true, "QuickLookNext");
        _middleClickPreviews = SettingHelper.Get("MiddleClickPreviews", true, "QuickLookNext");
    }

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode < 0)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            var message = (uint)wParam.ToInt64();
            if (message is not (WM_MOUSEWHEEL or WM_MOUSEHWHEEL or
                WM_XBUTTONDOWN or WM_XBUTTONUP or
                WM_MBUTTONDOWN or WM_MBUTTONUP or WM_MBUTTONDBLCLK))
            {
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (Environment.TickCount64 - _settingsReadTick >= SettingRefreshMs)
                ReadSettings();

            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var manager = ViewWindowManager.GetInstance();

            if (message is WM_MOUSEWHEEL or WM_MOUSEHWHEEL && ViewerWindow.ShouldUseLayeredAcrylic())
            {
                if (TryGetWheelTarget(data.pt.X, data.pt.Y, out var targetHwnd))
                {
                    var delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    if (delta != 0)
                    {
                        var wp = (GetWheelKeyState() << 16) | ((uint)delta & 0xFFFF);
                        var lp = ((uint)(data.pt.Y & 0xFFFF) << 16) | ((uint)data.pt.X & 0xFFFF);
                        User32.PostMessage(targetHwnd, message, (nint)wp, (nint)lp);
                        return (nint)1; // consumed: the preview window is the only recipient
                    }
                }
            }

            // v5.6.7: the side buttons step through the folder while a preview is open. Nothing but the
            // decision happens here - the shell work and the log write go to a pool thread, and the
            // press is consumed so the folder window's own back/forward does not act as well.
            if (message is WM_XBUTTONDOWN or WM_XBUTTONUP)
            {
                var button = (data.mouseData >> 16) & 0xFFFF; // XBUTTON1 = back, XBUTTON2 = forward
                var enabled = _mouseButtonNavigation;

                // A preview has to be on screen for the buttons to be ours. The warm-up window is
                // shown off-screen and counts as "visible" for the whole session, so the test is the
                // file being previewed, not the window: taking the buttons for a parked window left
                // the folder window unable to navigate at all while nothing was being previewed.
                var previewed = manager.PreviewedPath;
                var showingPreview = !string.IsNullOrEmpty(previewed);

                // Where the press lands decides who owns it: the preview window itself, or the folder
                // window the preview was opened from. The second test is what stops the folder window
                // from navigating its own history when the cursor was over it instead of over the
                // preview ("it still controls the file manager").
                var underCursor = enabled && showingPreview
                    ? User32.WindowFromPoint(new User32.POINT(data.pt.X, data.pt.Y))
                    : IntPtr.Zero;
                var overPreview = enabled && showingPreview && IsPreviewWindow(underCursor);
                var overFolder = enabled && showingPreview && !overPreview && IsFolderWindow(underCursor);

                if (enabled && showingPreview && (overPreview || overFolder) && button is 1 or 2)
                {
                    if (message == WM_XBUTTONDOWN)
                    {
                        // The user asked for these to be crossed over: back walks forward through the
                        // folder, forward walks back.
                        var delta = button == 1 ? 1 : -1;
                        var landedOn = underCursor;
                        _ = Task.Run(() => StepToAdjacentFile(delta, previewed, landedOn));
                    }

                    return (nint)1; // consumed: this press belongs to the preview
                }

                // Queued diagnostic: a button press that reached the hook but was not acted on. One
                // line per press keeps a report answerable without costing the hook anything - it
                // cannot log, or do anything else slow, in place.
                if (message == WM_XBUTTONDOWN && showingPreview)
                {
                    var detail = $"button={button} overPreview={overPreview} overFolder={overFolder} enabled={enabled}";
                    var landedOn = underCursor;
                    _ = Task.Run(() => ProcessHelper.WriteLog($"Mouse button ignored: {detail} under={DescribeWindow(landedOn)}"));
                }
            }

            // v5.6.9: the middle button previews the file under the cursor - the mouse-only way to open
            // and close previews, next to the side buttons stepping through a folder. Both halves go to
            // a pool thread (resolving the item under the cursor talks to the shell) and the press is
            // consumed, so Explorer's own middle-click - a folder opening in a new tab, or the file
            // list panning - does not happen on top of it.
            if (message is WM_MBUTTONDOWN or WM_MBUTTONUP or WM_MBUTTONDBLCLK)
            {
                var enabled = _middleClickPreviews;
                var underCursor = enabled
                    ? User32.WindowFromPoint(new User32.POINT(data.pt.X, data.pt.Y))
                    : IntPtr.Zero;
                // Asking about the preview window creates its handle if it does not exist yet, so it is
                // only asked while a preview is actually showing - pressing the middle button must not
                // pull the window (and its rendering stack) in on a machine that has not previewed
                // anything yet.
                var overPreview = enabled
                    && !string.IsNullOrEmpty(manager.PreviewedPath)
                    && IsPreviewWindow(underCursor);
                var overFolder = enabled && !overPreview && IsFolderWindow(underCursor);

                if (overPreview || overFolder)
                {
                    if (message != WM_MBUTTONUP)
                    {
                        var x = data.pt.X;
                        var y = data.pt.Y;
                        var landedOn = underCursor;
                        var onPreviewWindow = overPreview;
                        var previewed = manager.PreviewedPath;
                        _ = Task.Run(() => HandleMiddleClick(x, y, landedOn, onPreviewWindow, previewed));
                    }

                    return (nint)1; // consumed: this press belongs to the preview
                }
            }
        }
        catch (Exception e)
        {
            // A hook that throws can take the process with it, and this one runs inside every
            // application's input path - so it never throws.
            Debug.WriteLine(e);
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// v5.6.7: the half that must not run in the hook. Runs on a pool thread (the same place the
    /// selection reader already uses shell COM from), so a slow shell call can never hold up input.
    /// </summary>
    private static void StepToAdjacentFile(int delta, string path, nint landedOn)
    {
        try
        {
            NativeMethods.QuickLookNext.TryMoveSelection(delta, path, out var report);
            // The window the press landed on is in the line on purpose: it says which of the two
            // ownership tests matched, so an issue report can be answered without a rebuild.
            ProcessHelper.WriteLog($"Mouse button: {report} under={DescribeWindow(landedOn)}");
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"Mouse button step failed: {e.Message}");
        }
    }

    /// <summary>
    /// v5.6.9: the mouse's middle button - "preview the file I am pointing at". Over the file list it
    /// previews the item under the cursor, and pressing it again on the file that is already being
    /// previewed closes the preview, the way Space does. Over the preview window itself it dismisses
    /// the preview. Runs on a pool thread because resolving the item goes to the shell; the preview
    /// itself is opened or closed on the UI thread.
    /// </summary>
    private static void HandleMiddleClick(int x, int y, nint landedOn, bool overPreviewWindow, string previewedPath)
    {
        try
        {
            var manager = ViewWindowManager.GetInstance();

            if (overPreviewWindow)
            {
                WriteMiddleClickLog("closing the preview");
                OnUiThread(() => manager.TogglePreview(previewedPath));
                return;
            }

            if (!NativeMethods.QuickLookNext.TryGetItemPathAt(x, y, landedOn, out var path, out var report))
            {
                WriteMiddleClickLog($"nothing to preview ({report})");
                return;
            }

            var closing = string.Equals(path, previewedPath, StringComparison.OrdinalIgnoreCase);
            WriteMiddleClickLog($"{(closing ? "closing" : "previewing")} \"{path}\" ({report})");

            // The preview comes first on purpose: closing stops the selection-follow, so the highlight
            // moved below cannot bring the preview straight back.
            OnUiThread(() =>
            {
                if (closing)
                    manager.ClosePreview();
                else
                    manager.InvokePreview(path);
            });

            if (!closing && NativeMethods.QuickLookNext.TrySelectFile(path, out var selection))
                WriteMiddleClickLog($"selection: {selection}");
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"Middle click failed: {e.Message}");
        }
    }

    private static void WriteMiddleClickLog(string detail) =>
        ProcessHelper.WriteLog($"Middle click: {detail}");

    /// <summary>
    /// Runs an action on the UI thread and waits for it. The caller is a pool thread, and the order
    /// matters there (the preview before the selection), so the call has to be synchronous.
    /// </summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    /// <summary>
    /// v1.3.5: the layered-window fallback never receives the WM_MOUSEWHEEL that Windows forwards to
    /// the window under the cursor, so the wheel is re-delivered to the preview window by hand. Only
    /// used while the preview is layered (see ShouldUseLayeredAcrylic).
    /// </summary>
    private static bool TryGetWheelTarget(int x, int y, out nint targetHwnd)
    {
        targetHwnd = IntPtr.Zero;

        var underCursor = User32.WindowFromPoint(new User32.POINT(x, y));
        if (underCursor == IntPtr.Zero || !IsPreviewWindow(underCursor))
            return false;

        targetHwnd = underCursor;
        return true;
    }

    /// <summary>
    /// v5.6.7: whether the window a press landed on is the preview - the window itself, one of its
    /// children (a WebView2 host, say), or a popup it owns (a tooltip over the preview).
    /// </summary>
    private static bool IsPreviewWindow(nint underCursor)
    {
        if (underCursor == IntPtr.Zero)
            return false;

        var hwnd = ViewWindowManager.GetInstance().PreviewWindowHandle;
        if (hwnd == IntPtr.Zero)
            return false;

        return underCursor == hwnd ||
               User32.GetAncestor(underCursor, User32.GA_ROOT) == hwnd ||
               User32.GetAncestor(underCursor, User32.GA_ROOTOWNER) == hwnd;
    }

    /// <summary>
    /// v5.6.7: whether the window a press landed on is a folder window (Explorer's file list, possibly
    /// inside a tab). With a preview open the side buttons belong to the preview, so a press over the
    /// folder window must not also navigate Explorer's own history.
    /// </summary>
    private static bool IsFolderWindow(nint underCursor)
    {
        if (underCursor == IntPtr.Zero)
            return false;

        var root = User32.GetAncestor(underCursor, User32.GA_ROOT);
        if (root == IntPtr.Zero)
            root = underCursor;

        return WindowClass(root) is "CabinetWClass" or "ExploreWClass" or "ShellTabWindowClass";
    }

    /// <summary>
    /// v5.6.7: names the window a press landed on, for the diagnostic line. It runs on a pool thread
    /// (a window can be gone by the time the line is written), so every part of it is allowed to fail.
    /// </summary>
    private static string DescribeWindow(nint hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "none";

        try
        {
            User32.GetWindowThreadProcessId(hwnd, out var processId);

            string process;
            try
            {
                process = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                process = "?";
            }

            return $"{WindowClass(hwnd)}/{process}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string WindowClass(nint hwnd)
    {
        var buffer = new StringBuilder(64);
        User32.GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static uint GetWheelKeyState()
    {
        uint flags = 0;
        if ((GetKeyState(0x11) & 0x8000) != 0) flags |= 0x0008; // MK_CONTROL
        if ((GetKeyState(0x10) & 0x8000) != 0) flags |= 0x0004; // MK_SHIFT
        if ((GetKeyState(0x12) & 0x8000) != 0) flags |= 0x0020; // MK_MENU
        return flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
