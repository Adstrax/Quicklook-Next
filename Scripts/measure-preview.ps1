# Measure preview latency: cold-start the app, preview several files in a row, and print the
# "request -> content ready" duration for each one.
#
# Usage: pwsh -NoProfile -File .\Scripts\measure-preview.ps1
#        pwsh -NoProfile -File .\Scripts\measure-preview.ps1 -Files test.png,test.md
#
# Note: the request uses the same path as opening from the shell (a second QuickLook-Next.exe forwards
# the path to the resident instance), so the numbers include process start-up and message forwarding.

param(
    [string[]]$Files = @('test.png', 'test.md', 'test.pptx', 'test.xlsx'),
    [int]$StartupWaitMs = 1500,
    [int]$TimeoutMs = 20000,
    # Also record the idle memory sample (ql-smoke\memory.txt) for baseline runs.
    [switch]$Memory
)

$ErrorActionPreference = 'Stop'
# In -File mode "a,b" arrives as a single string, so split it apart here.
$Files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
$smoke = Join-Path $root 'ql-smoke'
$env:QL_SMOKE_DIR = $smoke
$timing = Join-Path $smoke 'timing.txt'

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Remove-Item -LiteralPath $timing -Force -ErrorAction SilentlyContinue

$appArgs = @('/autorun', '/test-timing', '/test-no-focusmonitor')
if ($Memory) { $appArgs += '/test-memory' }

if ($Memory) { Remove-Item -LiteralPath (Join-Path $smoke 'memory.txt') -Force -ErrorAction SilentlyContinue }

Start-Process -FilePath $exe -ArgumentList $appArgs
Start-Sleep -Milliseconds $StartupWaitMs

Write-Host ("{0,-18} {1}" -f 'File', 'Request->ready')
foreach ($file in $Files) {
    $path = Join-Path $smoke $file
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host ("{0,-18} skipped (file not found)" -f $file)
        continue
    }

    $before = if (Test-Path -LiteralPath $timing) { @(Get-Content -LiteralPath $timing).Count } else { 0 }
    $t0 = [DateTime]::UtcNow
    Start-Process -FilePath $exe -ArgumentList $path | Out-Null

    $stamp = $null
    $budget = [Diagnostics.Stopwatch]::StartNew()
    while ($budget.ElapsedMilliseconds -lt $TimeoutMs) {
        Start-Sleep -Milliseconds 20
        if (-not (Test-Path -LiteralPath $timing)) { continue }

        $lines = @(Get-Content -LiteralPath $timing)
        if ($lines.Count -gt $before) {
            $stamp = [DateTime]::Parse($lines[-1].Split('|')[0], $null,
                [Globalization.DateTimeStyles]::RoundtripKind)
            break
        }
    }

    if ($stamp) {
        $ms = [math]::Round(($stamp - $t0).TotalMilliseconds)
        Write-Host ("{0,-18} {1,5} ms" -f $file, $ms)
    }
    else {
        Write-Host ("{0,-18} timed out" -f $file)
    }

    Start-Sleep -Milliseconds 400
}

# When -Memory was given, hand the last idle sample to the caller (the smoke test
# records it as the memory baseline) before the process goes away.
if ($Memory) {
    $memFile = Join-Path $smoke 'memory.txt'
    if (Test-Path -LiteralPath $memFile) {
        Write-Host ('memory|' + (Get-Content -LiteralPath $memFile | Select-Object -Last 1))
    }
}

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
