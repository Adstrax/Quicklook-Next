# share-plugin-dependency.ps1
# Keeps one exact copy of a managed dependency in Build\<cfg>\ and removes the
# per-plugin copies. Plugins are loaded with Assembly.LoadFrom, and their
# dependencies resolve from the application directory, the same way
# QuickLook.Shared and the WebView2 assemblies already do.
#
# A version mismatch is an error: silently replacing the application copy could
# bind another plugin to the wrong assembly. Different versions must stay local
# to their plugin until they are aligned explicitly.

param(
    [Parameter(Mandatory = $true)][string]$ProjectDir,
    [Parameter(Mandatory = $true)][string]$Configuration,
    [string]$Names = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path (Split-Path $ProjectDir -Parent) -Parent
$pluginDir = Split-Path $ProjectDir -Leaf
$releasePath = Join-Path $repoRoot "Build\$Configuration"
$outputPath = Join-Path $releasePath "QuickLook.Plugin\$pluginDir"

function Get-FileSha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        return [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

$namesToShare = @($Names.Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ })

if ($namesToShare.Count -eq 0) {
    exit 0
}

# Plugin builds may run in parallel. Serialize the copy/compare/delete sequence
# so two projects sharing a dependency cannot race over the application copy.
$mutex = [System.Threading.Mutex]::new($false, 'QuickLookNext.PluginDependencyShare')
$hasLock = $false
try {
    try {
        $hasLock = $mutex.WaitOne([TimeSpan]::FromSeconds(30))
    }
    catch [System.Threading.AbandonedMutexException] {
        # A cancelled build left the mutex behind; this process now owns it.
        $hasLock = $true
    }

    if (-not $hasLock) {
        throw 'Timed out waiting to consolidate shared plugin dependencies.'
    }

    foreach ($name in $namesToShare) {
        $source = Join-Path $outputPath $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            continue
        }

        $destination = Join-Path $releasePath $name
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            $sourceHash = Get-FileSha256 $source
            $destinationHash = Get-FileSha256 $destination
            if ($sourceHash -ne $destinationHash) {
                throw "Shared plugin dependency '$name' differs between the plugin and the application root. Align the package versions before sharing it."
            }
        }
        else {
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }

        Remove-Item -LiteralPath $source -Force
    }
}
finally {
    if ($hasLock) {
        $mutex.ReleaseMutex()
    }

    $mutex.Dispose()
}
