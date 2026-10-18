# Diagnostic: preview a video and sample the preview window's brightness over
# time. Capturing the whole screen and then restricting the statistics to the
# window's current rectangle keeps the reading correct while the window is
# moved or resized.
#
# Usage:
#   pwsh -NoProfile -File .\Scripts\measure-video-flicker.ps1 -File flickerB.mp4 -WarmFile flickerA.mp4

param(
    [string]$File = 'test.mp4',
    [string]$WarmFile = '',
    [int]$DurationMs = 3000,
    [int]$IntervalMs = 20,
    [int]$StartupWaitMs = 1500,
    [string]$FrameDir = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WindowProbe
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder t, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    public static string Find(uint pid, string titlePart)
    {
        string result = "";
        EnumWindows((h, l) =>
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h))
                return true;

            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            if (sb.Length == 0 || sb.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0)
                return true;

            RECT r;
            GetWindowRect(h, out r);
            if (r.Right - r.Left < 40 || r.Bottom - r.Top < 40)
                return true;

            result = h.ToInt64() + "|" + r.Left + "|" + r.Top + "|" + (r.Right - r.Left) + "|" + (r.Bottom - r.Top);
            return false;
        }, IntPtr.Zero);
        return result;
    }

    public static string Rect(long hwnd)
    {
        RECT r;
        if (!GetWindowRect(new IntPtr(hwnd), out r))
            return "";
        return r.Left + "|" + r.Top + "|" + (r.Right - r.Left) + "|" + (r.Bottom - r.Top);
    }

    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
'@ | Out-Null

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
$smoke = Join-Path $root 'ql-smoke'
$path = Join-Path $smoke $File
if (-not (Test-Path -LiteralPath $path)) { throw "$path not found" }

$env:QL_SMOKE_DIR = $smoke
$env:QL_VIDEO_TIMING = '1'
$env:QL_INSTANCE_SUFFIX = 'flicker-probe'

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -eq $exe } catch { $false } } |
    Stop-Process -Force
Start-Sleep -Milliseconds 800

$timingFile = Join-Path $smoke 'video-timing.txt'
Remove-Item -LiteralPath $timingFile -Force -ErrorAction SilentlyContinue

$app = Start-Process -FilePath $exe -ArgumentList '/autorun', '/test-timing' -PassThru
Start-Sleep -Milliseconds $StartupWaitMs

$hwnd = [int64]0
if ($WarmFile) {
    $warmPath = Join-Path $smoke $WarmFile
    if (Test-Path -LiteralPath $warmPath) {
        Start-Process -FilePath $exe -ArgumentList $warmPath | Out-Null
        Start-Sleep -Milliseconds 2500
        $warm = [WindowProbe]::Find([uint32]$app.Id, [IO.Path]::GetFileNameWithoutExtension($WarmFile))
        if ($warm) {
            $hwnd = [int64]$warm.Split('|')[0]
            [WindowProbe]::SetWindowPos([IntPtr]$hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0013) | Out-Null
        }
    }
}

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
$screenRect = New-Object System.Drawing.Rectangle 0, 0, $screen.Width, $screen.Height
$bytes = $null

$samples = New-Object System.Collections.Generic.List[string]
$launchAt = [DateTime]::UtcNow
$launch = [Diagnostics.Stopwatch]::StartNew()
Start-Process -FilePath $exe -ArgumentList $path | Out-Null
Write-Host ("launch at {0:o}" -f $launchAt)

try {
    while ($launch.ElapsedMilliseconds -lt $DurationMs) {
        $sampleAt = $launch.ElapsedMilliseconds

        if ($hwnd -eq 0) {
            $found = [WindowProbe]::Find([uint32]$app.Id, [IO.Path]::GetFileNameWithoutExtension($File))
            if ($found) {
                $hwnd = [int64]$found.Split('|')[0]
                [WindowProbe]::SetWindowPos([IntPtr]$hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0013) | Out-Null
            }
        }

        $bounds = if ($hwnd -ne 0) { [WindowProbe]::Rect($hwnd) } else { '' }
        $graphics.CopyFromScreen($screen.Left, $screen.Top, 0, 0, $screen.Size)

        $data = $bmp.LockBits($screenRect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $needed = $data.Stride * $screen.Height
            if ($null -eq $bytes -or $bytes.Length -lt $needed) { $bytes = New-Object byte[] $needed }
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $needed)
        }
        finally {
            $bmp.UnlockBits($data)
        }

        $x0 = 0; $y0 = 0; $x1 = $screen.Width; $y1 = $screen.Height
        if ($bounds) {
            $b = $bounds.Split('|')
            $x0 = [math]::Max(0, [int]$b[0] - $screen.Left)
            $y0 = [math]::Max(0, [int]$b[1] - $screen.Top)
            $x1 = [math]::Min($screen.Width, [int]$b[0] - $screen.Left + [int]$b[2])
            $y1 = [math]::Min($screen.Height, [int]$b[1] - $screen.Top + [int]$b[3])
        }

        $sum = 0.0
        $dark = 0
        $count = 0
        for ($y = $y0; $y -lt $y1; $y += 8) {
            $row = $y * $data.Stride
            for ($x = $x0; $x -lt $x1; $x += 8) {
                $i = $row + $x * 4
                $lum = ($bytes[$i + 2] * 299 + $bytes[$i + 1] * 587 + $bytes[$i] * 114) / 1000
                $sum += $lum
                if ($lum -lt 24) { $dark++ }
                $count++
            }
        }

        $mean = if ($count -gt 0) { [math]::Round($sum / $count, 1) } else { -1 }
        $darkPct = if ($count -gt 0) { [math]::Round(100.0 * $dark / $count, 1) } else { -1 }
        $size = if ($bounds) { $bounds.Split('|')[2] + 'x' + $bounds.Split('|')[3] } else { 'none' }
        $samples.Add(("{0}|{1}|{2}|{3}" -f $sampleAt, $mean, $darkPct, $size))

        if ($FrameDir -and ($sampleAt % 60) -lt $IntervalMs) {
            if (-not (Test-Path -LiteralPath $FrameDir)) {
                New-Item -ItemType Directory -Force -Path $FrameDir | Out-Null
            }
            $bmp.Save((Join-Path $FrameDir ("{0:D5}.png" -f $sampleAt)),
                [System.Drawing.Imaging.ImageFormat]::Png)
        }

        $wait = $IntervalMs - ($launch.ElapsedMilliseconds - $sampleAt)
        if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
    }
}
finally {
    $graphics.Dispose()
    $bmp.Dispose()
}

foreach ($sample in $samples) {
    $v = $sample.Split('|')
    $flag = if ([double]$v[2] -ge 97) { '  <== BLACK' } else { '' }
    Write-Host ("+{0,5} ms  mean={1,6}  dark={2,6}%  win={3}{4}" -f $v[0], $v[1], $v[2], $v[3], $flag)
}

Start-Sleep -Milliseconds 2500
Write-Host ''
Write-Host 'video-timing:'
if (Test-Path -LiteralPath $timingFile) {
    Get-Content -LiteralPath $timingFile | ForEach-Object { Write-Host $_ }
}

Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -eq $exe } catch { $false } } |
    Stop-Process -Force
