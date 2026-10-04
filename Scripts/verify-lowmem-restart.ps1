# Does the low memory mode restart itself once the user has stopped previewing, and
# does the memory come back to the cold baseline afterwards?
#
# Usage: pwsh -NoProfile -File .\Scripts\verify-lowmem-restart.ps1 -WaitSeconds 90

param(
    [int]$WaitSeconds = 90,
    [string]$File = 'test.png'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
$smoke = Join-Path $root 'ql-smoke'
$data = Join-Path $root 'Build\Release\UserData'
$config = Join-Path $data 'QuickLookNext.config'
$log = Join-Path $data 'QuickLookNext.Exception.log'
$env:QL_SMOKE_DIR = $smoke

$doc = [xml](Get-Content -LiteralPath $config -Raw -Encoding UTF8)
foreach ($key in 'LowMemoryMode', 'LowMemoryReleaseSeconds') {
    $node = $doc.DocumentElement.SelectSingleNode($key)
    if (-not $node) { $node = $doc.DocumentElement.AppendChild($doc.CreateElement($key)) }
    $node.InnerText = if ($key -eq 'LowMemoryMode') { 'true' } else { "$WaitSeconds" }
}
$doc.Save($config)
Write-Host "LowMemoryMode=true, release after $WaitSeconds s" -ForegroundColor Cyan

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$logLengthBefore = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }

Start-Process -FilePath $exe -ArgumentList '/autorun', '/test-no-focusmonitor' | Out-Null
Start-Sleep -Seconds 9

function Report([string]$label) {
    $proc = Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) {
        Write-Host ("  {0,-20} no process" -f $label)
        return $null
    }
    Write-Host ("  {0,-20} pid={1,-7} private {2,6:N1} MB" -f $label, $proc.Id, ($proc.PrivateMemorySize64 / 1MB))
    return $proc
}

$first = Report 'before preview'

$path = Join-Path $smoke $File
Start-Process -FilePath $exe -ArgumentList $path | Out-Null
Start-Sleep -Seconds 4
Report 'preview open' | Out-Null

Start-Process -FilePath $exe -ArgumentList $path | Out-Null
Start-Sleep -Seconds 3
Report 'just closed' | Out-Null

Write-Host "  waiting $WaitSeconds s for the release ..." -ForegroundColor DarkGray
Start-Sleep -Seconds ($WaitSeconds + 20)

$after = Report 'after the release'
Write-Host ''

if ($after -and $first -and $after.Id -ne $first.Id) {
    Write-Host "restarted: pid $($first.Id) -> $($after.Id)" -ForegroundColor Green
} else {
    Write-Host 'the tray process did NOT restart' -ForegroundColor Red
}

if (Test-Path $log) {
    $text = Get-Content -LiteralPath $log -Raw -Encoding UTF8
    $from = [Math]::Min($logLengthBefore, $text.Length)
    $hit = @(($text.Substring($from) -split "`n") | Where-Object { $_ -match 'restarting to release' })
    if ($hit) {
        Write-Host ("log: " + $hit[-1].Trim()) -ForegroundColor Green
    } else {
        Write-Host 'log: no restart line' -ForegroundColor Yellow
    }
}

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
