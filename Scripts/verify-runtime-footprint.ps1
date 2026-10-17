# v6.0.0: Share and OCR were removed, so the app must no longer carry the WinRT
# projection. Fail fast if an incremental build or a stale output folder brings
# either assembly back.

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

Write-Host 'Runtime footprint check passed: no WinRT projection or runtime in the output.'
