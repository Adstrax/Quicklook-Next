# QuickLook-Next

**简体中文** | [English](README.en.md)

> QuickLook 的 **UI 美化与功能完善版**（基于 [QL-Win/QuickLook](https://github.com/QL-Win/QuickLook)
> 4.5.0 的 .NET 10 迁移版）。

QuickLook-Next 保留完整的文件预览能力，并把预览背景、窗口圆角、主题、托盘菜单、
插件管理、语言、自动更新等体验全面重构与增强——**功能只多不少**。

独立分支 `lite`，与官方完整版命名隔离（管道 / 互斥体使用 `QuickLookNext.App.*`），
可同时安装互不干扰。

## 界面一览

以下截图均来自实际运行效果。

### 图片预览

支持 png / jpg / gif / webp / bmp / psd / raw / heic / svg 等 100+ 图片格式；
Acrylic 毛玻璃背景一打开即生效并跟随壁纸，窗口带 Win11 原生圆角。

![图片预览](docs/screenshots/preview-image.png)

### Markdown 预览

支持标准 Markdown、mermaid 图表与 MathJax 公式，代码高亮；内容可上下滚动，
阅读体验流畅。

![Markdown 预览](docs/screenshots/preview-markdown.png)

### Office 预览（自研渲染，截图以 Excel 为例）

Excel / Word / PowerPoint 均不再调用 Windows 系统预览组件，改为 OOXML 解析 +
WebView2 自研渲染；固定浅色纸面、阅读舒适，圆角与主题和整体界面一致，
无需安装 Office。

![Office 预览（Excel 示例）](docs/screenshots/preview-excel.png)

### PDF 预览

逐页浏览 PDF，左侧框架区 + 右侧纸面布局清晰，阅读舒适。

![PDF 预览](docs/screenshots/preview-pdf.png)

### 托盘菜单（Acrylic）

毛玻璃托盘菜单，主题 / 背景 / 语言等收进二级子菜单，条目带 Fluent 图标；
点击选项不会误关菜单。

![托盘菜单](docs/screenshots/tray-menu.png)

### 插件管理面板

列出内置与用户插件，用户插件可直接卸载；老插件无需重新编译即可安装加载。

![插件管理面板](docs/screenshots/plugin-manager.png)

## 相比原版的主要改进

### UI 美化

- **Acrylic 一打开即生效、跟随壁纸**：原版在 Win11 上使用 DWM Acrylic，而预览窗口
  从不抢焦点，导致文本 / 代码等内容打开时是纯色、点击后才出现毛玻璃。QuickLook-Next
  改用 WCA 方案，打开即是毛玻璃，且桌面壁纸变化时背景同步变化
- **Win11 原生圆角**：毛玻璃与内容一起 8px 圆角，没有方形毛玻璃边角
- **统一 Acrylic 观感**：预览窗口、托盘菜单、插件管理面板同为无边框非分层
  WCA 毛玻璃 + DWM 圆角，没有方形毛玻璃边角，也没有多余的外圈投影
- **亮色 / 暗色 / 跟随系统**：托盘菜单一键切换并持久化，预览立即生效
- **顶部状态栏默认隐藏**：鼠标移到窗口顶部标题栏区域才显示，移开后自动隐藏，
  不遮挡内容
- **主题色统一**：托盘菜单与插件管理面板共享同一套调色板，文字 / 分隔线 /
  悬停 / 强调色单一来源，强调色跟随系统主题色
- **滚动条随主题**：预览窗口、托盘菜单、插件管理面板的滚动条滑块颜色随
  亮 / 暗主题变化
- **切换预览无闪烁**：内容淡入动画只在首次预览时触发，切换文件时保持全不透明度
- **托盘菜单分组 + 图标**：主题模式、背景模式、语言、选项收进二级子菜单，
  顶层保持简短，条目带 Fluent 图标；同时修复了菜单闪动、点击图标菜单消失、
  二级菜单误关等问题

### 功能完善

- **Office 三件套自研渲染**：Excel / Word / PowerPoint 不再调用 Windows 系统预览
  组件，改为自研解析渲染（MiniExcel / OOXML 解析 → WebView2），获得圆角、毛玻璃、
  深浅色一致的观感，且内容固定浅色纸面、阅读舒适
- **插件管理面板（原版没有）**：列出用户安装与内置插件，用户插件可直接卸载；
  插件契约保持 `QuickLook.Common` / `QuickLook.Plugin.*`，老插件无需重新编译即可
  安装加载
- **内置语言切换**：托盘菜单「语言」子菜单支持跟随系统 + 30 种语言（常用语言
  优先排序），显示名使用本地语言（中文显示为「简体中文」「繁体中文」），
  选择后持久化
- **自动更新**：检查更新时直接下载 Release 安装包并原地更新重启，不再只是打开网页
- **文本预览可上下滚动**：修复分层窗口收不到滚轮消息的问题，txt / log / json /
  代码等预览可正常滚动
- **预览打开提速**：第二实例不再初始化 WPF，直接通过命名管道转发给常驻实例；
  图片解码管线在启动后台预热，首次预览不再支付一次性初始化成本

### 性能与体积

- **启动提速**：25 个插件程序集并行加载、语法高亮并行初始化，插件就绪时间从约
  2.5s 降到约 0.4s
- **按需加载**：罕见格式插件（3D、数据库、PE、邮件等）首次遇到才加载，常驻内存
  与原生库占用更低（MediaInfo 约 8MB、ImageMagick 约 24MB 原生库不再预载）；
  Markdown 预览按需加载 mermaid / MathJax；字体预览（FontViewer）同样按需加载，
  FreeType / OpenFont 依赖不再常驻
- **预览匹配缓存**：同一扩展名重复预览跳过全量插件扫描（结果与全量扫描一致），
  插件 Init 后台并行化，程序集索引启动期后台预建
- **画刷缓存**：托盘菜单 / 插件面板调色板画刷复用并冻结，减少反复分配
- **发布包精简**：运行库统一收进 `lib\`，共享依赖去重，应用图标无损压缩
  （app.ico 1457KB → 92KB，exe 约 1.6MB → 0.25MB，zip 约 60MB）

### 工程与架构

- **.NET 10 迁移**：目标框架 net10.0-windows，构建只需 .NET SDK
- **纯 C# 空格键链路**：焦点判断 + Explorer / 桌面选区读取改为 P/Invoke + Shell COM，
  不再依赖原生 C++ 工具链
- **命名隔离**：管道 / 互斥体使用 `QuickLookNext.App.*`，与官方完整版互不干扰
- **构建质量**：构建警告从 31 个清理到 1 个
- **自动化测试**：19 种格式预览 + Shell 选区链路 + 托盘菜单/插件面板
  （[test.ps1](test.ps1)，提交前必须全绿）；GitHub Actions 在每次推送自动构建并
  运行冒烟测试

## 安装与使用

1. 从 [Releases](https://github.com/Adstrax/QuickLook-Next/releases) 下载最新版，解压后运行
   `QuickLook-Next.exe`
2. 选中文件按 **空格** 预览，**Esc** 关闭；预览窗口支持置顶、跨预览拖拽内容
3. 托盘图标右键可切换主题 / 背景 / 语言、管理插件、检查更新等

> **系统要求**：需要 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
> （Windows 10 / 11）。如果启动时提示缺少运行时，点击提示中的下载按钮安装后
> 重新打开即可；未安装 .NET 10 时应用不会启动。

## 构建

需要 .NET 10 SDK：

```powershell
dotnet build QuickLookNext.slnx -c Release
```

提交前运行冒烟测试：`.\test.ps1`（要求全部通过）。

生成用户友好的发布包（根目录只保留 `QuickLook-Next.exe` 和少量配置文件，
其余运行库收进 `lib\` 子目录，插件在 `QuickLook.Plugin` 子目录，自带便携模式与
首次使用说明）：

```powershell
.\Scripts\pack-release.ps1 -MakeZip
```

产物：`Build\QuickLook-Next-<版本号>.zip`，解压后结构如下，用户只需双击根目录的
`QuickLook-Next.exe`：

```
QuickLook-Next.exe
QuickLook-Next.dll / .deps.json / .runtimeconfig.json
Translations.config
Readme.txt          # 首次使用说明（含 .NET 运行时要求，中文）
lib\               # 第三方运行库
runtimes\          # 原生运行库
QuickLook.Plugin\  # 内置插件
```

## 支持的文件格式

文件夹预览由主程序内置的 InfoPanel 提供，内置 25 个插件（含恢复的 11 个
插件），覆盖日常与专业格式：

| 插件 | 用途 |
|---|---|
| ImageViewer | 图片（png/jpg/gif/webp/bmp/psd/raw/heic/svg/ico 等 100+ 格式） |
| VideoViewer | 视频与音频（MediaInfo 嗅探，支持 mp4/mkv/avi/mov/webm/mp3/flac 等） |
| TextViewer | 文本与代码（txt/log/ini/json/xml/rtf/csv 及数百种代码语言） |
| MarkdownViewer | Markdown（md/mdx/mermaid/ipynb/adoc/rst 等） |
| OfficeViewer | Office（docx/xlsx/pptx 自研渲染；doc/xls/ppt/odt/ods/odp/vsd/vsdx 系统兜底） |
| HtmlViewer | HTML/MHT/URL（WebView2 渲染，也是 MD 插件的依赖） |
| PdfViewer | PDF |
| ArchiveViewer | 压缩包与安装包（zip/rar/7z/tar/gz/bz2/xz/cbz/cbr/jar/apk/msi 等） |
| CsvViewer | CSV/TSV/PSV 表格化视图 |
| FontViewer | 字体（ttf/otf/woff/woff2/ttc/eot） |
| MediaInfoViewer | 右键菜单查看媒体信息 |
| CLSIDViewer | 系统 shell 特殊对象（我的电脑、回收站等） |
| AppViewer | 应用安装包详情（apk/ipa/msi/dmg/deb/rpm 等） |
| PluginInstaller | .qlplugin 插件安装 |
| BinaryViewer | 二进制文件（bin/hex） |
| CertViewer | 数字证书（cer/crt/pem/pfx/p12 等） |
| ChmViewer | CHM 帮助文档 |
| DbViewer | 数据库（SQLite 等） |
| DumpViewer | 崩溃转储（dmp） |
| ELFViewer | ELF 可执行文件（Linux 二进制） |
| HelixViewer | 3D 模型（stl/obj/3ds/fbx/glb/gltf/dae 等） |
| MailViewer | 邮件（eml/msg） |
| PEViewer | PE 可执行文件（exe/dll/sys 等） |
| PrefetchViewer | Windows 预读取文件（pf） |
| ThumbnailViewer | 设计文件缩略图（cdr/fig/kra/pdn/sketch/xd 等） |

## 更新历史

详细更新记录见 [CHANGELOG.md](CHANGELOG.md) 与 GitHub [Releases](https://github.com/Adstrax/QuickLook-Next/releases)。
