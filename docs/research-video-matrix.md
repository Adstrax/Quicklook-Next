# 视频预览健壮性回归（2026-09-19）

用一批有代表性的视频把"预览一张视频"这条路走一遍：打开速度、是否出画面、失败时是否优雅、会不会崩。
样本矩阵覆盖容器/编码/变体，共 **22 个**，全部由 ffmpeg 生成（脚本在仓库里，可复跑）。

## 结论

| | |
|---|---|
| 22/22 样本正常预览 | 出画面耗时 299–1422 ms（中位数约 350 ms） |
| 崩溃 | **0**（修复前：见下面的 find #1） |
| 超时/无预览 | **0** |
| 故意损坏的文件 | 2 个（0 字节、截断），均**优雅报错**并写日志，不再崩溃 |

覆盖到的格式：H.264/MP4、H.265（8-bit + 10-bit）/MP4+MKV、AV1/MKV、VP9+Opus/WebM、MPEG-2/AVI、
WMV2+WMA/WMV、H.264/MOV、H.264/TS、H.264+MP3/FLV、Theora+Vorbis/OGV、4K H.264、6 分钟长视频、
竖屏 240×320、带旋转元数据的横屏、可变帧率、无音轨、纯音频（MP3/M4A）、中文+带空格路径、
截断文件、0 字节文件。

## find #1：打不开的视频会**崩溃整个应用**（已修）

`QuickLook.Plugin.VideoViewer.ViewerPanel.MediaFailed` 前两行直接清空缩略图（`videoThumbnail.Source`
/ `Visibility`），但 **WPFMediaKit 是在自己的工作线程上抛这个回调的** —— 从非 UI 线程碰视觉树会抛
`InvalidOperationException: The calling thread cannot access this object`，而那个异常落在没有捕获的
工作线程上，**整个进程直接退出**。日志里两条 8 行堆栈（0 字节文件、截断文件）就是它。

用户感受：预览一个打不开的视频（损坏文件、编码缺失、容器不支持的 `.mp4`）→ 整个 QuickLook 直接消失，
而不是提示"这个视频打不开"。这与上游 [#1768](https://github.com/QL-Win/QuickLook/issues/1768)
"Cannot open videos" 是同一类现象。

修复（5.0.9）：所有 UI 操作都挪到窗口 Dispatcher 上执行；用户看到一句话提示
（`VV_PlaybackFailed`：无法播放此视频 / This video could not be played），完整异常写进日志。
顺带把原来直接显示在面板上的**原始堆栈**换成了这句提示。

## find #2：脚本自身的坑（已修，记录以免重犯）

第一版扫描脚本用 `Start-Process -ArgumentList $path` 请求预览 —— PowerShell 的 `-ArgumentList`
**不会自动加引号**，所以路径带空格时子进程收到的是两个参数、应用拿到的路径被截断，表现为
"带空格的视频文件预览失败"。一度误判成"中文路径有问题"，隔离测试后确认：**中文没问题，空格才有问题**，
而且那是脚本的错，不是应用的错。修正后 `中文 名称 测试.mp4` 正常预览。

## 怎么复跑

```powershell
# 1) 需要 ffmpeg（winget install --id Gyan.FFmpeg -e）；生成 22 个样本到 ql-smoke\video-matrix
pwsh -NoProfile -File .\Scripts\make-video-matrix.ps1

# 2) 逐个预览并输出 耗时 / 崩溃 / 日志新增 / 窗口标题，结果写到 ql-smoke\video-matrix-results.txt
pwsh -NoProfile -File .\Scripts\run-video-matrix.ps1
```

扫描脚本会在应用被某个样本搞崩后**自动重启**带 `/test-timing` 的实例，所以崩一次不会污染后面的样本。

## 固化成防线

`test.ps1` 现在会先把 `test.mp4` 截断成 `test-corrupt.mp4` 并预览它（不带 ffmpeg 也能造出来）：

- 断言 **进程存活**（这就是 find #1 的守卫：崩溃会让整条冒烟测试失败）
- 断言 **确实写了错误日志**（失败必须被报告，而不是静默吞掉）

## 没覆盖到的

- 网络路径（UNC / 映射盘）与超长路径（>260 字符）：需要真实环境，未纳入
- HDR / Dolby Vision、以及需要额外解码器的编码（如 AV1 硬解路径）：样本是软解友好的
- 双显卡切换场景：上游 [#1968](https://github.com/QL-Win/QuickLook/issues/1968) 指向核显/独显切换，
  我们已有 `UseHardwareAcceleration` 开关（OPTIONS.md），但没有自动化手段制造双 GPU 环境
- 播放中的交互（暂停/拖动进度/音量）：只测了"出画面 + 不崩"，交互仍靠人工
