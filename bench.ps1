# bench.ps1 - preview latency benchmark
#
# Relies on the hidden /test-timing hook built into QuickLookNext: every time a preview's content
# becomes ready (the spinner disappears), the resident instance appends a timestamp with the file path to
# %TEMP%\ql-smoke\timing.txt. This script issues one preview request per test file and computes the
# latency as "request time -> ready time".
#
# Usage: .\bench.ps1 [-Rounds 2]
# Prerequisite: run test.ps1 first (it prepares the test files and builds), or build Release yourself.

param([int]$Rounds = 2)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
# v1.2.36: keep bench files inside the repository instead of the C: temp
# folder; the app's diagnostics follow via QL_SMOKE_DIR.
$smoke = Join-Path $root 'ql-smoke'
$env:QL_SMOKE_DIR = $smoke
$timing = Join-Path $smoke 'timing.txt'
$startup = Join-Path $smoke 'startup.txt'

if (-not (Test-Path $exe)) {
    throw "$exe not found - run test.ps1 to build first"
}

$fileNames = @('test.png', 'test.txt', 'test.md', 'test.json', 'test.zip', 'test.ttf', 'test.pdf')
$files = $fileNames | ForEach-Object { Join-Path $smoke $_ } |
    Where-Object { Test-Path -LiteralPath $_ }
if ($files.Count -eq 0) {
    throw "test files are missing ($smoke) - run test.ps1 first"
}

if (Test-Path -LiteralPath $timing) {
    Remove-Item -LiteralPath $timing -Force
}
if (Test-Path -LiteralPath $startup) {
    Remove-Item -LiteralPath $startup -Force
}

$p = Start-Process -FilePath $exe -ArgumentList '/autorun', '/test-timing', '/test-startup' -PassThru
$requests = [System.Collections.Generic.List[object]]::new()
try {
    Start-Sleep -Seconds 14   # wait for the plugins to finish loading
    foreach ($round in 1..$Rounds) {
        foreach ($f in $files) {
            $t0 = Get-Date
            & $exe $f | Out-Null
            $requests.Add([pscustomobject]@{ Path = $f; RequestTime = $t0 })
            Start-Sleep -Seconds 15   # leave plenty of time for the preview to finish
        }
    }
}
finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $timing)) {
    Write-Host "no timing entries were produced (timing.txt is missing)" -ForegroundColor Red
    exit 1
}

# Parse the ready timestamps (grouped by file path; the same file may have entries from several rounds)
$ready = @{}
foreach ($line in Get-Content -LiteralPath $timing) {
    $parts = $line.Split('|')
    if ($parts.Count -lt 2) { continue }
    $t = [DateTime]::Parse($parts[0]).ToLocalTime()
    $path = $parts[1]
    if (-not $ready.ContainsKey($path)) {
        $ready[$path] = [System.Collections.Generic.List[DateTime]]::new()
    }
    $ready[$path].Add($t)
}

# Match each request with the earliest ready entry that comes after the request time
$results = [System.Collections.Generic.List[object]]::new()
foreach ($r in $requests) {
    $candidates = @($ready[$r.Path] | Where-Object { $_ -ge $r.RequestTime.AddSeconds(-2) })
    if ($candidates.Count -gt 0) {
        $rt = ($candidates | Sort-Object)[0]
        $latency = [math]::Round(($rt - $r.RequestTime).TotalMilliseconds, 0)
        $results.Add([pscustomobject]@{ File = [IO.Path]::GetFileName($r.Path); LatencyMs = $latency })
        $ready[$r.Path].Remove($rt) | Out-Null
    }
    else {
        $results.Add([pscustomobject]@{ File = [IO.Path]::GetFileName($r.Path); LatencyMs = $null })
    }
}

Write-Host "Per-run results:" -ForegroundColor Cyan
$results | Format-Table -AutoSize

Write-Host "Summary (ms):" -ForegroundColor Cyan
$results | Group-Object File | ForEach-Object {
    $lats = @($_.Group | Where-Object { $null -ne $_.LatencyMs } | Select-Object -ExpandProperty LatencyMs)
    [pscustomobject]@{
        File = $_.Name
        Runs = $lats.Count
        Average = if ($lats.Count) { [math]::Round(($lats | Measure-Object -Average).Average, 0) } else { 'N/A' }
        Fastest = if ($lats.Count) { ($lats | Measure-Object -Minimum).Minimum } else { 'N/A' }
        Slowest = if ($lats.Count) { ($lats | Measure-Object -Maximum).Maximum } else { 'N/A' }
    }
} | Format-Table -AutoSize

if (Test-Path -LiteralPath $startup) {
    $startupLines = Get-Content -LiteralPath $startup
    $end = $startupLines | Where-Object { $_ -match '\|onstartup-end$' } |
        ForEach-Object { $_.Split('|')[0] } | Select-Object -First 1
    $plugins = $startupLines | Where-Object { $_ -match '\|plugins-inited$' } |
        ForEach-Object { $_.Split('|')[0] } | Select-Object -First 1
    Write-Host "Startup timings (ms):" -ForegroundColor Cyan
    [pscustomobject]@{
        UIReady = if ($end) { "$end ms" } else { 'N/A' }
        PluginsReady = if ($plugins) { "$plugins ms" } else { 'N/A' }
    } | Format-Table -AutoSize
}
