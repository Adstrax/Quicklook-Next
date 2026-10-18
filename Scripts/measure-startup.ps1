# measure-startup.ps1
# Runs the hidden /test-startup hook several times and reports the time to the
# tray being ready and to the plugin list being loaded/initialized. It starts no
# previews, so it measures cold start rather than preview latency.

param(
    [int]$Runs = 3,
    [string]$Exe = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
}

if (-not (Test-Path -LiteralPath $Exe)) {
    throw "$Exe not found - build Release first"
}

if (Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue) {
    throw 'QuickLook-Next is already running. Close the tray app before measuring cold start.'
}

$Runs = [Math]::Max(1, $Runs)
$smoke = Join-Path $root 'ql-startup-smoke'
$env:QL_SMOKE_DIR = $smoke
New-Item -ItemType Directory -Path $smoke -Force | Out-Null

$startup = Join-Path $smoke 'startup.txt'
$pluginLoad = Join-Path $smoke 'plugin-load.txt'
$pluginInit = Join-Path $smoke 'plugin-init.txt'

function Get-PhaseMs([string[]]$Lines, [string]$Phase) {
    $match = $Lines |
        Where-Object { $_ -match "\|$([Regex]::Escape($Phase))$" } |
        Select-Object -First 1

    if (-not $match) {
        return $null
    }

    return [long]$match.Split('|')[0]
}

function Get-PluginWork([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return @{ Top = 'none'; Total = 0 }
    }

    $entries = foreach ($line in Get-Content -LiteralPath $Path) {
        $parts = $line.Split('|')
        if ($parts.Count -lt 2) {
            continue
        }

        try {
            [pscustomobject]@{
                Plugin = $parts[1]
                ElapsedMs = [long]$parts[0]
            }
        }
        catch {
            continue
        }
    }

    $entries = @($entries)
    if ($entries.Count -eq 0) {
        return @{ Top = 'none'; Total = 0 }
    }

    $groups = @($entries |
        Group-Object Plugin |
        ForEach-Object {
            [pscustomobject]@{
                Plugin = $_.Name
                ElapsedMs = ($_.Group | Measure-Object ElapsedMs -Sum).Sum
            }
        })

    $top = $groups | Sort-Object ElapsedMs -Descending | Select-Object -First 1
    return @{
        Top = "$($top.Plugin)=$($top.ElapsedMs)ms"
        Total = ($groups | Measure-Object ElapsedMs -Sum).Sum
    }
}

$results = for ($run = 1; $run -le $Runs; $run++) {
    foreach ($path in @($startup, $pluginLoad, $pluginInit)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    $process = Start-Process -FilePath $Exe `
        -ArgumentList @('/autorun', '/test-startup', '/test-no-focusmonitor') `
        -PassThru -WindowStyle Hidden

    try {
        $deadline = (Get-Date).AddSeconds(30)
        $uiReadyAt = $null
        while ((Get-Date) -lt $deadline) {
            if (Test-Path -LiteralPath $startup) {
                try {
                    $lines = @(Get-Content -LiteralPath $startup)
                }
                catch {
                    $lines = @()
                }

                $pluginsReady = $lines | Where-Object { $_ -match '\|plugins-inited$' }
                $uiReady = $lines | Where-Object { $_ -match '\|onstartup-end$' }
                if ($pluginsReady -and $uiReady) {
                    # v6.1.1: the preview window is built on the first Background
                    # turn after startup, so wait for it (bounded) to keep its cost
                    # in the report instead of losing it to the kill below.
                    if ($null -eq $uiReadyAt) {
                        $uiReadyAt = Get-Date
                    }

                    $windowReady = $lines | Where-Object { $_ -match '\|after-deferred-viewer-window$' }
                    if ($windowReady -or ((Get-Date) - $uiReadyAt).TotalMilliseconds -ge 1500) {
                        break
                    }
                }
            }

            Start-Sleep -Milliseconds 100
        }
    }
    finally {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path -LiteralPath $startup)) {
        throw "Run $run produced no startup diagnostics"
    }

    $lines = @(Get-Content -LiteralPath $startup)
    $load = Get-PluginWork $pluginLoad
    $init = Get-PluginWork $pluginInit

    [pscustomobject]@{
        Run = $run
        UIReadyMs = Get-PhaseMs $lines 'onstartup-end'
        PluginsLoadedMs = Get-PhaseMs $lines 'plugins-assemblies-loaded'
        PluginsReadyMs = Get-PhaseMs $lines 'plugins-inited'
        WindowReadyMs = Get-PhaseMs $lines 'after-deferred-viewer-window'
        TopLoad = $load.Top
        LoadWorkMs = $load.Total
        TopInit = $init.Top
        InitWorkMs = $init.Total
    }
}

$results | Format-Table -AutoSize

if ($Runs -gt 1) {
    foreach ($column in @('UIReadyMs', 'PluginsLoadedMs', 'PluginsReadyMs', 'WindowReadyMs')) {
        $values = @($results | ForEach-Object { $_.$column } | Where-Object { $null -ne $_ })
        if ($values.Count -eq 0) {
            continue
        }

        $sorted = @($values | Sort-Object)
        $median = $sorted[[int][Math]::Floor($sorted.Count / 2)]
        Write-Host "$column median: $median ms"
    }
}

# The per-phase deltas of the last run: which startup step owns the remaining time.
if (Test-Path -LiteralPath $startup) {
    $last = @(Get-Content -LiteralPath $startup |
        Where-Object { $_ -match '^\d+\|' } |
        ForEach-Object {
            $at, $name = $_.Split('|', 2)
            [pscustomobject]@{ At = [long]$at; Phase = $name }
        })

    $rows = for ($i = 1; $i -lt $last.Count; $i++) {
        [pscustomobject]@{
            Phase = $last[$i].Phase
            AtMs = $last[$i].At
            DeltaMs = $last[$i].At - $last[$i - 1].At
        }
    }

    Write-Host ''
    Write-Host 'Last run, phase by phase:'
    $rows | Format-Table -AutoSize
}
