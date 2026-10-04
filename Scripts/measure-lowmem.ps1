# How long does each preview take when every preview is closed before the next one?
# That is the flow a user actually performs, and the one measure-preview.ps1 does not
# cover - it leaves the preview open. This is the script behind the v5.6.2 fix: with
# the low memory mode on, every preview after the first used to cost ~325 ms instead
# of ~120 ms, because the window rebuilt on each close was never warmed.
#
# Usage:  pwsh -NoProfile -File .\Scripts\measure-lowmem.ps1 -Mode on|off -Rounds 4 -File test.png
# (a string, not a bool: "pwsh -File" hands arguments over as strings)

param(
    [ValidateSet('on', 'off')][string]$Mode = 'on',
    [int]$Rounds = 4,
    [string]$File = 'test.png',
    [int]$StartupWaitMs = 9000,
    [int]$TimeoutMs = 15000
)

$ErrorActionPreference = 'Stop'
$LowMemory = $Mode -eq 'on'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
$smoke = Join-Path $root 'ql-smoke'
$timing = Join-Path $smoke 'timing.txt'
$config = Join-Path $root 'Build\Release\UserData\QuickLookNext.config'
$env:QL_SMOKE_DIR = $smoke

# Flip the setting the way the tray menu does.
$xml = [xml](Get-Content -LiteralPath $config -Raw -Encoding UTF8)
if (-not $xml.Settings.LowMemoryMode) {
    $node = $xml.CreateElement('LowMemoryMode')
    $null = $xml.Settings.AppendChild($node)
}
$xml.Settings.LowMemoryMode = if ($LowMemory) { 'true' } else { 'false' }
$xml.Save($config)
Write-Host "LowMemoryMode = $($xml.Settings.LowMemoryMode)" -ForegroundColor Cyan

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Remove-Item -LiteralPath $timing -Force -ErrorAction SilentlyContinue

Start-Process -FilePath $exe -ArgumentList '/autorun', '/test-timing', '/test-no-focusmonitor' | Out-Null
Start-Sleep -Milliseconds $StartupWaitMs

$path = Join-Path $smoke $File
$results = @()

for ($i = 1; $i -le $Rounds; $i++) {
    $before = if (Test-Path -LiteralPath $timing) { @(Get-Content -LiteralPath $timing).Count } else { 0 }
    $t0 = [DateTime]::UtcNow
    Start-Process -FilePath $exe -ArgumentList $path | Out-Null

    $ms = $null
    $budget = [Diagnostics.Stopwatch]::StartNew()
    while ($budget.ElapsedMilliseconds -lt $TimeoutMs) {
        Start-Sleep -Milliseconds 20
        if (-not (Test-Path -LiteralPath $timing)) { continue }
        $lines = @(Get-Content -LiteralPath $timing)
        if ($lines.Count -gt $before) {
            $stamp = [DateTime]::Parse($lines[-1].Split('|')[0], $null,
                [Globalization.DateTimeStyles]::RoundtripKind)
            $ms = [math]::Round(($stamp - $t0).TotalMilliseconds)
            break
        }
    }

    $results += "$File #$i = $(if ($null -ne $ms) { "$ms ms" } else { 'timeout' })"
    Write-Host "  $($results[-1])"

    # Request the same file again to close the preview, so the next round starts
    # from the same state a user would be in.
    Start-Process -FilePath $exe -ArgumentList $path | Out-Null
    Start-Sleep -Milliseconds 700
}

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host ($results -join '  |  ') -ForegroundColor Green
