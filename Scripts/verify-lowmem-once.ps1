# Does the low memory mode restart once per preview session, or does it keep scanning
# and restarting? Three phases, with a short wait so the run stays quick:
#   1. idle without previewing        -> must NOT restart (no scanning)
#   2. one preview, then idle         -> must restart exactly once
#   3. idle again after that restart  -> must NOT restart again
#
# Usage: pwsh -NoProfile -File .\Scripts\verify-lowmem-once.ps1 -WaitSeconds 30

param(
    [int]$WaitSeconds = 30,
    [string]$File = 'test.png'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
$smoke = Join-Path $root 'ql-smoke'
$config = Join-Path $root 'Build\Release\UserData\QuickLookNext.config'
$env:QL_SMOKE_DIR = $smoke

$doc = [xml](Get-Content -LiteralPath $config -Raw -Encoding UTF8)
foreach ($key in 'LowMemoryMode', 'LowMemoryReleaseSeconds') {
    $node = $doc.DocumentElement.SelectSingleNode($key)
    if (-not $node) { $node = $doc.DocumentElement.AppendChild($doc.CreateElement($key)) }
    $node.InnerText = if ($key -eq 'LowMemoryMode') { 'true' } else { "$WaitSeconds" }
}
$doc.Save($config)
Write-Host "low memory mode on, release after $WaitSeconds s" -ForegroundColor Cyan

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Start-Process -FilePath $exe -ArgumentList '/autorun', '/test-no-focusmonitor' | Out-Null
Start-Sleep -Seconds 8

function Pid() {
    return (Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Select-Object -First 1).Id
}

$start = Pid
Write-Host "phase 1: idle for ${WaitSeconds}s2 without previewing ..." -ForegroundColor DarkGray
Start-Sleep -Seconds ($WaitSeconds * 2)
$after1 = Pid
Write-Host ("  pid {0} -> {1}   {2}" -f $start, $after1,
    $(if ($after1 -eq $start) { 'no restart (correct)' } else { 'RESTARTED (wrong)' })) `
    -ForegroundColor $(if ($after1 -eq $start) { 'Green' } else { 'Red' })

Write-Host 'phase 2: one preview, then idle ...' -ForegroundColor DarkGray
$path = Join-Path $smoke $File
Start-Process -FilePath $exe -ArgumentList $path | Out-Null
Start-Sleep -Seconds 4
Start-Process -FilePath $exe -ArgumentList $path | Out-Null   # close it
Start-Sleep -Seconds ($WaitSeconds + 20)
$after2 = Pid
Write-Host ("  pid {0} -> {1}   {2}" -f $after1, $after2,
    $(if ($after2 -ne $after1) { 'restarted once (correct)' } else { 'did NOT restart (wrong)' })) `
    -ForegroundColor $(if ($after2 -ne $after1) { 'Green' } else { 'Red' })

Write-Host "phase 3: idle again for ${WaitSeconds}s2 ..." -ForegroundColor DarkGray
Start-Sleep -Seconds ($WaitSeconds * 2)
$after3 = Pid
Write-Host ("  pid {0} -> {1}   {2}" -f $after2, $after3,
    $(if ($after3 -eq $after2) { 'no second restart (correct)' } else { 'RESTARTED AGAIN (wrong)' })) `
    -ForegroundColor $(if ($after3 -eq $after2) { 'Green' } else { 'Red' })

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
