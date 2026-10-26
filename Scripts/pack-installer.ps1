# Builds QuickLook-Next-Setup-<version>.exe and QuickLook-Next-<version>.zip in
# Build\Release.
#
# The installer consumes the staged release package and deliberately omits
# portable.lock, so settings and caches use the normal installed-app locations.
#
# Usage:
#   .\Scripts\pack-installer.ps1 -Version 6.5.0-test.1
#   .\Scripts\pack-installer.ps1 -Version 6.5.0-test.1 -SkipBuild

param(
    [string]$Version = $env:QL_BUILD_VERSION,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $root 'Version.txt'
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
}

$match = [regex]::Match(
    $Version,
    '^(?<numeric>\d+\.\d+\.\d+)(?:-(?<suffix>[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*))?$')
if (-not $match.Success) {
    throw "invalid installer version: $Version"
}

$numericVersion = $match.Groups['numeric'].Value
$env:QL_BUILD_VERSION = $Version
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'

& (Join-Path $PSScriptRoot 'update-version.ps1')

if (-not $SkipBuild) {
    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

& (Join-Path $PSScriptRoot 'pack-release.ps1') -MakeZip

$iscc = if ($env:INNO_SETUP_ISCC) {
    $env:INNO_SETUP_ISCC
} else {
    'E:\Codex\.cache\innosetup\6.7.3\tools\ISCC.exe'
}
if (-not (Test-Path -LiteralPath $iscc)) {
    throw "Inno Setup compiler not found: $iscc"
}

$package = Join-Path $root 'Build\Package'
$release = Join-Path $root 'Build\Release'
$script = Join-Path $root 'QuickLookNext.Installer\Inno\QuickLook-Next.iss'
$setup = Join-Path $release "QuickLook-Next-Setup-$Version.exe"

New-Item -ItemType Directory -Path $release -Force | Out-Null
& $iscc `
    "/DMyAppVersion=$Version" `
    "/DMyAppNumericVersion=$numericVersion" `
    "/DSourceDir=$package" `
    "/DOutputDir=$release" `
    $script
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $setup)) {
    throw "installer was not produced: $setup"
}

$file = Get-Item -LiteralPath $setup
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
Write-Host ("Installer: {0}" -f $file.FullName)
Write-Host ("Size: {0:N1} MB" -f ($file.Length / 1MB))
Write-Host "SHA-256: $hash"
