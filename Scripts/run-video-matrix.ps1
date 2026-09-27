# Video robustness regression: preview every sample in ql-smoke\video-matrix in turn and record the
# "request -> content ready" duration, the preview window title, and the number of new error log lines
# that preview produced.
#
# Usage:
#   pwsh -NoProfile -File .\Scripts\run-video-matrix.ps1                      # everything
#   pwsh -NoProfile -File .\Scripts\run-video-matrix.ps1 -TimeoutMs 20000
param(
    [int]$TimeoutMs = 15000,
    [int]$StartupWaitMs = 1500,
    [string]$Filter = '*'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
$smoke = Join-Path $root 'ql-smoke'
$matrix = Join-Path $smoke 'video-matrix'
$timing = Join-Path $smoke 'timing.txt'
$log = Join-Path $root 'Build\Release\UserData\QuickLookNext.Exception.log'
$env:QL_SMOKE_DIR = $smoke

if (-not (Test-Path -LiteralPath $matrix)) {
    throw "Run .\Scripts\make-video-matrix.ps1 first to generate the samples"
}

Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public class PrevWin {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int c);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  public static string Find(uint pid, string contains) {
    string r = "";
    EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h, out p);
      if (p==pid && IsWindowVisible(h)) {
        var sb=new StringBuilder(512); GetWindowText(h,sb,512);
        if (sb.Length > 0 && sb.ToString().Contains(contains)) r = sb.ToString();
      } return true; }, IntPtr.Zero);
    return r; } }
"@

function Start-InstrumentedApp {
    Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Remove-Item -LiteralPath $timing -Force -ErrorAction SilentlyContinue

    $p = Start-Process -FilePath $exe -ArgumentList '/autorun', '/test-timing', '/test-no-focusmonitor' -PassThru
    Start-Sleep -Milliseconds $StartupWaitMs
    return $p
}

# Start-Process -ArgumentList does not add quotes on its own: a path with spaces has to be wrapped here,
# otherwise the child process receives two arguments (the first version of this script hit exactly that
# trap and produced a false report of "paths with spaces fail to preview").
function Request-Preview([string]$Path) {
    Start-Process -FilePath $exe -ArgumentList "`"$Path`"" | Out-Null
}

$app = Start-InstrumentedApp

$files = Get-ChildItem -LiteralPath $matrix -File | Where-Object { $_.Name -like $Filter } | Sort-Object Name
$rows = @()

Write-Host ("{0,-26} {1,10} {2,6} {3}" -f 'File', 'Duration', 'ErrLines', 'Window title') -ForegroundColor Cyan

foreach ($file in $files) {
    # If the previous sample crashed the app, restart an instance with test-timing here, otherwise none
    # of the remaining samples can be measured
    $restarted = $false
    if (-not (Get-Process -Id $app.Id -ErrorAction SilentlyContinue)) {
        $app = Start-InstrumentedApp
        $restarted = $true
    }

    $path = $file.FullName
    $logBefore = if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue).Count } else { 0 }
    $timingBefore = if (Test-Path -LiteralPath $timing) { @(Get-Content -LiteralPath $timing).Count } else { 0 }

    $t0 = [DateTime]::UtcNow
    Request-Preview $path

    $stamp = $null
    $budget = [Diagnostics.Stopwatch]::StartNew()
    while ($budget.ElapsedMilliseconds -lt $TimeoutMs) {
        Start-Sleep -Milliseconds 25
        if (-not (Test-Path -LiteralPath $timing)) { continue }
        $lines = @(Get-Content -LiteralPath $timing)
        if ($lines.Count -gt $timingBefore) {
            $stamp = [DateTime]::Parse($lines[-1].Split('|')[0], $null, [Globalization.DateTimeStyles]::RoundtripKind)
            break
        }
    }

    $ms = if ($stamp) { [math]::Round(($stamp - $t0).TotalMilliseconds) } else { $null }
    $died = -not [bool](Get-Process -Id $app.Id -ErrorAction SilentlyContinue)
    $title = [PrevWin]::Find($app.Id, $file.Name)
    $logAfter = if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue).Count } else { 0 }
    $newLog = [math]::Max(0, $logAfter - $logBefore)

    $result = if ($died) { 'app crashed' } elseif ($ms -ne $null) { "$ms ms" } else { 'timed out' }
    Write-Host ("{0,-26} {1,10} {2,6} {3}" -f $file.Name, $result, $newLog, $title)

    $rows += [PSCustomObject]@{
        File        = $file.Name
        Ms          = $ms
        TimedOut    = ($ms -eq $null)
        Died        = $died
        NewLogLines = $newLog
        Title       = $title
    }

    # Requesting the same file again closes the preview, so the next sample starts from a clean state
    if (-not $died) { Request-Preview $path }
    Start-Sleep -Milliseconds 400
}

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force

$out = Join-Path $smoke 'video-matrix-results.txt'
$rows | Format-Table -AutoSize | Out-String -Width 200 | Set-Content -LiteralPath $out -Encoding UTF8
$rows | ForEach-Object {
    "{0}|ms={1}|timeout={2}|died={3}|log={4}|title={5}" -f $_.File, $_.Ms, $_.TimedOut, $_.Died, $_.NewLogLines, $_.Title
} | Add-Content -LiteralPath $out -Encoding UTF8

$timedOut = @($rows | Where-Object { $_.TimedOut })
$died = @($rows | Where-Object { $_.Died })
$withLog = @($rows | Where-Object { $_.NewLogLines -gt 0 })

Write-Host ''
Write-Host ("{0} samples: {1} crashed, {2} timed out, {3} produced error log lines" -f $rows.Count, $died.Count, $timedOut.Count, $withLog.Count) `
    -ForegroundColor ($(if ($died.Count -eq 0 -and $timedOut.Count -eq 0 -and $withLog.Count -eq 0) { 'Green' } else { 'Yellow' }))
if ($died.Count -gt 0) { Write-Host ('crashed: ' + (($died | ForEach-Object { $_.File }) -join ', ')) -ForegroundColor Red }
if ($timedOut.Count -gt 0) { Write-Host ('timed out: ' + (($timedOut | ForEach-Object { $_.File }) -join ', ')) -ForegroundColor Yellow }
if ($withLog.Count -gt 0) { Write-Host ('with error log lines: ' + (($withLog | ForEach-Object { $_.File }) -join ', ')) -ForegroundColor Yellow }
Write-Host "Results written to: $out"
