# Builds a user-friendly release package: Build\Release -> Build\Package -> Build\QuickLook-Next-<version>.zip
#
# Layout (since v3.2.0):
#   root:          QuickLook-Next.exe (what the user double-clicks), QuickLook-Next.dll,
#                  QuickLook-Next.deps.json, QuickLook-Next.runtimeconfig.json,
#                  Translations.config, QLPlugin.ico, portable.lock
#   lib\:          every other runtime DLL (third-party dependencies + QuickLook.Common)
#   runtimes\:     native runtime libraries
#   QuickLook.Plugin\: built-in plugins
# A dozen dll/config files are no longer mixed into the root next to the exe.
#
# Usage:
#   .\Scripts\pack-release.ps1             # stage into Build\Package only
#   .\Scripts\pack-release.ps1 -MakeZip    # stage and produce the zip

param(
    [switch]$MakeZip,
    # v3.32.0: one package carries the native libraries of a single architecture (x64 by default).
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$release = Join-Path $root 'Build\Release'
$package = Join-Path $root 'Build\Package'

if (-not (Test-Path $release)) {
    throw "build output not found: $release (run .\build.ps1 first)"
}

$version = & git -C $root describe --always --tags --exclude latest 2>$null
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = '0.0.0'
}

# Rebuild the Package folder
if (Test-Path $package) {
    Remove-Item -LiteralPath $package -Recurse -Force
}
New-Item -ItemType Directory -Path $package | Out-Null

# The root only holds the program entry point and the manifests/configuration it needs
# v3.32.1: keep a copy of QuickLook.Common.dll in the root as well. The 3.31.0 updater (already released
# and still running in the field) checks before installing that "QuickLook-Next.exe and
# QuickLook.Common.dll exist in the root of the extracted folder"; the lib\ layout made it decide this was
# "not a QuickLook-Next package" and refuse the automatic update. The extra 100 KB copy costs the runtime
# nothing (both locations can be found by the resolver).
foreach ($name in @('QuickLook-Next.exe', 'QuickLook-Next.dll',
        'QuickLook-Next.deps.json', 'QuickLook-Next.runtimeconfig.json',
        'Translations.config', 'QLPlugin.ico', 'QuickLook.Common.dll')) {
    $src = Join-Path $release $name
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination $package -Force
    }
}

# Every other managed DLL goes into the lib\ subfolder (the AssemblyResolve fallback at startup searches
# the whole program folder recursively, so assemblies in lib load fine). The main QuickLook-Next.dll must
# stay in the root, because the apphost starts from it.
$lib = Join-Path $package 'lib'
New-Item -ItemType Directory -Path $lib | Out-Null
Get-ChildItem -LiteralPath $release -Filter *.dll -File |
    Where-Object { $_.Name -ne 'QuickLook-Next.dll' } |
    Copy-Item -Destination $lib -Force

# Native runtime libraries and built-in plugins keep their subfolders
if (Test-Path -LiteralPath (Join-Path $release 'runtimes')) {
    Copy-Item -LiteralPath (Join-Path $release 'runtimes') `
        -Destination $package -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $release 'QuickLook.Plugin') `
    -Destination (Join-Path $package 'QuickLook.Plugin') -Recurse -Force

# v3.3.0: collapse the shared dependencies that several plugins each carry into a single copy in lib\
# (the AssemblyResolve fallback at startup loads managed assemblies from lib\; WebView2Loader.dll is a
# native loader and once moved into lib\ is resolved by the WebView2 managed assemblies next to it). Only
# purely managed files or native loaders paired with a managed assembly are handled; anything with its own
# native libraries (MediaInfo, SQLitePCLRaw, freetype, ...) stays where it is. Only byte-identical copies
# are removed.
$dedupeLibNames = @(
    'UtfUnknown.dll',
    'PureSharpCompress.dll',
    'ICSharpCode.SharpZipLib.dll',
    'System.ComponentModel.Composition.dll',
    'Microsoft.Bcl.HashCode.dll',
    'Microsoft.Extensions.Logging.Abstractions.dll',
    'Microsoft.Extensions.DependencyInjection.Abstractions.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'Microsoft.Web.WebView2.Wpf.dll',
    'WebView2Loader.dll',
    # v3.32.0: shared WebView2 host library (used by Html/Markdown/Office/CHM/Mail/Font/SVG)
    'QuickLook.Shared.dll',
    # v3.32.0: other duplicates measured to be byte-identical (the managed MediaInfo assembly in
    # MediaInfoViewer/VideoViewer, MiniExcel in DbViewer/OfficeViewer, ...). Only copies with a matching
    # hash are removed, so listing a few extra names here is safe.
    'QuickLook.MediaInfo.dll',
    'MiniExcel.dll',
    'CommunityToolkit.HighPerformance.dll',
    'Google.Protobuf.dll',
    'Microsoft.Data.Sqlite.dll',
    'SQLitePCLRaw.core.dll',
    'LiteDB.dll',
    'ELFSharp.dll',
    'CsvHelper.dll',
    'MsgReader.dll'
)

# Make sure every file on the dedupe list has a reference copy in lib\ (take one from a plugin folder if not)
foreach ($name in $dedupeLibNames) {
    $libCopy = Join-Path $lib $name
    if (Test-Path -LiteralPath $libCopy) {
        continue
    }
    $first = Get-ChildItem -LiteralPath (Join-Path $package 'QuickLook.Plugin') `
        -Recurse -Filter $name -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $first) {
        Copy-Item -LiteralPath $first.FullName -Destination $libCopy -Force
    }
}

$removedDedup = 0
foreach ($name in $dedupeLibNames) {
    $libCopy = Join-Path $lib $name
    if (-not (Test-Path -LiteralPath $libCopy)) {
        continue
    }
    $libHash = (Get-FileHash -LiteralPath $libCopy -Algorithm SHA256).Hash
    Get-ChildItem -LiteralPath (Join-Path $package 'QuickLook.Plugin') `
        -Recurse -Filter $name -File -ErrorAction SilentlyContinue |
        Where-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq $libHash } |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force
            $removedDedup++
        }
}
Write-Host "Shared dependencies deduplicated: removed $removedDedup duplicate files"

# v3.32.0: each plugin assembly should exist exactly once. Incremental builds do not clean up old output,
# and historically a stray QuickLook.Plugin.HtmlViewer.dll was left inside the PDFViewer folder, which at
# runtime triggered "Assembly with same name is already loaded". Packaging removes the duplicates and keeps
# only the copy in the plugin folder named after the assembly (if there is none, the newest copy wins).
$pluginDllGroups = Get-ChildItem -LiteralPath (Join-Path $package 'QuickLook.Plugin') `
    -Recurse -File -Filter 'QuickLook.Plugin.*.dll' -ErrorAction SilentlyContinue |
    Group-Object Name
$removedDuplicates = 0
foreach ($group in $pluginDllGroups) {
    if ($group.Count -le 1) {
        continue
    }

    $expectedFolder = [System.IO.Path]::GetFileNameWithoutExtension($group.Name)
    $keeper = $group.Group |
        Where-Object { (Split-Path (Split-Path $_.FullName -Parent) -Leaf) -eq $expectedFolder } |
        Select-Object -First 1
    if ($null -eq $keeper) {
        $keeper = $group.Group | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    }

    $group.Group | Where-Object { $_.FullName -ne $keeper.FullName } | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
        $removedDuplicates++
    }
}
if ($removedDuplicates -gt 0) {
    Write-Host "Removed duplicate plugin assembly copies: $removedDuplicates"
}

# The package needs no debug symbols and no .NET Framework era App.config
Get-ChildItem -LiteralPath $package -Recurse -Filter *.pdb |
    Remove-Item -Force
Remove-Item -LiteralPath (Join-Path $package 'QuickLook-Next.dll.config') `
    -ErrorAction SilentlyContinue

# v3.4.0: files the runtime never needs stay out of the package:
# - *.xml files are IntelliSense documentation (about 4 MB)
# - *.deps.json files under plugin folders only matter to the dotnet tooling (plugins go through
#   Assembly.LoadFrom); the QuickLook-Next.deps.json in the root is required by the apphost and must be kept
# - *.dylib files are macOS native libraries (not needed in a Windows package)
Get-ChildItem -LiteralPath $package -Recurse -File |
    Where-Object {
        $_.Extension -in '.xml', '.dylib' -or
        ($_.Name -like '*.deps.json' -and $_.FullName -like '*\QuickLook.Plugin\*')
    } |
    Remove-Item -Force

# v3.4.0: the redundant MediaInfo.dll copy that occasionally lingers in the VideoViewer root (the plugin
# actually loads it from runtimes\win-x64\native\); keep only the runtimes copy.
$videoRootMediaInfo = Join-Path $package 'QuickLook.Plugin\QuickLook.Plugin.VideoViewer\MediaInfo.dll'
$videoRuntimeMediaInfo = Join-Path $package `
    'QuickLook.Plugin\QuickLook.Plugin.VideoViewer\runtimes\win-x64\native\MediaInfo.dll'
if ((Test-Path -LiteralPath $videoRootMediaInfo) -and
    (Test-Path -LiteralPath $videoRuntimeMediaInfo)) {
    Remove-Item -LiteralPath $videoRootMediaInfo -Force
    Write-Host 'Removed the redundant MediaInfo.dll in the VideoViewer root'
}

# v3.10.0/v3.32.0: the package keeps only the native libraries for the target architecture. win-x86 is
# never used (this project produces no x86 build), and folders for other architectures (the WebView2Loader
# copies in ChmViewer / OfficeViewer, ...) are removed as well, so one package never ships several loaders.
$keepArch = if ($Architecture -eq 'arm64') { 'win-arm64' } else { 'win-x64' }
$dropArchDirs = @('win-x86', 'win-arm64', 'win-x64') | Where-Object { $_ -ne $keepArch }
$pluginRoot = Join-Path $package 'QuickLook.Plugin'
foreach ($archDir in $dropArchDirs) {
    Get-ChildItem -LiteralPath $pluginRoot -Recurse -Directory -Filter $archDir -ErrorAction SilentlyContinue |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
            Write-Host "Removed $($_.FullName.Substring($package.Length + 1))"
        }
}

# Portable marker: make the data folder follow the program folder
Set-Content -LiteralPath (Join-Path $package 'portable.lock') `
    -Value 'This file makes QuickLook-Next portable.' -Encoding ASCII

# v3.20.0: a first-run note (above all about the .NET runtime dependency) shipped with the package.
# v3.43.0: added "how updating works / where to look when something goes wrong / the two optional
# switches"; these previously only existed in code comments and commit messages, invisible to users.
# v5.5.0: the file name became Readme.txt - every other file in the package had a Latin name, only this
# one had a Chinese name.
# NOTE: the note text below is intentionally still Chinese; it is the packaged Readme.txt content.
$firstRunNote = @'
QuickLook-Next 使用说明

1. 双击根目录的 QuickLook-Next.exe 即可使用。
2. 选中文件后按空格预览，Esc 关闭。
3. 需要 .NET 10 Desktop Runtime（Windows 10 / 11）。
   如果启动时提示缺少运行时，点击提示窗口中的下载按钮安装，然后重新打开。
   下载地址：https://dotnet.microsoft.com/download/dotnet/10.0
4. 便携模式：数据目录跟随本文件夹（UserData），可整体移动。

更新
----------------------------------------
* 程序每天自动检查一次更新；也可以右键托盘图标 →「检查更新…」手动检查。
* 发现新版本后会询问「立即更新 / 忽略更新」：
  - 立即更新：显示下载进度（可取消），下载完成后自动退出、替换文件并重启；
  - 忽略更新：这个版本不再打扰，下次手动检查时仍会再问一次。
* 更新只替换程序文件，UserData（设置、插件、缓存）不会被改动。
* 更新失败：程序下次启动会弹通知说明原因，详情见
  %TEMP%\QuickLookNext-update.log；也可以直接到发布页手动下载 zip 覆盖安装。

数据与日志
----------------------------------------
* 设置：UserData\QuickLookNext.config（各插件设置在 UserData\QuickLook*.config）
* 出错日志：UserData\QuickLookNext.Exception.log（排查问题时最有用）
* 预览使用统计：UserData\plugin-usage.json（仅本地，用于决定预热哪些格式）
* 占用与清理：托盘菜单 →「数据与缓存…」，可以看到数据目录占了多少、一键清理
  可重建的缓存（WebView2 的着色器/网页缓存、更新残留文件）；登录状态、设置与日志
  不会被删除。

可选开关（写在 UserData\QuickLookNext.config 的 <Settings> 里）
----------------------------------------
* <WarmUpPreviewFamilies>false</WarmUpPreviewFamilies>
  关闭「启动后台预热常用格式」。关掉可省约 20-25MB 内存，
  代价是每类格式第一次预览慢约 90ms。默认 true。
* <WarmUpFamilyCount>3</WarmUpFamilyCount>
  预热几个格式（默认 2，按使用统计选最常用的几个）。
* <WebView2IdleTimeoutSeconds>60</WebView2IdleTimeoutSeconds>
  网页类预览（Markdown / HTML / Office）空闲多少秒后回收 Chromium 内存，
  默认 300，设为 0 表示一直保留。
'@
Set-Content -LiteralPath (Join-Path $package 'Readme.txt') `
    -Value $firstRunNote -Encoding UTF8

# v3.32.0: packaging self-check + size report. Before releasing, confirm the package really contains the
# files startup needs, and make "how big is this package and what dominates it" visible at a glance (in the
# past a hand-made package missed the lib folder or shipped pdbs).
foreach ($required in @('QuickLook-Next.exe', 'QuickLook-Next.dll',
        'QuickLook-Next.deps.json', 'QuickLook-Next.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $required))) {
        throw "package is missing a required file: $required"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $package 'lib\QuickLook.Common.dll'))) {
    throw 'package is missing lib\QuickLook.Common.dll'
}
if (-not (Test-Path -LiteralPath (Join-Path $package 'QuickLook.Plugin'))) {
    throw 'package is missing QuickLook.Plugin'
}

$leftover = @(Get-ChildItem -LiteralPath $package -Recurse -File |
    Where-Object { $_.Extension -in '.pdb', '.xml' })
if ($leftover.Count -gt 0) {
    $leftover | Remove-Item -Force
    Write-Host "Cleaned up debug/documentation files left after packaging: $($leftover.Count)"
}

$packageFiles = Get-ChildItem -LiteralPath $package -Recurse -File
Write-Host ("Package size: {0} MB ({1} files)" -f `
        [math]::Round((($packageFiles | Measure-Object Length -Sum).Sum / 1MB), 1), $packageFiles.Count)
Write-Host 'Top 10 by size:'
$packageFiles | Sort-Object Length -Descending | Select-Object -First 10 | ForEach-Object {
    Write-Host ("  {0,7:N1} MB  {1}" -f ($_.Length / 1MB), $_.FullName.Substring($package.Length + 1))
}

if (-not $MakeZip) {
    Write-Host "Staged into: $package"
    Write-Host "(add -MakeZip to produce the archive)"
    exit 0
}

$zip = Join-Path $root "Build\QuickLook-Next-$version.zip"
Remove-Item -LiteralPath $zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

# Write the zip by hand with forward slashes throughout, so that backslash paths do not make some
# extraction tools (macOS / Linux and friends) treat an entry as a single file name
$base = $package.TrimEnd('\')
$fileStream = [System.IO.File]::Open($zip, 'Create')
$archive = New-Object System.IO.Compression.ZipArchive($fileStream, 'Create')
try {
    Get-ChildItem -LiteralPath $package -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($base.Length + 1).Replace('\', '/')
        $entry = $archive.CreateEntry($relative, 'Optimal')
        $inputStream = [System.IO.File]::OpenRead($_.FullName)
        try {
            $entryStream = $entry.Open()
            try {
                $inputStream.CopyTo($entryStream)
            } finally {
                $entryStream.Dispose()
            }
        } finally {
            $inputStream.Dispose()
        }
    }
} finally {
    $archive.Dispose()
    $fileStream.Dispose()
}
Remove-Item -LiteralPath (Join-Path $package 'portable.lock')

Write-Host "Release package created: $zip"
Write-Host ("Archive size: {0} MB" -f [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1))
