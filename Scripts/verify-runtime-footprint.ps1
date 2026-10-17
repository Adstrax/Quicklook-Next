# v6.0.0: Share and OCR were removed, so the app must no longer carry the WinRT
# projection. v6.0.1 also keeps shared WebView2 and MediaInfo assemblies in the
# application root instead of copying them into every plugin folder.

param(
    [string]$ReleasePath = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleasePath)) {
    $ReleasePath = Join-Path $root 'Build\Release'
}

$forbiddenFiles = @('Microsoft.Windows.SDK.NET.dll', 'WinRT.Runtime.dll')
foreach ($name in $forbiddenFiles) {
    $path = Join-Path $ReleasePath $name
    if (Test-Path -LiteralPath $path) {
        throw "Runtime footprint check failed: stale $name is present in $ReleasePath"
    }
}

$depsPath = Join-Path $ReleasePath 'QuickLook-Next.deps.json'
if (-not (Test-Path -LiteralPath $depsPath)) {
    throw "Runtime footprint check failed: missing $depsPath"
}

$deps = Get-Content -LiteralPath $depsPath -Raw
foreach ($name in @('Microsoft.Windows.SDK.NET', 'WinRT.Runtime')) {
    if ($deps.Contains($name, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime footprint check failed: QuickLook-Next.deps.json still references $name"
    }
}

$sharedRuntime = @(
    'QuickLook.Shared.dll',
    'QuickLook.MediaInfo.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.Wpf.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'WebView2Loader.dll'
)

foreach ($name in $sharedRuntime) {
    if (-not (Test-Path -LiteralPath (Join-Path $ReleasePath $name))) {
        throw "Runtime footprint check failed: the shared $name is missing from the application root"
    }

    $pluginRoot = Join-Path $ReleasePath 'QuickLook.Plugin'
    $duplicates = @(Get-ChildItem -LiteralPath $pluginRoot -Recurse -File -Filter $name)
    if ($duplicates.Count -gt 0) {
        $folders = ($duplicates | ForEach-Object { $_.Directory.FullName }) -join '; '
        throw "Runtime footprint check failed: $name is duplicated in plugin folders: $folders"
    }
}

Write-Host 'Runtime footprint check passed: no WinRT projection or duplicated shared runtime.'
