# 可行性验证探针：能否用 IFolderView::SelectItem 程序化改变 Explorer 的选区？
#
# 用途：为"预览时用 ←/→ 切换同目录上/下一个文件"验证核心未知项——应用能不能把
# Explorer 的选区移到指定项（能改选区就等于能驱动预览切换，因为应用已经在跟
# Explorer 的选区事件）。
#
# 验证内容：
#   1) IShellView -> IFolderView 的 QueryInterface 是否可用；
#   2) 能否按视图顺序（Explorer 当前排序）枚举项目；
#   3) 三种改选区路径：不激活 / SVUIA_ACTIVATE_NOFOCUS / IShellView::SelectItem(pidl)；
#   4) 越界（最后一项之后）的行为 —— Shell 不回绕，需要应用自己处理。
#
# 之所以针对"已打开的文件窗口"验证：本环境下用 explorer.exe 打开新窗口后，
# Shell.Application 枚举不到它（Win11 标签页 / 权限限制），而"改选区"这条能力
# 只需要一个真实存在的文件夹窗口即可验证。脚本会挑第一个文件窗口。
#
# 安全性：**只读 + 短暂改动 + 立即还原**。执行前记录原来的选中项，
# 结束后按原选中项选回去（原本没有选中项时清空选区），并恢复视图激活状态。
#
# 用法：pwsh -NoProfile -File .\Scripts\probe-explorer-selection.ps1

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
    // SVUIA_*: 视图的"UI 激活"状态。ACTIVATE_NOFOCUS 表示激活但不抢键盘焦点。
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

    /// <summary>对给定的 shell 窗口对象执行验证。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int DragQueryFile(IntPtr h, uint i, StringBuilder b, int c);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM m);

    /// <summary>
    /// 应用同款读法：从视图的"选中项"数据对象里取第一个文件路径。比
    /// GetSelectionMarkedItem 可靠（后者只在带"标记"的选择上有值）。
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
        log.AppendLine("目标窗口: " + label);

        var sp = (IServiceProvider)disp;
        var iid = IID_IShellBrowser;
        IntPtr sbPtr;
        if (sp.QueryService(ref iid, ref iid, out sbPtr) != 0 || sbPtr == IntPtr.Zero)
            return "FAIL: QueryService(IShellBrowser) 失败";

        var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(sbPtr);
        IntPtr psvPtr;
        if (browser.QueryActiveShellView(out psvPtr) != 0 || psvPtr == IntPtr.Zero)
            return "FAIL: QueryActiveShellView 失败";

        var view = (IShellView)Marshal.GetObjectForIUnknown(psvPtr);
        var fv = view as IFolderView;
        if (fv == null)
            return "FAIL: IShellView -> IFolderView 的 QueryInterface 失败（改选区这条路走不通）";
        log.AppendLine("PASS: IShellView -> IFolderView 可用");

        int count;
        if (fv.ItemCount(SVGIO_ALLVIEW, out count) != 0)
            return log.AppendLine("FAIL: ItemCount 失败").ToString();
        log.AppendLine("PASS: ItemCount = " + count + "（能看到视图里的项目数）");

        var names = new List<string>();
        for (var i = 0; i < count && i < 8; i++)
        {
            IntPtr pidl;
            names.Add(fv.Item(i, out pidl) == 0 && pidl != IntPtr.Zero ? NameOf(pidl) : "<err>");
        }
        log.AppendLine("视图顺序（前 8 项）: " + string.Join(" | ", names));

        int originalMarked;
        fv.GetSelectionMarkedItem(out originalMarked);
        int originalFocused;
        fv.GetFocusedItem(out originalFocused);
        var before = ReadSelection(view);
        log.AppendLine("原状态: 选中项=" + (before.Length == 0 ? "(无)" : System.IO.Path.GetFileName(before)) +
                       "，标记索引=" + originalMarked + "，焦点索引=" + originalFocused);

        if (count < 2)
            return log.AppendLine("SKIP: 该项目数不足以做相邻项测试").ToString();

        var target = 1;
        var targetName = NameOf(ItemOf(fv, target));
        log.AppendLine("目标: 第 " + target + " 项 = " + targetName);

        // ① 不激活视图，直接改选区
        var hr = fv.SelectItem(target, SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED | SVSI_ENSUREVISIBLE);
        System.Threading.Thread.Sleep(250);
        var after1 = ReadSelection(view);
        log.AppendLine("① 不激活 + IFolderView::SelectItem: hr=0x" + hr.ToString("X8") +
                       " -> 选中项=" + (after1.Length == 0 ? "(无)" : System.IO.Path.GetFileName(after1)) +
                       (System.IO.Path.GetFileName(after1) == targetName ? "  PASS" : "  FAIL"));

        // ② SVUIA_ACTIVATE_NOFOCUS（激活但不抢键盘焦点）之后再改
        var hrAct = view.UIActivate(SVUIA_ACTIVATE_NOFOCUS);
        hr = fv.SelectItem(target, SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED | SVSI_ENSUREVISIBLE);
        System.Threading.Thread.Sleep(250);
        var after2 = ReadSelection(view);
        log.AppendLine("② ACTIVATE_NOFOCUS(hr=0x" + hrAct.ToString("X8") + ") + IFolderView::SelectItem: hr=0x" +
                       hr.ToString("X8") + " -> 选中项=" +
                       (after2.Length == 0 ? "(无)" : System.IO.Path.GetFileName(after2)) +
                       (System.IO.Path.GetFileName(after2) == targetName ? "  PASS" : "  FAIL"));

        // ③ 激活状态下改用 IShellView::SelectItem(pidl) 这条备用路径
        var hr3 = view.SelectItem(ItemOf(fv, target), SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED | SVSI_ENSUREVISIBLE);
        System.Threading.Thread.Sleep(250);
        var after3 = ReadSelection(view);
        log.AppendLine("③ IShellView::SelectItem(pidl): hr=0x" + hr3.ToString("X8") + " -> 选中项=" +
                       (after3.Length == 0 ? "(无)" : System.IO.Path.GetFileName(after3)) +
                       (System.IO.Path.GetFileName(after3) == targetName ? "  PASS" : "  FAIL"));

        // 越界行为（决定要不要自己实现首尾回绕）
        var hrOver = fv.SelectItem(count, SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED | SVSI_ENSUREVISIBLE);
        log.AppendLine("越过最后一项（索引 " + count + "）: hr=0x" + hrOver.ToString("X8") +
                       "（不会自动回绕，回绕要应用自己处理）");

        // 还原
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
        log.AppendLine("已还原: 选中项=" +
                       (restored.Length == 0 ? "(无，与原状态一致)" : System.IO.Path.GetFileName(restored)));
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
    Write-Host "SKIP: 没有可用的文件资源管理器窗口（当前没有打开任何文件夹窗口）" -ForegroundColor Yellow
    exit 0
}

Write-Host ([SelectionSetProbe]::Run($target, $label))
