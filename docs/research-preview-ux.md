# 预览体验调查与可行性验证记录

> 状态：**仅调研与验证，未改动产品代码、未发布版本**。记录时间 2026-09-17。
>
> 起因：有用户反馈"预览（视频）时窗口能否记住上一次大小，或者自定义窗口大小"。
> 查过上游后决定**不做**这项需求（见 §3），转而验证两件更有价值的事：
> **←/→ 文件夹内切换文件**、**大图预览保护**。本文是这两项的可行性验证记录，
> 外加一份上游热门 issue 的排期参考。

## 1. 结论速览

| 项 | 结论 | 关键证据 |
|---|---|---|
| ←/→ 切换同目录上/下一个文件 | **可行** | `Scripts/probe-explorer-selection.ps1`：`IFolderView::SelectItem` 能把选区移到相邻项，**且不需要激活窗口（不抢焦点）** |
| 大图预览保护 | **必要** | 6400 万像素 PNG：首帧 287ms（速度没问题），但私有内存峰值 **1.2–1.7 GB**（普通图 243 MB），关闭后仍留 429 MB（基线 203 MB） |
| 记住预览窗口尺寸 | **不做** | 上游 8 年间同类请求全部被拒（#169/#821/#492/#1196）；固定尺寸会把一个宽高比强加给所有内容 → 黑边/空白 |

## 2. ←/→ 文件导航：可行性验证

### 2.1 实测输出

```
PASS: IShellView -> IFolderView 可用
PASS: ItemCount = 18（能按视图顺序枚举项目）
视图顺序（前 8 项）: America | Artificial | Beauty | CutePet | Economics | Japan | Jing | Lin
原状态: 选中项=(无)，标记索引=-1，焦点索引=0
目标: 第 1 项 = Artificial
① 不激活 + IFolderView::SelectItem: hr=0x00000000 -> 选中项=Artificial  PASS
② ACTIVATE_NOFOCUS(hr=0x00000000) + IFolderView::SelectItem: hr=0x00000000 -> 选中项=Artificial  PASS
③ IShellView::SelectItem(pidl): hr=0x00000000 -> 选中项=Artificial  PASS
越过最后一项（索引 18）: hr=0x80004005（不会自动回绕，回绕要应用自己处理）
已还原: 选中项=(无，与原状态一致)
```

（探针只操作一个已打开的文件夹窗口，改完立即还原，未改动任何文件。）

### 2.2 为什么这条路走得通

- 应用读选区**已经在用** `IShellBrowser::QueryActiveShellView → IShellView::GetItemObject(SVGIO_SELECTION)`
  （`QuickLookNext/NativeMethods/QuickLook.cs`），只差在同一个 `IShellView` 上再 QueryInterface
  一个 `IFolderView`——探针证明这一步通得过（`PASS: IShellView -> IFolderView 可用`）。
- `IFolderView::ItemCount(SVGIO_ALLVIEW)` + `Item(i)` 返回的是 **Explorer 当前的显示顺序**
  （包含用户的排序设置），所以"下一项"和用户屏幕上看到的一致，不需要我们自己排序目录。
- `IFolderView::SelectItem(index, SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED | SVSI_ENSUREVISIBLE)`
  **不需要视图被激活**即可生效（上面 ① 就是不带任何激活的成功案例）——这对"不该抢焦点"的
  预览工具是决定性的一点。
- 应用本来就在**跟随 Explorer 的选区事件**（见 CHANGELOG 里 "follow the Explorer selection
  from events instead of a 500 ms poll"），所以选区一变，预览会自动切到新文件。
- 越界返回 `E_FAIL` 且不改变选区 → 首尾回绕（最后一项 → 第一项）需要应用自己实现。

### 2.3 实现草图（预计改动 4 处）

1. `QuickLookNext/NativeMethods/QuickLook.cs`：新增 `IFolderView` 接口声明
   （`ItemCount` / `Item` / `GetFocusedItem` / `SelectItem`）与一个内部方法
   `TryMoveSelection(int delta)`（找到当前项 → 目标项 → 回绕处理 → `SelectItem`）。
2. 全局键盘钩子：当"预览已打开 **且** 预览来源是 Explorer 选区"时拦下 ←/→，其余场景一律放行。
3. 目标窗口：使用**提供当前预览的那个 Explorer 窗口**的 `IShellBrowser`（多窗口时不会改错窗口）。
4. 回绕与边界：`index - 1 < 0 → count - 1`；`index + 1 >= count → 0`。

### 2.4 待确认 / 风险

- **多选**：以 `GetFocusedItem`（或带标记项）为基准，多选时只移动"当前项"。
- **虚拟文件夹**（搜索结果、库）：`Item(i)` 顺序即视图顺序，可用；路径解析统一走
  `SHGetNameFromIDList`。
- **按键归属**：预览窗口本身有键盘焦点时，←/→ 是否应该给插件（视频快退/快进、图片翻页）
  要定优先级。建议：预览窗口有焦点 → 给插件；焦点在别处（典型空格键预览场景）→ 文件导航。
- **来源判定**：预览可能是通过命令行/管道打开的（不是 Explorer 选区），这种情况要直接跳过。

## 3. 为什么不做"记住预览窗口尺寸"

上游（QL-Win/QuickLook）同类诉求的历史：

| issue | 诉求 | 上游答复 |
|---|---|---|
| [#169](https://github.com/QL-Win/QuickLook/issues/169)（2018） | 记住窗口大小和位置 | 原作者 xupefei：大小**只在切换预览时保留**，关闭后恢复，**"This is intended"**；后续要求写进配置时维护者回"要同时满足所有插件的要求很难" |
| [#821](https://github.com/QL-Win/QuickLook/issues/821) | 按文件类型记住尺寸 | emako：**"No plan to support"** |
| [#492](https://github.com/QL-Win/QuickLook/issues/492) | 记住上一次大小 | emako：**"No supported plans"** |
| [#1196](https://github.com/QL-Win/QuickLook/issues/1196) → #1078 → #608 | 同一诉求的多次重复 | 一路判重复，最终回到 #169 |
| [#525](https://github.com/QL-Win/QuickLook/issues/525)（原作者提出） | 视频窗口没有贴到实际视频尺寸 | xupefei：**"视频插件会检测真实分辨率并让窗口去贴合它；检测不到时才用默认尺寸，于是出现奇怪的边框"** |

代码侧一致：上游 `QuickLook/ViewerWindow.xaml.cs` 就是本仓库这套 `_customWindowSize`
（**只在内存、会话内有效**），上游 `OPTIONS.md` 里**没有任何窗口尺寸/位置选项**。

结论：预览窗口尺寸"由内容决定"是刻意设计。把它固定下来，等于给每个文件强加同一个宽高比
——视频会出现信箱黑边、图片会居中留白。**5.0.4 试过持久化，效果就是用户反馈的"很大的黑边和空白"，
发布当天已撤回**（Release 删除，最新版回到 5.0.3；分支上的提交仍需回退，见 §5）。

## 4. 大图预览保护：验证数据

同一台机器、本地产物实测：

| 文件 | 请求→内容就绪 | 私有内存峰值 | 工作集峰值 | 关闭预览后 |
|---|---|---|---|---|
| `test.png`（普通图） | 108 ms | 243 MB | 335 MB | — |
| `big8000.png`（8000×8000，6400 万像素） | 287 ms | **1,193–1,682 MB** | 1,657 MB | 429 MB（基线 203 MB） |

结论：

- **速度不是问题**：首帧走的是缩略图路径，6400 万像素也能 287ms 出内容；
- **内存是风险**：64MP 就吃到 1.2–1.7 GB；再大一档（1 亿像素以上的全景/TIFF）在 8 GB 机器上
  就是上游 [#1054 "Crash when previewing large images"](https://github.com/QL-Win/QuickLook/issues/1054)
  的崩溃场景；
- **关闭后没有完全归还**（+226 MB），大图路径建议在关闭时主动回收一次；
- 现成的钩子已经存在：`MetaProvider.GetSize()`（只读文件头拿像素数，目前用于计算窗口尺寸）、
  `AnimatedImage` 的缩略图路径 + `DecodedImageCache`。

### 修复草图

1. 像素数阈值（建议默认 40 MP，可配置）：超过时**只渲染缩略图**，并在标题/提示里说明
   "按 1:1（或放大）才会解码原始尺寸"；
2. 1:1 / 放大操作再触发全分辨率解码（挂在现有的 `ZoomToFit` / `_zoomFactor` 逻辑上）；
3. 关闭大图预览后做一次强制 GC + 清空解码缓存（**仅大图路径**，避免影响普通图的首帧速度）；
4. ImageMagick 那条路径（TIFF/PSD/DICOM 等）同样加护栏。

### 5.0.5 已实施的部分 + 实测复核（2026-09-19）

已实现"解码像素上限"（`DecodePixelLimit`，默认 40 MP，插件配置 `MaxDecodePixels`，`0` = 关闭；
WIC 与 ImageMagick 两条路径都覆盖），标题会标注"已按 40 MP 上限缩放预览"，关闭大图预览时主动回收一次。

同一张 6400 万像素 JPEG 的 A/B：

| 解码上限 | 峰值 | 稳定值 |
|---|---|---|
| 关闭（旧行为） | 1,705 MB | 1,424 MB |
| **40 MP（新默认）** | **1,425 MB** | **1,146 MB** |
| 8 MP（强压，仅用于定位） | 1,038 MB | — |

**结论：护栏只解决了一部分（约 -17%）。** 把上限压到 8 MP 仍有 ~1 GB 峰值，说明剩余开销**与解码大小
无关**，来自显示层：`NativeProvider.GetThumbnail` 会把缩略图缩放成"原图坐标系"的 `TransformedBitmap`
（8000×8000），WPF 又在这个巨大坐标系里布局/渲染。真正要解决得改成：

1. 缩略图保持自己的像素坐标系（不放大到原图坐标）；
2. 面板的缩放/平移数学改用"解码像素尺寸 + 比例系数"，而不是"原图像素尺寸"；
3. 只有用户放大到超过某个阈值（如 100%）时，才按需解码更高分辨率（并受同一个像素上限约束）。

这一项会动到面板的缩放/平移逻辑（回归面较大），单独作为一步来做。

### 5.0.6 完成收尾（2026-09-19 同日）

实验定位确认了上面的判断：只去掉"缩略图放大到原图坐标系"这一步（其它不动），峰值 1,425 → **911 MB**
（8 MP 上限时 1,038 → 437 MB）。但直接取消会让**帧换入后不再适应窗口**（缩略图几何 ≠ 帧几何，面板不会
重算适配，实测截图是放大裁切的状态）。

最终做法：**缩略图放大到"渲染帧的解码尺寸"**（受同一个 40 MP 上限约束），两者几何一致 → 适配正确
（截图验证），峰值也降下来：

| 版本 | 峰值 | 稳定值 |
|---|---|---|
| 5.0.4 之前（无护栏） | 1,705 MB | 1,424 MB |
| 5.0.5（仅解码上限） | 1,425 MB | 1,146 MB |
| **5.0.6（坐标空间 = 解码尺寸）** | **1,156 MB** | — |

缩放徽标同步折算（`ZoomDisplayFactor = ZoomFactor × 解码尺寸/真实尺寸`），100% 的含义仍是"相对原图的
1:1"。至此"大图保护"这一项可以视为完成；若要进一步压，只剩"只在放大时按需解码"这一条（收益递减）。

## 5. 上游热门 issue 汇总（排期参考）

按 open issue 评论数与全量 👍 数排序后筛选（"我们的现状"是代码核实过的）：

| 上游 | 诉求 | 上游状态 | 我们的机会 |
|---|---|---|---|
| [#691](https://github.com/QL-Win/QuickLook/issues/691) / [#1979](https://github.com/QL-Win/QuickLook/issues/1979) | 应用内 ←/→ 切换文件 | 未做成（卡在"预览窗口不该抢输入/需要改 C++"） | **纯 C# 可做**，见 §2 |
| [#1054](https://github.com/QL-Win/QuickLook/issues/1054) | 大图预览崩溃 | open，11 条评论 | 见 §4 |
| [#827](https://github.com/QL-Win/QuickLook/issues/827) / [#1956](https://github.com/QL-Win/QuickLook/issues/1956) | 混合 DPI 多显示器尺寸错 / 改缩放后 Markdown 渲染错 | open | 监听 DPI 变化重新贴合 + WebView2 重新布局 |
| [#1608](https://github.com/QL-Win/QuickLook/issues/1608) | 图片 OCR 提取文字 | open，10 条评论 | 系统自带 `Windows.Media.Ocr`，我们已引 WinRT 投影，零新依赖 |
| [#1933](https://github.com/QL-Win/QuickLook/issues/1933) | 显示/清理缓存占用 | open | 我们已有"打开数据文件夹"和 profile 修复逻辑，加个占用统计即可 |
| [#1571](https://github.com/QL-Win/QuickLook/issues/1571) | ARM64 原生支持 | open，14 条评论 | `pack-release.ps1` 已有 `-Architecture arm64`，但 Magick/LAV/pdfium/MediaInfo 缺 arm64 原生库 |
| [#1844](https://github.com/QL-Win/QuickLook/issues/1844) / [#1528](https://github.com/QL-Win/QuickLook/issues/1528) / [#1768](https://github.com/QL-Win/QuickLook/issues/1768) / [#1968](https://github.com/QL-Win/QuickLook/issues/1968) | 视频播放各种打不开（上游 4.x 换 WPFMediaKit 后） | open | 我们用 LAVFilters，理论上更稳；做一轮长视频/HEVC/MKV/网络路径回归即可作为优势背书 |
| [#1987](https://github.com/QL-Win/QuickLook/issues/1987) | 记住窗口尺寸/位置 | open | **不做**，见 §3 |

我们**已经领先**上游的部分（上游仍有人抱怨、我们已实现）：失焦自动关闭（#484）、启动与首预览性能
（上游 #1953 约 500ms 延迟；我们启动 96ms、首预览 100–350ms）、插件管理界面（#1916）、
二进制/Hex 查看（#290）。

## 6. 待办与交接

1. **回退 5.0.4 的"记住窗口尺寸"持久化**（保留其中两处真修复：插件请求的尺寸不再污染用户尺寸、
   固定尺寸预览的 `CanResize` 门控、以及"重置窗口大小"菜单项）——作为 5.0.5 的内容，**尚未发布**；
2. 大图护栏（§4）——改动小、直接防崩，建议先做；
3. ←/→ 文件导航（§2）；
4. 之后候选：图片 OCR（#1608）、缓存占用与清理（#1933）、ARM64（#1571）、视频健壮性回归。

## 7. 复现方式

- 选区探针：`pwsh -NoProfile -File .\Scripts\probe-explorer-selection.ps1`
  （挑第一个文件夹窗口做验证，改动立即还原）
- 大图测量：`pwsh -NoProfile -File .\Scripts\measure-preview.ps1 -Files <大图> -StartupWaitMs 1500`
  （耗时）；内存峰值用外部采样该进程的 `PrivateMemorySize64`
