// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// QuickLookNext Lite: managed implementation of the shell integration that the full
// QuickLookNext performed through the native QuickLookNext.Native32/64/arm64 DLLs.
// Covers Explorer and Desktop (the space-key path). No C++/ATL toolchain required.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

namespace QuickLookNext.NativeMethods;

internal static class QuickLookNext
{
    private const int S_OK = 0;
    private const int MaxPath = 32767;
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const int SVGIO_SELECTION = 0x1;
    private const uint SVGIO_ALLVIEW = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // Selection flags for IFolderView::SelectItem.
    private const uint SVSI_SELECT = 0x1;
    private const uint SVSI_DESELECTOTHERS = 0x4;
    private const uint SVSI_ENSUREVISIBLE = 0x8;
    private const uint SVSI_FOCUSED = 0x10;

    // SHGetNameFromIDList: the display name of an item, without going through the data object.
    private const int SIGDN_NORMALDISPLAY = 0x0;

    /// <summary>
    /// v5.6.7: "parent relative parsing" - the item's own name, extension included. The display name
    /// above is what Explorer shows, and Explorer hides known extensions by default, so "photo.jpg"
    /// comes back as "photo" and never matches the file being previewed.
    /// </summary>
    private const int SIGDN_PARENTRELATIVEPARSING = unchecked((int)0x80018001);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetNameFromIDList(IntPtr pidl, int sigdnName, out IntPtr ppszName);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    /// <summary>
    /// v5.6.6: asks Explorer to move its selection by <paramref name="delta"/> items - what the
    /// preview does when the mouse's back/forward buttons are pressed over it.
    ///
    /// <para>
    /// Moving Explorer's selection rather than switching the preview directly keeps the two in
    /// step: the highlight in the folder window follows, and the preview follows the selection
    /// through the same path the arrow keys already use. The Shell does not wrap around, so
    /// stepping past either end does nothing.
    /// </para>
    /// </summary>
    /// <summary>
    /// v5.6.7: asks Explorer to move its selection by <paramref name="delta"/> items - what the
    /// preview does when the mouse's back/forward buttons are pressed over it.
    ///
    /// <para>
    /// Moving Explorer's selection rather than switching the preview directly keeps the two in step:
    /// the highlight in the folder window follows, and the preview follows the selection through the
    /// same path the arrow keys already use.
    /// </para>
    ///
    /// <para>
    /// <paramref name="currentFile"/> is how the right folder window is found. The first pass only
    /// accepts a window whose *focused* item is that file - which is the window the preview came from
    /// - and only if none matches does the second pass look through every window's items by name. The
    /// foreground window is not relied on: it is the folder window only while the user is in it, and
    /// the preview is under the cursor when these buttons are pressed.
    /// </para>
    /// </summary>
    internal static bool TryMoveSelection(int delta, string currentFile, out string report)
    {
        report = string.Empty;

        if (delta == 0 || string.IsNullOrEmpty(currentFile))
        {
            report = "no file to step from";
            return false;
        }

        var name = Path.GetFileName(currentFile);
        var browsers = FindShellBrowsers(out var searched);
        try
        {
            foreach (var browser in browsers)
            {
                // Fast path: the window whose focused item is the file being previewed.
                if (TryMoveInView(browser, delta, name, mustMatchFocus: true))
                {
                    report = $"stepped to the {(delta < 0 ? "previous" : "next")} file";
                    return true;
                }
            }

            foreach (var browser in browsers)
            {
                if (TryMoveInView(browser, delta, name, mustMatchFocus: false))
                {
                    report = $"stepped to the {(delta < 0 ? "previous" : "next")} file (by name)";
                    return true;
                }
            }

            report = $"nothing to step to (windows={browsers.Count}, considered={searched}, file={name})";
            return false;
        }
        catch (Exception e)
        {
            report = $"step failed: {e.Message}";
            Debug.WriteLine(e);
            return false;
        }
        finally
        {
            foreach (var browser in browsers)
                Marshal.ReleaseComObject(browser);
        }
    }

    /// <summary>
    /// Moves the selection inside one folder view. <paramref name="mustMatchFocus"/> restricts it to
    /// views whose focused item is <paramref name="name"/>, which is what makes the first pass pick
    /// the right window instead of a random one showing a file with the same name.
    /// </summary>
    private static bool TryMoveInView(IShellBrowser browser, int delta, string name, bool mustMatchFocus)
    {
        IntPtr psvPtr = IntPtr.Zero;
        IShellView view = null;

        try
        {
            if (browser.QueryActiveShellView(out psvPtr) != S_OK || psvPtr == IntPtr.Zero)
                return false;

            view = (IShellView)Marshal.GetObjectForIUnknown(psvPtr);
            if (view is not IFolderView folderView)
                return false;

            if (folderView.ItemCount(SVGIO_ALLVIEW, out var count) != S_OK || count <= 0)
                return false;

            var index = -1;
            if (folderView.GetFocusedItem(out var focused) == S_OK && focused >= 0 && focused < count &&
                NameMatches(ItemName(folderView, focused), name))
            {
                index = focused;
            }
            else if (mustMatchFocus)
            {
                return false;
            }
            else
            {
                // An exact name first, then the display name (which Explorer returns without the
                // extension) - a folder holding both "photo.jpg" and "photo.png" must not be guessed.
                for (var i = 0; i < count; i++)
                {
                    var itemName = ItemName(folderView, i);
                    if (string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        if (NameMatches(ItemName(folderView, i), name))
                        {
                            index = i;
                            break;
                        }
                    }
                }
            }

            if (index < 0)
                return false;

            var target = index + delta;
            if (target < 0 || target >= count)
                return false;

            return folderView.SelectItem(target,
                SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED | SVSI_ENSUREVISIBLE) == S_OK;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return false;
        }
        finally
        {
            if (view != null)
                Marshal.ReleaseComObject(view);
        }
    }

    /// <summary>
    /// The item's name, with its extension when the shell can give one (the display name drops it
    /// when Explorer is set to hide known extensions, which is the default).
    /// </summary>
    private static string ItemName(IFolderView folderView, int index)
    {
        if (folderView.Item(index, out var pidl) != S_OK || pidl == IntPtr.Zero)
            return string.Empty;

        try
        {
            foreach (var kind in new[] { SIGDN_PARENTRELATIVEPARSING, SIGDN_NORMALDISPLAY })
            {
                if (SHGetNameFromIDList(pidl, kind, out var namePtr) == S_OK && namePtr != IntPtr.Zero)
                {
                    var value = Marshal.PtrToStringUni(namePtr);
                    if (!string.IsNullOrEmpty(value))
                        return value;
                }
            }

            return string.Empty;
        }
        finally
        {
            ILFree(pidl);
        }
    }

    /// <summary>
    /// Whether an item is the file being previewed. The second test covers the display name the shell
    /// gives when Explorer hides extensions ("photo" for "photo.jpg").
    /// </summary>
    private static bool NameMatches(string itemName, string fileName)
    {
        if (string.IsNullOrEmpty(itemName))
            return false;

        if (string.Equals(itemName, fileName, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(
            Path.GetFileNameWithoutExtension(itemName),
            Path.GetFileNameWithoutExtension(fileName),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every folder window's shell browser, foreground first. The order matters: the first pass
    /// wants the window the user is working in.
    /// </summary>
    private static List<IShellBrowser> FindShellBrowsers(out int searched)
    {
        var browsers = new List<IShellBrowser>();
        searched = 0;

        var shellWindowsType = Type.GetTypeFromCLSID(CLSID_ShellWindows);
        if (shellWindowsType == null)
            return browsers;

        object shellWindows = Activator.CreateInstance(shellWindowsType);
        try
        {
            dynamic windows = shellWindows;
            var count = (int)windows.Count;
            var foreground = GetForegroundWindow();
            var tabWindow = FindWindowEx(foreground, IntPtr.Zero, "ShellTabWindowClass", null);

            for (var i = 0; i < count; i++)
            {
                try
                {
                    var dispObj = windows.Item(i);
                    if (dispObj == null)
                        continue;

                    var provider = (IServiceProvider)dispObj;
                    var browserIid = IID_IShellBrowser;
                    if (provider.QueryService(ref browserIid, ref browserIid, out var sbPtr) != S_OK)
                        continue;

                    var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(sbPtr);
                    searched++;

                    if (browser.GetWindow(out var phwnd) == S_OK &&
                        (phwnd == foreground || (tabWindow != IntPtr.Zero && tabWindow == phwnd)))
                    {
                        browsers.Insert(0, browser);
                    }
                    else
                    {
                        browsers.Add(browser);
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e);
                }
            }

            return browsers;
        }
        finally
        {
            Marshal.ReleaseComObject(shellWindows);
        }
    }
    private const short CF_HDROP = 15;
    private const int TYMED_HGLOBAL = 1;
    private const int DVASPECT_CONTENT = 1;

    private static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IID_IServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    internal enum FocusedWindowType
    {
        Invalid,
        Desktop,
        Explorer,
        Dialog,
        Everything,
        DOpus,
        MultiCommander,
        IDM,
        FilePilot,
        DeskBox,
    }

    internal static void Init()
    {
        // Lite build: shell integration is fully managed; nothing to initialize.
    }

    internal static FocusedWindowType GetFocusedWindowType()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return FocusedWindowType.Invalid;

        if (IsCursorActivated(hwnd))
            return FocusedWindowType.Invalid;

        var cls = GetClassNameString(hwnd);

        // Desktop
        if (cls is "WorkerW" or "Progman")
        {
            if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                return FocusedWindowType.Desktop;
        }

        // File Explorer
        if (cls is "ExploreWClass" or "CabinetWClass")
        {
            if (!IsExplorerSearchBoxFocused())
                return FocusedWindowType.Explorer;
        }

        // Open/save dialogs
        if (cls == "#32770")
        {
            if (FindWindowEx(hwnd, IntPtr.Zero, "DUIViewWndClassName", null) != IntPtr.Zero
                && !IsExplorerSearchBoxFocused())
                return FocusedWindowType.Dialog;
        }

        return FocusedWindowType.Invalid;
    }

    // Shell COM (SHDocVw/ShellWindows) must run on an STA thread. Reuse a single
    // dedicated worker instead of creating and joining a new thread on every
    // call (FocusMonitor polls selection every 500 ms). No WPF Dispatcher here:
    // a plain STA worker + blocking queue matches the previously working
    // thread-per-call behaviour most closely.
    private static readonly BlockingCollection<SelectionRequest> SelectionQueue = [];
    private static readonly Thread SelectionWorker = CreateSelectionWorker();

    private sealed class SelectionRequest
    {
        public string Result;
        public readonly ManualResetEventSlim Done = new(false);
    }

    private static Thread CreateSelectionWorker()
    {
        var thread = new Thread(SelectionWorkerLoop)
        {
            IsBackground = true,
            Name = "QuickLookNext.ShellSelection",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private static void SelectionWorkerLoop()
    {
        foreach (var request in SelectionQueue.GetConsumingEnumerable())
        {
            try
            {
                request.Result = ReadSelectionCore();
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                request.Result = string.Empty;
            }
            finally
            {
                request.Done.Set();
            }
        }
    }

    internal static string GetCurrentSelection()
    {
        var request = new SelectionRequest();
        SelectionQueue.Add(request);
        request.Done.Wait();

        var result = request.Result ?? string.Empty;
        if (result.Length > 2 && result[0] == '"' && result[^1] == '"')
        {
            result = result.Substring(1, result.Length - 2);
        }

        return ResolveShortcut(result);
    }

    private static string ReadSelectionCore()
    {
        return GetFocusedWindowType() switch
        {
            FocusedWindowType.Desktop => GetSelectionFromDesktop(),
            FocusedWindowType.Explorer => GetSelectionFromExplorer(),
            _ => string.Empty,
        };
    }

    private static string GetSelectionFromExplorer()
    {
        var shellWindowsType = Type.GetTypeFromCLSID(CLSID_ShellWindows);
        if (shellWindowsType == null)
            return string.Empty;

        dynamic shellWindows = Activator.CreateInstance(shellWindowsType);
        try
        {
            var count = (int)shellWindows.Count;

            var foreground = GetForegroundWindow();
            var tabWindow = FindWindowEx(foreground, IntPtr.Zero, "ShellTabWindowClass", null);

            for (var i = 0; i < count; i++)
            {
                try
                {
                    object dispObj = shellWindows.Item(i);
                    if (dispObj == null)
                        continue;

                    var provider = (IServiceProvider)dispObj;
                    var browserIid = IID_IShellBrowser;
                    if (provider.QueryService(ref browserIid, ref browserIid, out var sbPtr) != S_OK)
                        continue;

                    var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(sbPtr);
                    try
                    {
                        if (browser.GetWindow(out var phwnd) != S_OK)
                            continue;

                        if (phwnd == foreground || (tabWindow != IntPtr.Zero && tabWindow == phwnd))
                        {
                            var selection = GetSelectedInternal(browser);
                            if (!string.IsNullOrEmpty(selection))
                                return selection;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(browser);
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e);
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellWindows);
        }

        return string.Empty;
    }

    private static string GetSelectionFromDesktop()
    {
        // v1.2.6: get the desktop folder view via IShellWindows.FindWindowSW
        // (the same route the original native code used). VARIANT-byref params
        // are passed as raw IntPtr pointing at manually allocated VT_EMPTY
        // VARIANTs; the interface must be declared as dual for the vtable to be
        // aligned (InterfaceIsIUnknown crashes on .NET Core here).
        var selection = GetSelectionFromDesktopViaShell();
        if (!string.IsNullOrEmpty(selection))
            return selection;

        // Fallback: read the desktop list view directly.
        return GetSelectionFromDesktopListView();
    }

    private static string GetSelectionFromDesktopViaShell()
    {
        var shellWindows = (IShellWindows)Activator.CreateInstance(
            Type.GetTypeFromCLSID(CLSID_ShellWindows));

        var emptyVariant = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(VARIANT)));
        try
        {
            // vt = VT_EMPTY, everything else zero.
            Marshal.WriteInt16(emptyVariant, 0, 0);
            Marshal.WriteInt16(emptyVariant, 2, 0);
            Marshal.WriteInt16(emptyVariant, 4, 0);
            Marshal.WriteInt16(emptyVariant, 6, 0);
            Marshal.WriteIntPtr(emptyVariant + 8, IntPtr.Zero);
            Marshal.WriteIntPtr(emptyVariant + 16, IntPtr.Zero);

            var hr = shellWindows.FindWindowSW(emptyVariant, emptyVariant,
                SWC_DESKTOP, out var hwnd, SWFO_NEEDDISPATCH, out var dispPtr);
            if (hr != S_OK || dispPtr == IntPtr.Zero)
                return string.Empty;

            var disp = Marshal.GetObjectForIUnknown(dispPtr);
            try
            {
                var provider = (IServiceProvider)disp;
                var browserIid = IID_IShellBrowser;
                if (provider.QueryService(ref browserIid, ref browserIid, out var sbPtr) != S_OK)
                    return string.Empty;

                var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(sbPtr);
                try
                {
                    return GetSelectedInternal(browser);
                }
                finally
                {
                    Marshal.ReleaseComObject(browser);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(disp);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(emptyVariant);
        }
    }

    private static string GetSelectionFromDesktopListView()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            progman = FindWindow("WorkerW", null);

        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
            return string.Empty;

        var listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        if (listView == IntPtr.Zero)
            return string.Empty;

        var selectedIndex = SendMessage(listView, LVM_GETNEXTITEM, (IntPtr)(-1), (IntPtr)LVNI_SELECTED);
        if (selectedIndex.ToInt64() < 0)
            return string.Empty;

        var buffer = new StringBuilder(MaxPath);
        var item = new LVITEM
        {
            iSubItem = 0,
            cchTextMax = buffer.Capacity,
            pszText = buffer,
        };
        SendMessage(listView, LVM_GETITEMTEXT, selectedIndex, ref item);

        var name = buffer.ToString();
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var fullPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);

        return File.Exists(fullPath) || Directory.Exists(fullPath)
            ? ResolveShortcut(fullPath)
            : fullPath;
    }

    private static string GetSelectedInternal(IShellBrowser browser)
    {
        var hrShellView = browser.QueryActiveShellView(out var psvPtr);
        if (hrShellView != S_OK || psvPtr == IntPtr.Zero)
            return string.Empty;

        var view = (IShellView)Marshal.GetObjectForIUnknown(psvPtr);
        try
        {
            var dataObjectIid = IID_IDataObject;
            var hrItemObject = view.GetItemObject(SVGIO_SELECTION, ref dataObjectIid, out var daoPtr);
            if (hrItemObject != S_OK || daoPtr == IntPtr.Zero)
                return string.Empty;

            var dataObject = (IDataObject)Marshal.GetObjectForIUnknown(daoPtr);
            try
            {
                return ReadFirstFileFromDataObject(dataObject);
            }
            finally
            {
                Marshal.ReleaseComObject(dataObject);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(view);
        }
    }

    private static string ReadFirstFileFromDataObject(IDataObject dataObject)
    {
        var format = new FORMATETC
        {
            cfFormat = CF_HDROP,
            ptd = IntPtr.Zero,
            dwAspect = (DVASPECT)DVASPECT_CONTENT,
            lindex = -1,
            tymed = (TYMED)TYMED_HGLOBAL,
        };

        STGMEDIUM medium;
        try
        {
            dataObject.GetData(ref format, out medium);
        }
        catch (Exception)
        {
            return string.Empty;
        }

        try
        {
            if ((int)medium.tymed != TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero)
                return string.Empty;

            var fileCount = DragQueryFile(medium.unionmember, 0xFFFFFFFF, null, 0);
            if (fileCount < 1)
                return string.Empty;

            var path = new StringBuilder(MaxPath);
            if (DragQueryFile(medium.unionmember, 0, path, path.Capacity) <= 0)
                return string.Empty;

            var longPath = new StringBuilder(MaxPath);
            GetLongPathName(path.ToString(), longPath, longPath.Capacity);
            return longPath.ToString();
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static string ResolveShortcut(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
            return path;

        var link = new ShellLink();
        ((IPersistFile)link).Load(path, 0);
        var sb = new StringBuilder(MaxPath);
        ((IShellLinkW)link).GetPath(sb, sb.Capacity, out _, 0);

        return sb.Length == 0 ? path : sb.ToString();
    }

    // ---- helpers ----------------------------------------------------------

    private static bool IsCursorActivated(IntPtr hwnd)
    {
        var threadId = GetWindowThreadProcessId(hwnd, out _);

        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, ref info))
            return false;

        return info.flags != 0 || info.hwndCaret != IntPtr.Zero;
    }

    private static bool IsExplorerSearchBoxFocused()
    {
        var hwnd = GetFocusedControl();
        if (hwnd == IntPtr.Zero)
            return false;

        return GetClassNameString(hwnd) == "Windows.UI.Core.CoreWindow";
    }

    private static IntPtr GetFocusedControl()
    {
        var threadId = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        if (threadId == 0 || !AttachThreadInput(GetCurrentThreadId(), threadId, true))
            return IntPtr.Zero;

        try
        {
            return GetFocus();
        }
        finally
        {
            AttachThreadInput(GetCurrentThreadId(), threadId, false);
        }
    }

    private static string GetClassNameString(IntPtr hwnd)
    {
        var buffer = new char[256];
        if (GetClassName(hwnd, buffer, buffer.Length) <= 0)
            return string.Empty;
        return new string(buffer).TrimEnd('\0');
    }

    // ---- P/Invoke ---------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetLongPathName(string lpszShortPath, StringBuilder lpszLongPath, int cchBuffer);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, int cch);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

    private const uint LVM_GETNEXTITEM = 0x100C;
    private const uint LVM_GETITEMTEXT = 0x1073;
    private const int LVNI_SELECTED = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, ref LVITEM lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LVITEM
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public StringBuilder pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr data1;
        public IntPtr data2;
    }

    // ---- Shell COM interfaces ---------------------------------------------

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        // IShellWindows is a dual interface. The methods are declared in vtable
        // order (IUnknown + IDispatch are implicit for dual), and VARIANT-byref
        // parameters are raw IntPtr to avoid .NET Core's broken marshaling.
        [DispId(1), PreserveSig] int get_Count(out int count);

        [DispId(2), PreserveSig]
        int Item([In, MarshalAs(UnmanagedType.Struct)] object index,
            [Out, MarshalAs(UnmanagedType.IDispatch)] out object pDisp);

        [DispId(3), PreserveSig] int _NewEnum(out IntPtr ppunk);

        [DispId(4), PreserveSig]
        int Register([In, MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass,
            out int plCookie);

        [DispId(5), PreserveSig]
        int RegisterPending(int lThreadId, IntPtr pvarloc, IntPtr pvarlocRoot, int swClass,
            out int plCookie);

        [DispId(6), PreserveSig] int Revoke(int lCookie);

        [DispId(7), PreserveSig] int OnNavigate(int lCookie, IntPtr pvarLoc);

        [DispId(8), PreserveSig] int OnActivated(int lCookie, IntPtr fActive);

        [DispId(9), PreserveSig]
        int FindWindowSW(IntPtr pvarLoc, IntPtr pvarLocRoot, int swClass, out int phwnd,
            int swfwOptions, out IntPtr ppdispOut);

        [DispId(10), PreserveSig]
        int OnCreated(int lCookie, [In, MarshalAs(UnmanagedType.IUnknown)] object punk);

        [DispId(11), PreserveSig] int ProcessAttachDetach(int fAttach);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out IntPtr phwnd);

        [PreserveSig] int ContextSensitiveHelp(int fEnterMode);

        // IShellBrowser
        [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);

        [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenu, IntPtr hwndActiveObject);

        [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);

        [PreserveSig] int SetStatusTextSB(IntPtr pszStatusText);

        [PreserveSig] int EnableModelessSB(int fEnable);

        [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);

        [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);

        [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppstm);

        [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);

        [PreserveSig]
        int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);

        [PreserveSig] int QueryActiveShellView(out IntPtr ppshv);

        [PreserveSig] int OnViewWindowActive(IntPtr pshv);

        [PreserveSig] int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out IntPtr phwnd);

        [PreserveSig] int ContextSensitiveHelp(int fEnterMode);

        // IShellView
        [PreserveSig] int TranslateAccelerator(IntPtr pmsg);

        [PreserveSig] int EnableModeless(int fEnable);

        [PreserveSig] int UIActivate(uint uState);

        [PreserveSig] int Refresh();

        [PreserveSig]
        int CreateViewWindow(IntPtr psb, IntPtr psvPrev, uint dwFlags, IntPtr prcView, out IntPtr phwnd);

        [PreserveSig] int DestroyViewWindow();

        [PreserveSig] int GetCurrentInfo(out IntPtr pvi);

        [PreserveSig] int AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);

        [PreserveSig] int SaveViewState();

        [PreserveSig] int SelectItem(IntPtr pidlItem, uint uFlags);

        [PreserveSig] int GetItemObject(uint uItem, ref Guid riid, out IntPtr ppv);
    }

    /// <summary>
    /// v5.6.6: the view of a folder as Explorer shows it - the item order here is the order on
    /// screen (the user's sort included), which is what "the next file" has to mean. The
    /// declarations, and the fact that an <see cref="IShellView"/> can be queried for this
    /// interface, come from Scripts/probe-explorer-selection.ps1, which validated both.
    /// </summary>
    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);

        [PreserveSig] int SetCurrentViewMode(uint viewMode);

        [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);

        [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);

        [PreserveSig] int ItemCount(uint uFlags, out int pcItems);

        [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);

        [PreserveSig] int GetSelectionMarkedItem(out int piItem);

        [PreserveSig] int GetFocusedItem(out int piItem);

        [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);

        [PreserveSig] int GetSpacing(out POINT ppt);

        [PreserveSig] int GetDefaultSpacing(out POINT ppt);

        [PreserveSig] int GetAutoArrange();

        [PreserveSig] int SelectItem(int iItemIndex, uint dwFlags);

        [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
    }
}
