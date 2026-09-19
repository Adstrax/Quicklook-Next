# 混合 DPI 多显示器：预览窗口"横跨三块屏"的成因与修复

> 状态：**已修复并随 5.0.10 发布**。记录时间 2026-09-19。
>
> 起因：上游 [QL-Win/QuickLook#827](https://github.com/QL-Win/QuickLook/issues/827)
> ——在 4K 笔记本（主屏 250%、副屏 175%）+ 外接 1080p（100%）的机器上，
> 用外接屏预览**横向大图**时，预览窗口会横跨三块显示器；竖图和小图正常。

## 1. 结论速览

| 项 | 结论 |
|---|---|
| 根因 | **算尺寸用的显示器**和**放窗口用的显示器**不是同一块：插件按前台窗口所在屏算，窗口定位按自己所在屏算 |
| 为什么"横图才出问题" | 适配比例取 `min(宽比, 高比)`：横图由**宽度**决定比例，宽屏的 DIP 尺寸一旦被放大到低缩放屏，像素宽度就超出该屏 |
| 为什么"方向键切换就正常" | 那条路径走的是"已有窗口"分支，定位基准仍是窗口自己所在屏，两侧基准一致 |
| 修法 | ① 预览开始时把**目标屏**交给插件（`ContextObject.HostDesktopSize`）；② 应用尺寸前用同一块屏**夹取**；③ DPI 变化时重新贴合 |
| 本机可验证的部分 | 单屏结果与修复前**逐像素一致**（见 §4）；尺寸算法本身由 12 条单元测试覆盖 |

## 2. 成因

两个尺寸基准，分别来自两块不同的屏幕：

1. **插件算尺寸**：`ContextObject.SetPreferredSizeFit()`（`QuickLook.Common/Plugin/ContextObject.cs`）
   调用 `WindowHelper.GetCurrentDesktopSize()`，其中 `GetCurrentDesktopRectInPixel()` 用的是
   **`GetForegroundWindow()`**——也就是资源管理器所在那块屏。
   插件随后把 `PreferredSize` 交给宿主（图片 0.8、PDF 0.9、Office 0.8 …… 都是这个入口）。

2. **宿主放窗口**：`ViewerWindow.Actions.cs` 的 `ResizeAndCentreNewWindow()`
   用 `GetCurrentDesktopRectInPixel()` + `GetCurrentScaleFactor()` 居中；
   `ResizeAndCentreExistingWindow()` 则用 `GetDesktopRectFromWindowInPixel(this)`——
   **预览窗口自己所在那块屏**。

于是"尺寸"和"落点"来自两块屏：

- 在前台窗口那块屏（DIP 桌面更大，例如 1920×1040@100%）算出的尺寸，
  一旦落到缩放更高的屏（1536×864 DIP @250%），换算成像素就被放大 2.5 倍 →
  横图那种"宽度顶满"的形状最先生效，窗口于是宽过屏幕，横跨多块显示器。
- 竖图的比例由**高度**决定，高度上有 10% 的边距余量（`limitPercentY`）兜着，所以看起来正常。

另外 `ResizeAndCentre*` 只会**夹位置**（把左/上边界拉回屏内），从不夹尺寸——
`pxNewRect` 大于屏宽时窗口照旧溢出，这正是截图里"三屏连成一片"的样子。

## 3. 修复

三处，都在"尺寸/落点必须用同一块屏"这个原则上：

1. **把目标屏交给插件**（`ViewerWindow.Actions.cs` 的 `BeginShow`）：
   每次预览开始、`Prepare` 之前设置
   `ContextObject.HostDesktopSize = GetTargetDesktopSizeInDip()`——
   目标屏 = 前台窗口所在屏（新窗口的居中基准），失效时回落到预览窗口自己的屏，再回落到主屏。
   `SetPreferredSizeFit()` 优先用它，拿不到才退回原来的 `GetCurrentDesktopSize()`。

2. **应用尺寸前夹取**（`ComputeWindowSize(clampToDesktop: true)`）：
   用**落点所在屏**的 DIP 工作区做 `ClampToDesktop`。
   即便插件写死了尺寸、或用户在预览打开后又把鼠标移到别的屏，
   窗口也不会大于它即将落上的那块屏。**用户自己拖出来的尺寸不夹**——那是用户的意图，不是尺寸计算错误。
   插件后置的尺寸请求（`ApplyPreferredSizeNow`，例如 PDF 量完页面）同样夹取。

3. **DPI 变化后重新贴合**（`ViewerWindow.OnDpiChanged` → `RefitWindowToItsOwnDesktop`）：
   Windows 在跨屏移动时保持窗口的**物理尺寸**，因此 DIP 尺寸会变——
   在 250% 的 4K 屏上看着正好的窗口，落到 100% 的 1080p 屏上就可能宽出几块屏。
   DPI 一变就按当前屏重新夹取（并把这次调整标记为"宿主的修正"，不写进用户的尺寸记忆）。

尺寸数学集中在 `QuickLook.Common/Helpers/PreviewWindowSizing.cs`（纯函数）：

- `FitRatio(content, desktop, maxRatio)`：取 `min(宽比, 高比)`，**不放大**（上限 1）；
- 异常输入不产生坏窗口：`maxRatio` 为 0/负/NaN 视作"整屏"，桌面查询失败（0×0）时原样返回，
  都不会把预览压成 0 像素；
- `ClampToDesktop(size, desktop)`：只缩不放，桌面未知时不动。

## 4. 验证

### 4.1 单元测试（不需要第二块屏）

`QuickLook.Tests/PreviewWindowSizingTests.cs`，12 条，覆盖 #827 的两块极端屏
（4K@250% → 1536×864 DIP，1080p@100% → 1920×1040 DIP）与 4289×631 那种"宽而扁"的形状：

- 本来放得下就不放大；横图按宽度、竖图按高度收敛；上限 >1 视作 1；
- 非法比例（0/负数/NaN）与未知桌面都不会产生 0 尺寸窗口；
- 夹取只缩不放，未知桌面不动；
- 端到端形状：在 1080p 上按 90% 量出的页面（高 936 DIP）放到 250% 的 4K 屏上时，
  未夹取前高度换成像素是 2340 px > 面板 2160 px（即"横跨多屏"），夹取后 = 2160 px，正好落在屏内。

测试套件：**60/60 通过**（原 48 条 + 新增 12 条）。

### 4.2 单屏不变性（本机可复现）

改动会经过"目标屏"这条新路径，所以必须在单屏机器上证明结果**没有变**。
本机：单屏 1536×960 DIP @200%（物理 3072×1920）。

复现方式（隐藏开关 `/test-preview-diag` 会把每次落位结果写进 `<smokeDir>\preview-rect.txt`）：

```powershell
pwsh -NoProfile -Command {
  $env:QL_SMOKE_DIR = '<repo>\ql-smoke'
  Start-Process '<repo>\Build\Release\QuickLook-Next.exe' -ArgumentList '/autorun','/test-preview-diag'
  Start-Sleep 3
  Start-Process '<repo>\Build\Release\QuickLook-Next.exe' -ArgumentList '<repo>\ql-smoke\big-image-6000x4000.png'
  Start-Sleep 3
  Get-Content '<repo>\ql-smoke\preview-rect.txt'
}
```

6000×4000 的图片：

| | 修复前 | 修复后 |
|---|---|---|
| 窗口（DIP） | 1154×770 | 1154×770 |
| 窗口（像素） | 2308×1540 @ (382,190) | 2308×1540 @ (382,190) |

逐像素一致，说明这次改动在单屏场景下是**路径替换**而非行为改变。
顺带把 png / pdf / md 各预览一遍：进程存活，窗口均居中落在 `monitorPx=(0,0,3072,1920)` 内。

### 4.3 本机**不能**验证的部分（如实记录）

本机只有一块屏，混合 DPI 的**真实**效果无法在此复现——上面的证据是
"数学正确 + 单屏零回归 + 诊断开关"，不是"在双屏机器上看到修好了"。
要在真机上复核，用同样的命令在混合 DPI 机器上跑一次，检查
`px=W×H at=(x,y)` 是否完整落在 `monitorPx=(l,t,w,h)` 之内即可（横图、竖图、PDF 各来一次）。

## 5. 相关与后续

- 上游 #827：2021 年报告，2025 年仍被标记"升级到 .NET Framework 4.7.2 之前无法解决"
  （[#1504](https://github.com/QL-Win/QuickLook/issues/1504)）；
  本仓库的定位代码本来就已按屏幕查询，本次补的是"两侧基准一致 + 落位夹取"。
- 上游 [#1956](https://github.com/QL-Win/QuickLook/issues/1956)"改屏幕缩放后 Markdown 渲染错"
  与本文同源（DPI 变化后没有重新贴合内容），本次的 `OnDpiChanged` 钩子为它留好了入口，
  但**未**在该 issue 上做验证，暂不算已解决。
- 仍未做：预览窗口被用户拖到另一块屏时**主动**重算插件内容尺寸（当前只在打开与 DPI 变化时贴合）。
