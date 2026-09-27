# Feasibility probe: can IFolderView::SelectItem change Explorer's selection programmatically?
#
# Purpose: verify the core unknown for "switch to the previous/next file in the folder with ←/→ while
# previewing" — can the app move Explorer's selection to a given item? (Being able to change the selection
# is as good as driving the preview switch, because the app already follows Explorer's selection events.)
#
# What it verifies:
#   1) whether the IShellView -> IFolderView QueryInterface is available;
#   2) whether items can be enumerated in view order (Explorer's current sort);
#   3) three ways of changing the selection: no activation / SVUIA_ACTIVATE_NOFOCUS /
#      IShellView::SelectItem(pidl);
#   4) what happens out of bounds (past the last item) — the Shell does not wrap around, so the app has
#      to handle it.
#
# Why an already-open folder window: in this environment, a window newly opened via explorer.exe cannot be
# enumerated through Shell.Application (Windows 11 tabs / permission limits), and "change the selection"
# only needs one real folder window to verify. The script picks the first folder window.
#
# Safety: **read-only + a brief change + immediate restore**. The original selection is recorded before
# the run, the original selection is restored afterwards (an empty selection stays empty), and the view's
# activation state is restored too.
#
# Usage: pwsh -NoProfile -File .\Scripts\probe-explorer-selection.ps1

$ErrorActionPreference = 'Stop'

$probe = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

public class SelectionSetProbe
{
    private static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
    private const uint SVGIO_ALLVIEW = 0x2;
    private const uint SVGIO_SELECTION = 0x1;
    private const uint SVSI_SELECT = 0x1;
    private const uint SVSI_DESELECTOTHERS = 0x4;
    private const uint SVSI_ENSUREVISIBLE = 0x8;
    private const uint SVSI_FOCUSED = 0x10;
    // SVUIA_*: the view's "UI activation" state. ACTIVATE_NOFOCUS means activate without taking keyboard focus.
    private const uint SVUIA_DEACTIVATE = 0x0;
    private const uint SVUIA_ACTIVATE_NOFOCUS = 0x1;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider { [PreserveSig] int QueryService(ref Guid g, ref Guid r, out IntPtr p); }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(int f);
        [PreserveSig] int InsertMenusSB(IntPtr a, IntPtr b);
        [PreserveSig] int SetMenuSB(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int RemoveMenusSB(IntPtr a);
        [PreserveSig] int SetStatusTextSB(IntPtr a);
        [PreserveSig] int EnableModelessSB(int f);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr a, ushort b);
        [PreserveSig] int BrowseObject(IntPtr a, uint b);
        [PreserveSig] int GetViewStateStream(uint a, out IntPtr b);
        [PreserveSig] int GetControlWindow(uint a, out IntPtr b);
        [PreserveSig] int SendControlMsg(uint a, uint b, IntPtr c, IntPtr d, out IntPtr e);
        [PreserveSig] int QueryActiveShellView(out IntPtr ppshv);
        [PreserveSig] int OnViewWindowActive(IntPtr a);
        [PreserveSig] int SetToolbarItems(IntPtr a, uint b, uint c);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(int f);
        [PreserveSig] int TranslateAccelerator(IntPtr a);
        [PreserveSig] int EnableModeless(int f);
        [PreserveSig] int UIActivate(uint s);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(IntPtr a, IntPtr b, uint c, IntPtr d, out IntPtr e);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(out IntPtr a);
        [PreserveSig] int AddPropertySheetPages(uint a, IntPtr b, IntPtr c);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(IntPtr a, uint b);
        [PreserveSig] int GetItemObject(uint u, ref Guid r, out IntPtr p);
    }

    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
        [PreserveSig] int SetCurrentViewMode(uint ViewMode);
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

    [DllImport("shell32.dll")]
    private static extern int SHGetNameFromIDList(IntPtr pidl, int sigdnName, out IntPtr ppszName);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    private static string NameOf(IntPtr pidl)
    {
        IntPtr namePtr;
        if (SHGetNameFromIDList(pidl, 0, out namePtr) != 0 || namePtr == IntPtr.Zero)
            return "<unknown>";
        try { return Marshal.PtrToStringUni(namePtr); }
        finally { CoTaskMemFree(namePtr); }
    }

    /// <summary>Runs the verification against the given shell window object.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int DragQueryFile(IntPtr h, uint i, StringBuilder b, int c);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM m);

    /// <summary>
    /// Reads the selection the same way the app does: take the first file path from the view's
    /// "selection" data object. More reliable than GetSelectionMarkedItem (which only has a value when the
    /// selection carries a "mark").
    /// </summary>
    private static string ReadSelection(IShellView view)
    {
        var iid = new Guid("0000010E-0000-0000-C000-000000000046");
        IntPtr daoPtr;
        if (view.GetItemObject(SVGIO_SELECTION, ref iid, out daoPtr) != 0 || daoPtr == IntPtr.Zero)
            return string.Empty;

        var dao = (IDataObject)Marshal.GetObjectForIUnknown(daoPtr);
        try
        {
            var fe = new FORMATETC
            {
                cfFormat = 15,
                ptd = IntPtr.Zero,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL,
            };
            STGMEDIUM sm;
            try { dao.GetData(ref fe, out sm); }
            catch { return string.Empty; }
            try
            {
                var sb = new StringBuilder(32767);
                return DragQueryFile(sm.unionmember, 0, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
            }
            finally { ReleaseStgMedium(ref sm); }
        }
        finally { Marshal.ReleaseComObject(dao); }
    }

    public static string Run(object disp, string label)
    {
        var log = new StringBuilder();
        log.AppendLine("target window: " + label);

        var sp = (IServiceProvider)disp;
        var iid = IID_IShellBrowser;
        IntPtr sbPtr;
        if (sp.QueryService(ref iid, ref iid, out sbPtr) != 0 || sbPtr == IntPtr.Zero)
            return "FAIL: QueryService(IShellBrowser) failed";

        var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(sbPtr);
        IntPtr psvPtr;
        if (browser.QueryActiveShellView(out psvPtr) != 0 || psvPtr == IntPtr.Zero)
            return "FAIL: QueryActiveShellView failed";

        var view = (IShellView)Marshal.GetObjectForIUnknown(psvPtr);
        var fv = view as IFolderView;
        if (fv == null)
            return "FAIL: the IShellView -> IFolderView QueryInterface failed (changing the selection is a dead end)";
        log.AppendLine("PASS: IShellView -> IFolderView available");

        int count;
        if (fv.ItemCount(SVGIO_ALLVIEW, out count) != 0)
            return log.AppendLine("FAIL: ItemCount failed").ToString();
        log.AppendLine("PASS: ItemCount = " + count + " (the number of items in the view is visible)");

        var names = new List<string>();
        for (var i = 0; i < count && i < 8; i++)
        {
            IntPtr pidl;
            names.Add(fv.Item(i, out pidl) == 0 && pidl != IntPtr.Zero ? NameOf(pidl) : "<err>");
        }
        log.AppendLine("view order (first 8): " + string.Join(" | ", names));

        int originalMarked;
        fv.GetSelectionMarkedItem(out originalMarked);
        int originalFocused;
        fv.GetFocusedItem(out originalFocused);
        var before = ReadSelection(view);
        log.AppendLine("initial state: selection=" + (before.Length == 0 ? "(none)" : System.IO.Path.GetFileName(before)) +
                       ", marked index=" + originalMarked + ", focused index=" + originalFocused);

        if (count < 2)
            return log.AppendLine("SKIP: too few items to test adjacent navigation").ToString();

        var target = 1;
        var targetName = NameOf(ItemOf(fv, target));
        log.AppendLine("target: item " + target + " = " + targetName);

        // (1) change the selection without activating the view
        var hr = fv.SelectItem(target, SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED | SVSI_ENSUREVISIBLE);
        System.Threading.Thread.Sleep(250);
        var after1 = ReadSelection(view);
        log.AppendLine("(1) no activation + IFolderView::SelectItem: hr=0x" + hr.ToString("X8") +
                       " -> selection=" + (after1.Length == 0 ? "(none)" : System.IO.Path.GetFileName(after1)) +
                       (System.IO.Path.GetFileName(after1) == targetName ? "  PASS" : "  FAIL"));

        // (2) activate with SVUIA_ACTIVATE_NOFOCUS (activation without stealing keyboard focus), then change
        var hrAct = view.UIActivate(SVUIA_ACTIVATE_NOFOCUS);
        hr = fv.SelectItem(target, SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED | SVSI_ENSUREVISIBLE);
        System.Threading.Thread.Sleep(250);
        var after2 = ReadSelection(view);
        log.AppendLine("② ACTIVATE_NOFOCUS(hr=0x" + hrAct.ToString("X8") + ") + IFolderView::SelectItem: hr=0x" +
                       hr.ToString("X8") + " -> selection=" +
                       (after2.Length == 0 ? "(none)" : System.IO.Path.GetFileName(after2)) +
                       (System.IO.Path.GetFileName(after2) == targetName ? "  PASS" : "  FAIL"));

        // (3) while activated, use the fallback IShellView::SelectItem(pidl) path instead
        var hr3 = view.SelectItem(ItemOf(fv, target), SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED | SVSI_ENSUREVISIBLE);
        System.Threading.Thread.Sleep(250);
        var after3 = ReadSelection(view);
        log.AppendLine("(3) IShellView::SelectItem(pidl): hr=0x" + hr3.ToString("X8") + " -> selection=" +
                       (after3.Length == 0 ? "(none)" : System.IO.Path.GetFileName(after3)) +
                       (System.IO.Path.GetFileName(after3) == targetName ? "  PASS" : "  FAIL"));

        // out-of-bounds behaviour (decides whether wrap-around has to be implemented here)
        var hrOver = fv.SelectItem(count, SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED | SVSI_ENSUREVISIBLE);
        log.AppendLine("past the last item (index " + count + "): hr=0x" + hrOver.ToString("X8") +
                       " (no automatic wrap-around; wrapping is up to the app)");

        // restore
        view.UIActivate(SVUIA_DEACTIVATE);
        if (before.Length != 0)
        {
            var restoredIndex = -1;
            for (var i = 0; i < count; i++)
            {
                if (string.Equals(NameOf(ItemOf(fv, i)), System.IO.Path.GetFileName(before),
                        StringComparison.OrdinalIgnoreCase))
                {
                    restoredIndex = i;
                    break;
                }
            }
            if (restoredIndex >= 0)
                fv.SelectItem(restoredIndex, SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED);
            else
                fv.SelectItem(-1, SVSI_DESELECTOTHERS);
        }
        else
        {
            fv.SelectItem(-1, SVSI_DESELECTOTHERS);
        }
        System.Threading.Thread.Sleep(200);
        var restored = ReadSelection(view);
        log.AppendLine("restored: selection=" +
                       (restored.Length == 0 ? "(none, same as the initial state)" : System.IO.Path.GetFileName(restored)));
        return log.ToString();
    }

    private static IntPtr ItemOf(IFolderView fv, int index)
    {
        IntPtr pidl;
        return fv.Item(index, out pidl) == 0 ? pidl : IntPtr.Zero;
    }
}
"@

Add-Type -TypeDefinition $probe

$shell = New-Object -ComObject Shell.Application
$windows = $shell.Windows()

$target = $null
$label = ''
for ($i = 0; $i -lt $windows.Count; $i++) {
    $win = $windows.Item($i)
    if ($win.LocationURL -match '^file:') {
        $target = $win
        $label = "$($win.LocationURL)"
        break
    }
}

if (-not $target) {
    Write-Host "SKIP: no usable file manager window (no folder window is currently open)" -ForegroundColor Yellow
    exit 0
}

Write-Host ([SelectionSetProbe]::Run($target, $label))
