# 生成"视频健壮性回归"用的样本矩阵（覆盖容器/编码/变体）。
# 依赖 winget 安装的 ffmpeg（Gyan 构建，含 libx264/libx265/libvpx-vp9/libaom-av1）。
#
# 用法：pwsh -NoProfile -File .\Scripts\make-video-matrix.ps1
# 样本默认写到 ql-smoke\video-matrix（该目录在 .gitignore 里，不会进仓库）。
param(
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'ql-smoke\video-matrix')
)

$ErrorActionPreference = 'Stop'

$ff = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $ff) {
    throw 'ffmpeg not found - install it with: winget install --id Gyan.FFmpeg -e'
}
$ffmpeg = $ff.FullName

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Write-Host "输出目录: $OutDir" -ForegroundColor Cyan

function New-Clip {
    # 注意：参数不能叫 $Args（那是 PowerShell 的自动变量，会和 splat 打架）
    param([string]$Name, [string[]]$FfmpegArgs)

    $path = Join-Path $OutDir $Name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }

    $common = @('-hide_banner', '-loglevel', 'error', '-y')
    & $ffmpeg @common @FfmpegArgs $path
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed for $Name" }

    Write-Host ("  {0,-26} {1,8:N0} KB" -f $Name, ((Get-Item -LiteralPath $path).Length / 1KB))
}

# 基础源：3 秒 320x240 测试图 + 440Hz 正弦音
$src = @('-f', 'lavfi', '-i', 'testsrc=size=320x240:rate=25:duration=3', '-f', 'lavfi', '-i', 'sine=frequency=440:duration=3')

# --- 容器 / 编码矩阵 ---
New-Clip 'h264-mp4.mp4'  ($src + @('-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest'))
New-Clip 'h265-mp4.mp4'  ($src + @('-c:v', 'libx265', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-tag:v', 'hvc1', '-c:a', 'aac', '-shortest'))
New-Clip 'h265-10bit-mkv.mkv' ($src + @('-c:v', 'libx265', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p10le', '-c:a', 'aac', '-shortest'))
New-Clip 'vp9-webm.webm' ($src + @('-c:v', 'libvpx-vp9', '-b:v', '250k', '-cpu-used', '5', '-c:a', 'libopus', '-shortest'))
New-Clip 'av1-mkv.mkv'   ($src + @('-c:v', 'libaom-av1', '-cpu-used', '8', '-crf', '45', '-b:v', '0', '-c:a', 'libopus', '-shortest'))
New-Clip 'mpeg2-avi.avi' ($src + @('-c:v', 'mpeg2video', '-q:v', '8', '-c:a', 'libmp3lame', '-shortest'))
New-Clip 'h264-mov.mov'  ($src + @('-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest'))
New-Clip 'wmv2-wmav2.wmv' ($src + @('-c:v', 'wmv2', '-b:v', '400k', '-c:a', 'wmav2', '-shortest'))
New-Clip 'h264-ts.ts'    ($src + @('-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest', '-f', 'mpegts'))
New-Clip 'h264-flv.flv'  ($src + @('-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-c:a', 'libmp3lame', '-shortest', '-f', 'flv'))
New-Clip 'theora-ogv.ogv' ($src + @('-c:v', 'libtheora', '-q:v', '6', '-c:a', 'libvorbis', '-shortest'))

# --- 变体 ---
# 真实竖屏（240x320）
New-Clip 'portrait.mp4' (@('-f', 'lavfi', '-i', 'testsrc=size=240x320:rate=25:duration=3') + @('-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p'))
# 横屏 + 旋转元数据（模拟手机拍的视频）
New-Clip 'rotation-metadata.mp4' (@('-f', 'lavfi', '-i', 'testsrc=size=320x240:rate=25:duration=3') + @('-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-metadata:s:v', 'rotate=90'))
# 4K
New-Clip '4k-h264.mp4' (@('-f', 'lavfi', '-i', 'testsrc=size=3840x2160:rate=25:duration=2') + @('-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '40', '-pix_fmt', 'yuv420p'))
# 6 分钟长视频（静态画面，文件很小）
New-Clip 'long-6min.mp4' (@('-f', 'lavfi', '-i', 'color=c=navy:size=320x240:duration=360:rate=5') + @('-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '45', '-pix_fmt', 'yuv420p'))
# 可变帧率（两段不同帧率拼接）
New-Clip 'vfr.mp4' (@('-f', 'lavfi', '-i', 'testsrc=size=320x240:rate=30:duration=2', '-f', 'lavfi', '-i', 'testsrc=size=320x240:rate=10:duration=2',
        '-filter_complex', '[0:v][1:v]concat=n=2:v=1:a=0[v]', '-map', '[v]') + @('-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-fps_mode', 'vfr'))
# 无音轨
New-Clip 'no-audio.mp4' (@('-f', 'lavfi', '-i', 'testsrc=size=320x240:rate=25:duration=3') + @('-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-an'))

# --- 纯音频 ---
New-Clip 'audio-only.mp3' (@('-f', 'lavfi', '-i', 'sine=frequency=440:duration=3') + @('-c:a', 'libmp3lame'))
New-Clip 'audio-only.m4a' (@('-f', 'lavfi', '-i', 'sine=frequency=440:duration=3') + @('-c:a', 'aac'))

# --- 路径变体 ---
$unicode = Join-Path $OutDir '中文 名称 测试.mp4'
Copy-Item -LiteralPath (Join-Path $OutDir 'h264-mp4.mp4') -Destination $unicode -Force
Write-Host ("  {0,-26} {1,8:N0} KB" -f (Split-Path $unicode -Leaf), ((Get-Item -LiteralPath $unicode).Length / 1KB))

# --- 损坏文件：截断到 40% ---
$good = Join-Path $OutDir 'h264-mp4.mp4'
$broken = Join-Path $OutDir 'truncated.mp4'
$bytes = [IO.File]::ReadAllBytes($good)
[IO.File]::WriteAllBytes($broken, $bytes[0..([int]($bytes.Length * 0.4))])
Write-Host ("  {0,-26} {1,8:N0} KB" -f 'truncated.mp4', ((Get-Item -LiteralPath $broken).Length / 1KB))

# --- 空文件 / 只有文件头 ---
$empty = Join-Path $OutDir 'empty.mp4'
[IO.File]::WriteAllBytes($empty, [byte[]]@())
Write-Host ("  {0,-26} {1,8:N0} KB" -f 'empty.mp4', 0)

Write-Host '矩阵生成完成' -ForegroundColor Cyan
