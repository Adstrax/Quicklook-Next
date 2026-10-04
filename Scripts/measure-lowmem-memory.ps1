# The other half of the low memory mode contract: after the preview session has been
# idle for PreviewSession.IdleTimeout, is the memory of the parked preview window
# really given back? Point -Exe at another build (e.g. an extracted older release) to
# compare.
#
# Usage: pwsh -NoProfile -File .\Scripts\measure-lowmem-memory.ps1 -Mode on -IdleSeconds 100

param(
    [ValidateSet('on', 'off')][string]$Mode = 'on',
    [int]$IdleSeconds = 100,
    [string]$File = 'test.png',
    [int]$StartupWaitMs = 9000,
    # Point this at another build (e.g. an extracted older release) to compare.
    [string]$Exe = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = if ($Exe) { $Exe } else { Join-Path $root 'Build\Release\QuickLook-Next.exe' }
$smoke = Join-Path $root 'ql-smoke'
$config = Join-Path (Split-Path -Parent $exe) 'UserData\QuickLookNext.config'
$env:QL_SMOKE_DIR = $smoke

Write-Host "exe = $exe" -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $config)) {
    # A fresh copy has no settings file yet: start it once so it writes one.
    $bootstrap = Start-Process -FilePath $exe -ArgumentList '/autorun', '/test-no-focusmonitor' -PassThru
    Start-Sleep -Seconds 8
    Get-Process -Id $bootstrap.Id -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
}

$doc = [xml](Get-Content -LiteralPath $config -Raw -Encoding UTF8)
$node = $doc.DocumentElement.SelectSingleNode('LowMemoryMode')
if (-not $node) {
    $node = $doc.CreateElement('LowMemoryMode')
    $null = $doc.DocumentElement.AppendChild($node)
}
$node.InnerText = if ($Mode -eq 'on') { 'true' } else { 'false' }
$doc.Save($config)
Write-Host "LowMemoryMode = $($node.InnerText)" -ForegroundColor Cyan

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$p = Start-Process -FilePath $exe -ArgumentList '/autorun', '/test-no-focusmonitor' -PassThru
Start-Sleep -Milliseconds $StartupWaitMs

function Sample([string]$label) {
    $proc = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    if (-not $proc) {
        Write-Host "  $label : the process is gone" -ForegroundColor Red
        return
    }
    $line = "  {0,-26} private {1,6:N1} MB   working set {2,6:N1} MB" -f `
        $label, ($proc.PrivateMemorySize64 / 1MB), ($proc.WorkingSet64 / 1MB)
    Write-Host $line
}

$path = Join-Path $smoke $File

Sample 'idle before preview'

Start-Process -FilePath $exe -ArgumentList $path | Out-Null
Start-Sleep -Seconds 4
Sample 'preview open'

Start-Process -FilePath $exe -ArgumentList $path | Out-Null   # closes it
Start-Sleep -Seconds 3
Sample 'just closed (warm)'

Write-Host "  waiting $IdleSeconds s for the idle release ..." -ForegroundColor DarkGray
Start-Sleep -Seconds $IdleSeconds
Sample "after ${IdleSeconds}s idle"

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
