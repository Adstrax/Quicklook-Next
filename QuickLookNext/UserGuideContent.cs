// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using System.Linq;

namespace QuickLookNext;

/// <summary>
/// Built-in User Guide copy. The guide intentionally ships only in English and
/// Simplified Chinese for now; other UI languages fall back to English.
/// </summary>
internal static class UserGuideContent
{
    internal sealed record GuideSection(
        string Title,
        string Summary,
        IReadOnlyList<GuideBlock> Blocks)
    {
        internal string SearchText => string.Join(
            " ",
            new[] { Title, Summary }.Concat(Blocks.Select(block => block.SearchText)));
    }

    internal abstract record GuideBlock(string SearchText);

    internal sealed record GuideParagraph(string Text)
        : GuideBlock(Text);

    internal sealed record GuideBullets(IReadOnlyList<string> Items)
        : GuideBlock(string.Join(" ", Items));

    internal sealed record GuideCallout(string Text)
        : GuideBlock(Text);

    internal sealed record GuideDefinitions(IReadOnlyList<(string Title, string Body)> Items)
        : GuideBlock(string.Join(" ", Items.Select(item => item.Title + " " + item.Body)));

    internal sealed record GuideShortcuts(IReadOnlyList<(string Input, string Action)> Items)
        : GuideBlock(string.Join(" ", Items.Select(item => item.Input + " " + item.Action)));

    internal static IReadOnlyList<GuideSection> Create(bool chinese)
        => chinese ? CreateChinese() : CreateEnglish();

    private static GuideParagraph P(string text) => new(text);

    private static GuideBullets B(params string[] items) => new(items);

    private static GuideCallout C(string text) => new(text);

    private static GuideDefinitions D(params (string Title, string Body)[] items) => new(items);

    private static GuideShortcuts K(params (string Input, string Action)[] items) => new(items);

    private static IReadOnlyList<GuideSection> CreateChinese()
    {
        return
        [
            new GuideSection(
                "快速开始",
                "不打开原程序，也能立刻查看文件内容。",
                [
                    P("QuickLook Next 会在系统托盘中运行。预览文件时不需要先打开软件主界面。"),
                    C("最常用的操作：在资源管理器中选中文件，按空格预览；再按一次空格或 Esc 关闭。"),
                    B(
                        "打开安装版或便携版后，确认托盘区域出现 QuickLook Next 图标。",
                        "在资源管理器中单击一个受支持的文件。",
                        "按空格打开预览。",
                        "按空格或 Esc 关闭预览。"),
                    P("安装完成后，软件可以直接预览文档、图片、视频和压缩包等常见文件，不需要额外安装 Office。"),
                ]),
            new GuideSection(
                "基本操作",
                "空格、鼠标中键和方向键是最常用的操作方式。",
                [
                    D(
                        ("空格键", "打开或关闭当前选中文件的预览。"),
                        ("Esc", "关闭当前预览窗口。"),
                        ("方向键", "资源管理器仍保持焦点时用于切换文件；预览窗口获得焦点后，方向键交给当前预览内容使用。"),
                        ("鼠标中键", "预览鼠标指针下的文件或桌面图标；再次按下中键即可关闭。"),
                        ("鼠标侧键", "在支持的资源管理器场景中切换相邻文件。"),
                        ("顶栏", "默认隐藏。将鼠标移动到预览窗口顶部时会自动出现。")),
                    C("当前鼠标侧键规则：后退键打开下一个文件，前进键打开上一个文件。侧键是否有效还取决于鼠标驱动是否把按键发送给 Windows。"),
                ]),
            new GuideSection(
                "预览窗口技巧",
                "调整阅读区域、隐藏导航栏，并按文件类型使用不同的浏览方式。",
                [
                    P("预览窗口顶部默认保持收起，正常阅读时不会占用空间。需要刷新或关闭窗口时，把鼠标移到窗口顶部即可显示顶栏。"),
                    B(
                        "PDF 等文档可以隐藏和重新显示导航栏。",
                        "隐藏导航栏后，它会悬浮在正文上方，不会继续挤压阅读区域。",
                        "拖动中间分隔线可以调整导航栏和内容区的宽度。",
                        "预览窗口支持拖动位置；可调整大小的窗口也可以拖动边缘改变尺寸。"),
                ]),
            new GuideSection(
                "支持的预览类型",
                "内置预览覆盖日常办公、图片、媒体、文本和开发文件。",
                [
                    D(
                        ("图片", "支持滚轮缩放和拖动查看。超长或超宽图片可以上下或左右滑动，不会为了塞进窗口而缩得过小。超大图片默认使用 40 MP 解码保护。"),
                        ("视频", "直接播放，并尽量按照原始比例适配窗口。部分特殊编码的视频需要系统具备相应解码能力。"),
                        ("PDF", "支持页面预览、翻页、缩放和导航。有目录时显示目录，没有目录时显示页面缩略图。"),
                        ("Office", "支持 Word、Excel 和 PowerPoint 等常见文件，不需要安装 Microsoft Office。自建界面会尽量保留字体、颜色、图片、图表和版式。"),
                        ("Markdown 与 HTML", "显示排版后的内容。Markdown 可在连续切换时保持稳定的预览区域，不会反复闪烁。"),
                        ("纯文本与代码", "直接显示文本内容，适合快速查看日志、代码和配置文件。"),
                        ("压缩包", "查看压缩包内部结构，并直接预览其中的支持文件。关闭后不会在桌面留下解压文件。"),
                        ("XMind", "直接预览思维导图，并可缩放和拖动查看节点。"),
                        ("其他格式", "还包含字体、数据库、邮件、3D 模型等内置预览。不同格式的可用功能由对应插件决定。")),
                    C("Office 自建界面负责保持统一的软件外观；遇到无法完整解析的复杂元素时，可以使用原生预览作为兼容兜底。"),
                ]),
            new GuideSection(
                "托盘菜单",
                "右键单击托盘图标，可以修改设置、管理插件和查看更新。",
                [
                    D(
                        ("主题模式", "跟随系统、浅色或深色。"),
                        ("语言", "跟随系统，或手动选择界面语言。"),
                        ("背景模式", "选择 Auto、无背景、Mica、Acrylic、Acrylic 10、Acrylic 11 或 Tabbed。"),
                        ("选项", "开关开机自启动、失去焦点时关闭、顶部状态栏默认隐藏和自动检查更新。"),
                        ("省内存模式", "选择关闭、90 秒、5 分钟、15 分钟、30 分钟、1 小时或从不释放。默认 5 分钟。"),
                        ("预览预热", "按使用频率预热 1、3、5 或 8 类文件。默认 3 类。"),
                        ("检查更新 / 更新日志", "手动检查版本，或查看最近正式版本的更新说明。"),
                        ("使用指南", "打开当前窗口，随时查找功能和操作方式。"),
                        ("插件市场", "查看和安装插件，也可以更新或卸载用户安装的插件。"),
                        ("清理缓存", "查看数据占用，并清理可重新生成的缓存和诊断日志。"),
                        ("重启 / 退出", "重启预览服务，或完全退出 QuickLook Next。")),
                ]),
            new GuideSection(
                "外观设置",
                "主题、背景和语言彼此独立，可以按使用场景自由组合。",
                [
                    D(
                        ("主题模式", "跟随系统会根据 Windows 的浅色或深色设置自动切换；也可以固定使用浅色或深色。"),
                        ("背景模式", "Mica 和 Acrylic 提供不同的窗口材质。对性能敏感或系统关闭透明效果时，可以使用无背景。"),
                        ("语言", "默认跟随 Windows。当前完整支持指南正文的是简体中文和英文；其他界面语言打开指南时会使用英文。")),
                    P("图片和视频主要展示内容本身，不适合承载复杂窗口材质，因此会尽量减少背景效果对内容的影响。"),
                ]),
            new GuideSection(
                "预热与省内存",
                "用少量内存换取更快的首次预览，也可以在闲置后自动释放。",
                [
                    D(
                        ("预览预热", "软件根据你最常预览的文件类型提前准备预览环境。默认预热 3 类，也可以选择 1、3、5 或 8 类。预热越多，首次预览通常越快，但闲置内存也会增加。"),
                        ("省内存模式", "软件启动后先保持预热。一个时间段内发生过预览，就继续保持下一个完整时间段的预热；只有整整一个时间段没有预览时，才会释放预览内存。")),
                    C("刚关闭预览时内存没有立即下降是正常现象。默认需要等待完整的 5 分钟空闲时间段，期间再次预览会重新开始计算。"),
                ]),
            new GuideSection(
                "插件市场",
                "扩展 QuickLook Next 支持的文件格式和预览能力。",
                [
                    P("托盘菜单中的“插件市场”可查看已安装插件和可下载插件，支持搜索、安装、更新以及卸载用户安装的插件。"),
                    B(
                        "内置插件随软件一起安装并经过统一维护。",
                        "用户插件会安装到个人数据目录，卸载后建议重启软件以彻底释放文件。",
                        "插件与软件拥有相同的系统权限，只安装来源可信的插件。",
                        "安装或更新插件失败时，先检查网络连接和插件文件是否完整。"),
                ]),
            new GuideSection(
                "数据、缓存与更新",
                "了解软件保存了什么，以及如何安全清理和升级。",
                [
                    D(
                        ("清理缓存", "清除 WebView2 缓存、旧更新残留和超过一天的旧预览临时文件。最近预览文件、设置、登录状态与日志会保留。"),
                        ("清空诊断日志", "单独删除异常和诊断日志。日志通常只在反馈问题时需要保留。"),
                        ("自动更新", "默认开启。发现新版本后可在软件内查看更新说明、下载并自动重启安装。"),
                        ("更新日志", "查看最近正式版本的更新内容，也可以在升级后回顾本次变化。")),
                    C("升级失败时，请先检查网络是否可以访问 GitHub 和本次发布页面，再尝试重新检查更新。"),
                ]),
            new GuideSection(
                "快捷键和鼠标",
                "常用操作一览。",
                [
                    K(
                        ("空格", "打开或关闭当前选中文件的预览"),
                        ("Esc", "关闭预览窗口"),
                        ("方向键", "资源管理器中切换文件；预览获得焦点后交给预览内容"),
                        ("鼠标中键", "预览鼠标下的文件或桌面图标，再次按下关闭"),
                        ("鼠标后退键", "支持的场景中切换到下一个文件"),
                        ("鼠标前进键", "支持的场景中切换到上一个文件")),
                ]),
            new GuideSection(
                "常见问题",
                "先检查最常见的几类原因，通常可以快速恢复预览。",
                [
                    D(
                        ("按空格没有反应", "确认 QuickLook Next 仍在托盘中运行、资源管理器已经选中文件，并检查该文件类型是否有可用插件。"),
                        ("文件打开了但没有内容", "先尝试在托盘中重启软件，再用原生程序打开文件确认文件本身没有损坏。"),
                        ("Office 版式或元素不同", "复杂图表、SmartArt、特殊字体和嵌入对象可能存在兼容差异。自建界面无法处理时可以改用原生预览。"),
                        ("压缩包中的图片不显示", "先升级到最新版本；如果仍然复现，请提供文件类型、操作步骤和一张截图。"),
                        ("内存没有立即下降", "省内存模式按完整空闲时间段工作。刚预览过文件时继续保持预热是预期行为。"),
                        ("更新检查失败", "检查 GitHub 是否可访问，以及代理、防火墙或 DNS 设置是否拦截了更新请求。")),
                ]),
            new GuideSection(
                "问题反馈",
                "提供足够的信息，可以更快确认兼容性或渲染问题。",
                [
                    B(
                        "说明 QuickLook Next 版本和 Windows 版本。",
                        "提供文件类型、文件大小以及是否可以稳定复现。",
                        "附上预览界面截图，必要时录制一小段操作过程。",
                        "保留相关诊断日志，并在反馈时一并说明。",
                        "不要上传包含隐私内容的完整文件；可以先提供去敏样例。"),
                    P("点击窗口底部的 GitHub Issues 可以打开反馈页面。"),
                ]),
        ];
    }

    private static IReadOnlyList<GuideSection> CreateEnglish()
    {
        return
        [
            new GuideSection(
                "Quick Start",
                "Preview files immediately without opening their native app.",
                [
                    P("QuickLook Next runs in the system tray. You do not need to open a main window before previewing a file."),
                    C("The everyday gesture: select a file in File Explorer, press Space to preview, then press Space again or Esc to close."),
                    B(
                        "Start the installed or portable build and confirm the QuickLook Next icon appears in the notification area.",
                        "Select a supported file in File Explorer.",
                        "Press Space to open the preview.",
                        "Press Space or Esc to close it."),
                    P("QuickLook Next can preview common documents, images, videos, and archives without requiring Microsoft Office."),
                ]),
            new GuideSection(
                "Basic Controls",
                "Space, the middle mouse button, and the arrow keys cover the most frequent tasks.",
                [
                    D(
                        ("Space", "Open or close the preview for the selected file."),
                        ("Esc", "Close the current preview window."),
                        ("Arrow keys", "Switch files while File Explorer keeps focus; once the preview has focus, the keys belong to the preview content."),
                        ("Middle click", "Preview the file or desktop icon under the pointer, and middle-click again to close it."),
                        ("Mouse side buttons", "Move between adjacent files in supported File Explorer scenarios."),
                        ("Top bar", "Hidden by default. Move the pointer to the top edge of the preview to reveal it.")),
                    C("Current side-button mapping: Back opens the next file and Forward opens the previous file. Your mouse driver must forward the buttons to Windows."),
                ]),
            new GuideSection(
                "Preview Window Tips",
                "Adjust the reading area, hide navigation, and use each preview type efficiently.",
                [
                    P("The top bar stays out of the way during normal reading. Move the pointer to the top edge when you need refresh or close controls."),
                    B(
                        "PDF and similar documents can show or hide their navigation pane.",
                        "The hidden navigation pane floats over the page instead of continuing to squeeze the reading area.",
                        "Drag the center divider to resize the navigation and content columns.",
                        "The preview window can be moved; resizable previews also accept edge resizing."),
                ]),
            new GuideSection(
                "Supported Previews",
                "Built-in previews cover everyday office, media, text, and developer files.",
                [
                    D(
                        ("Images", "Mouse-wheel zoom and drag-to-pan are supported. Very tall or wide images scroll instead of shrinking to an unreadable size. Large images use a 40 MP decode guard by default."),
                        ("Video", "Video plays directly and is fitted to the window while preserving its original aspect ratio. Unusual codecs may depend on Windows decoder support."),
                        ("PDF", "Page preview, navigation, zoom, and paging are supported. Documents with an outline show it; documents without one show page thumbnails."),
                        ("Office", "Common Word, Excel, and PowerPoint files work without Microsoft Office. The custom renderer preserves fonts, colors, images, charts, and layout where possible."),
                        ("Markdown and HTML", "Rendered content is shown instead of source. Markdown previews remain stable while switching between files."),
                        ("Text and code", "Plain text, logs, code, and configuration files can be inspected immediately."),
                        ("Archives", "Browse an archive and preview supported files inside it without extracting files to the desktop."),
                        ("XMind", "Mind maps render directly and can be zoomed and panned."),
                        ("Other formats", "Built-in previews also cover fonts, databases, email, 3D models, and more. Each plugin controls the features available for its format.")),
                    C("The custom Office renderer provides the consistent in-app look. When a complex element cannot be rendered faithfully, the native preview can act as a compatibility fallback."),
                ]),
            new GuideSection(
                "Tray Menu",
                "Right-click the tray icon to change settings, manage plugins, and check for updates.",
                [
                    D(
                        ("Theme mode", "Follow the system, light, or dark."),
                        ("Language", "Follow the system or choose an interface language."),
                        ("Backdrop mode", "Choose Auto, None, Mica, Acrylic, Acrylic 10, Acrylic 11, or Tabbed."),
                        ("Options", "Toggle startup, close on lost focus, hide the top bar by default, and automatic update checks."),
                        ("Low memory mode", "Release after 90 seconds, 5 minutes, 15 minutes, 30 minutes, 1 hour, never, or turn the mode off. The default is 5 minutes."),
                        ("Preview warm-up", "Warm the 1, 3, 5, or 8 most-used preview families. The default is 3."),
                        ("Check for Updates / Update History", "Check for a new version or read notes from recent stable releases."),
                        ("User Guide", "Open this window to find features and operating instructions."),
                        ("Plugin Market", "Browse and install plugins, or update and remove user-installed plugins."),
                        ("Clean cache", "Review data usage and clear rebuildable cache or the diagnostic log."),
                        ("Restart / Quit", "Restart the preview service or exit QuickLook Next completely.")),
                ]),
            new GuideSection(
                "Appearance",
                "Theme, backdrop, and language are independent settings.",
                [
                    D(
                        ("Theme mode", "Follow System changes with Windows light/dark mode. Light and Dark keep one appearance."),
                        ("Backdrop mode", "Mica and Acrylic provide different materials. Use None when you prefer maximum performance or Windows transparency is disabled."),
                        ("Language", "Follows Windows by default. The full guide is currently written in Simplified Chinese and English; the guide uses English when opened from another interface language.")),
                    P("Images and video should emphasize their own content, so their previews minimize the visual effect of the window material."),
                ]),
            new GuideSection(
                "Warm-up and Low Memory",
                "Trade a small amount of idle memory for faster first previews, then release it while idle.",
                [
                    D(
                        ("Preview warm-up", "The app prepares the preview families you use most often. The default is 3; you can choose 1, 3, 5, or 8. More warm-up usually improves first-preview speed but also increases idle memory."),
                        ("Low memory mode", "The app starts warm. If any preview happens during a time window, warm-up is retained for another complete window. Memory is released only after an entire window contains no preview.")),
                    C("Memory may not drop immediately after closing a preview. With the default setting, a complete 5-minute idle window must pass; another preview starts that window again."),
                ]),
            new GuideSection(
                "Plugin Market",
                "Extend the formats and capabilities available to QuickLook Next.",
                [
                    P("The tray menu's Plugin Market lists installed and available plugins, with search, installation, updates, and removal for user-installed plugins."),
                    B(
                        "Built-in plugins ship and are maintained with the app.",
                        "User plugins are installed in your personal data folder. Restart the app after removal to release loaded files.",
                        "Plugins run with the same system permissions as the app. Install only plugins you trust.",
                        "If installation or an update fails, check the network connection and the integrity of the plugin package."),
                ]),
            new GuideSection(
                "Data, Cache, and Updates",
                "Understand what the app stores, how cleanup works, and how updates are installed.",
                [
                    D(
                        ("Clean cache", "Removes WebView2 cache, old update leftovers, and preview temporary files older than one day. Recent preview files, settings, sign-in state, and logs are kept."),
                        ("Clear diagnostic log", "Removes only the error and diagnostic logs, which are usually needed when reporting a problem."),
                        ("Automatic updates", "Enabled by default. When a release is available, read its notes in the app, download it, and restart to finish installation."),
                        ("Update History", "Shows notes from recent stable releases and helps you review what changed after upgrading.")),
                    C("If an update fails, first check whether GitHub and the release page are reachable, then try checking for updates again."),
                ]),
            new GuideSection(
                "Keyboard and Mouse",
                "A quick reference for the common controls.",
                [
                    K(
                        ("Space", "Open or close the preview for the selected file"),
                        ("Esc", "Close the preview window"),
                        ("Arrow keys", "Switch files in File Explorer; control preview content after the preview gets focus"),
                        ("Middle click", "Preview the file or desktop icon under the pointer; click again to close"),
                        ("Mouse Back", "Open the next file in supported scenarios"),
                        ("Mouse Forward", "Open the previous file in supported scenarios")),
                ]),
            new GuideSection(
                "Troubleshooting",
                "Check the most common causes first; most preview issues can be restored quickly.",
                [
                    D(
                        ("Space does nothing", "Confirm QuickLook Next is still in the tray, File Explorer has a selected file, and the format has an available plugin."),
                        ("The preview opens but stays empty", "Restart the app from the tray, then open the file in its native app to confirm the file itself is valid."),
                        ("Office layout or elements differ", "Complex charts, SmartArt, unusual fonts, and embedded objects can differ. Use the native preview when the custom renderer cannot handle an element."),
                        ("Images inside an archive do not appear", "Update to the latest build first. If it still occurs, include the file type, reproduction steps, and a screenshot."),
                        ("Memory does not drop immediately", "Low memory mode works in complete idle windows. Staying warm immediately after a preview is expected."),
                        ("Update check fails", "Check whether GitHub is reachable and whether a proxy, firewall, or DNS setting blocks the request.")),
                ]),
            new GuideSection(
                "Reporting a Problem",
                "Good details make rendering and compatibility issues much faster to diagnose.",
                [
                    B(
                        "Include the QuickLook Next version and Windows version.",
                        "Describe the file type, file size, and whether the issue is repeatable.",
                        "Attach a screenshot and, when useful, a short recording of the steps.",
                        "Keep the relevant diagnostic log and mention it in the report.",
                        "Do not upload a complete private file. Provide a sanitized sample or describe its structure instead."),
                    P("Use the GitHub Issues button at the bottom of this window to open the feedback page."),
                ]),
        ];
    }
}
