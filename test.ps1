# QuickLookNext smoke test: must be run and must pass before every commit.
# Covers: full build -> start -> plugin load without failures -> PNG/text/SQLite previews -> window
# assertions -> no new errors in the log.
#
# Usage: .\test.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
# v3.35.0: the app keeps its data (settings, log, user plugins) next to the
# program; %APPDATA% is only used when the program folder is read-only.
# Resolved on every call because the folder is wiped in step 2.
$logLocations = @(
    (Join-Path $root 'Build\Release\UserData\QuickLookNext.Exception.log'),
    (Join-Path $env:APPDATA 'pooi.moe\QuickLookNext\QuickLookNext.Exception.log')
)
# v1.2.36: keep the smoke-test files inside the repository
# (E:\Codex\QK-Lite\<version>\ql-smoke) instead of the C: temp folder; the
# app's diagnostics (timing/startup/tray-menu) follow via QL_SMOKE_DIR.
$smoke = Join-Path $root 'ql-smoke'
$env:QL_SMOKE_DIR = $smoke
$failed = $false

function Assert([bool]$cond, [string]$msg) {
    if ($cond) { Write-Host "PASS: $msg" -ForegroundColor Green }
    else { Write-Host "FAIL: $msg" -ForegroundColor Red; $script:failed = $true }
}

function Get-LogLength {
    foreach ($candidate in $logLocations) {
        if (Test-Path $candidate) { return (Get-Item $candidate).Length }
    }
    0
}

function Get-QuickLookNextWindows([int]$targetPid) {
    if (-not ('WinEnum3' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public class WinEnum3 {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
  [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  public static List<string> Titles(uint targetPid) {
    var list = new List<string>();
    EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid); if (pid == targetPid) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); if (sb.Length > 0) list.Add(sb.ToString()); } return true; }, IntPtr.Zero);
    return list;
  }
}
"@
    }
    [WinEnum3]::Titles($targetPid)
}

function Get-QuickLookNextWindowRect([int]$targetPid, [string]$titleMatch) {
    if (-not ('WinEnumRect' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WinEnumRect {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
  [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  static string result = "";
  public static string Rect(uint targetPid, string titleMatch) {
    result = "";
    EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid); if (pid == targetPid && IsWindowVisible(h)) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); if (sb.Length > 0 && sb.ToString().Contains(titleMatch)) { RECT r; GetWindowRect(h, out r); result = r.Left + "|" + r.Top + "|" + (r.Right - r.Left) + "|" + (r.Bottom - r.Top); return false; } } return true; }, IntPtr.Zero);
    return result;
  }
}
"@
    }
    [WinEnumRect]::Rect($targetPid, $titleMatch)
}

# ---------- 1. Clean up old instances ----------
Write-Host "== 1/9 Clean up old instances ==" -ForegroundColor Cyan
# v3.31.0: the process name is "QuickLook-Next" (the assembly name has a hyphen). The old spelling never
# matched, so an instance left in the tray made the instance started below forward as a "second instance"
# and exit immediately, after which every assertion was checking an already-dead process - which shows up
# as a whole batch of inexplicable failures.
Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
if ($null -ne (Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue)) {
    Write-Host '=== Could not clean up the old instance; aborting (a new instance would exit as a second instance) ===' -ForegroundColor Red
    exit 1
}

# ---------- 2. Build ----------
Write-Host "== 2/9 Full build ==" -ForegroundColor Cyan
Get-ChildItem (Join-Path $root 'Build\Release') -Force -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force
# v1.2.34: build the whole solution in one parallel invocation instead of
# compiling the 16 projects one by one - roughly halves the test time.
& dotnet build (Join-Path $root 'QuickLookNext.slnx') -c Release -v minimal --nologo *> $null
Assert ($LASTEXITCODE -eq 0) 'build QuickLookNext.slnx'
if ($LASTEXITCODE -ne 0) { exit 1 }

# ---------- 3. Prepare the test files ----------
Write-Host "== 3/9 Prepare the test files ==" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $smoke | Out-Null
# v3.42.0: the preview warm-up proves itself by writing this file, so a leftover
# from an earlier run must not be able to satisfy the assertion below.
Remove-Item -LiteralPath (Join-Path $smoke 'warmup.txt') -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 256, 256
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::SteelBlue)
$font = New-Object System.Drawing.Font 'Arial', 24
$g.DrawString('QL Test', $font, [System.Drawing.Brushes]::White, 40, 100)
$g.Dispose()
$bmp.Save((Join-Path $smoke 'test.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Set-Content -Path (Join-Path $smoke 'test.txt') -Value "QuickLookNext smoke test`r`nLine 2" -Encoding UTF8
Set-Content -Path (Join-Path $smoke 'test.md') -Value "# Markdown`n`nThis is **bold** text." -Encoding UTF8
Set-Content -Path (Join-Path $smoke 'test.json') -Value '{"name":"ql-smoke","version":"1.0.0","scripts":{}}' -Encoding UTF8
Compress-Archive -Path (Join-Path $smoke 'test.txt') -DestinationPath (Join-Path $smoke 'test.zip') -Force
Copy-Item -LiteralPath "$env:WINDIR\Fonts\arial.ttf" -Destination (Join-Path $smoke 'test.ttf') -Force
if (-not (Test-Path (Join-Path $smoke 'test.mp4'))) {
    curl.exe -sL -o (Join-Path $smoke 'test.mp4') "https://interactive-examples.mdn.mozilla.net/media/cc0-videos/flower.mp4"
}
if (-not (Test-Path (Join-Path $smoke 'test.pdf'))) {
    curl.exe -sL -o (Join-Path $smoke 'test.pdf') "https://raw.githubusercontent.com/mozilla/pdf.js/master/web/compressed.tracemonkey-pldi-09.pdf"
}
# v3.7.0: rare-format coverage (on-demand plugin loading) + mermaid/math
# markdown paths. These used to be untested, which let a lazy-loading
# regression slip through.
[System.IO.File]::WriteAllBytes(
    (Join-Path $smoke 'test.bin'),
    [byte[]](0..9))
Copy-Item -LiteralPath (Join-Path $root 'Build\Release\QuickLook-Next.exe') `
    -Destination (Join-Path $smoke 'test-pe.exe') -Force
Set-Content -Path (Join-Path $smoke 'test-mermaid.md') `
    -Value "## Diagram`n`n``````mermaid`ngraph TD;`n  A-->B;`n``````" -Encoding UTF8
Set-Content -Path (Join-Path $smoke 'test-math.md') `
    -Value "## Formula`n`nMass-energy equivalence $E=mc^2$ or $$\int_0^1 x dx$$" -Encoding UTF8
# v3.12.0: a minimal but valid xlsx (OOXML zip) for the self-rendered
# spreadsheet preview.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$xlsxPath = Join-Path $smoke 'test.xlsx'
$xlsxFs = [System.IO.File]::Open($xlsxPath, [System.IO.FileMode]::Create)
$xlsxZip = New-Object System.IO.Compression.ZipArchive($xlsxFs,
    [System.IO.Compression.ZipArchiveMode]::Create)
function Add-XlsxEntry($zip, $name, $content) {
    $entry = $zip.CreateEntry($name)
    $writer = New-Object System.IO.StreamWriter($entry.Open(),
        (New-Object System.Text.UTF8Encoding($false)))
    $writer.Write($content)
    $writer.Dispose()
}
Add-XlsxEntry $xlsxZip '[Content_Types].xml' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>'
Add-XlsxEntry $xlsxZip '_rels/.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'
Add-XlsxEntry $xlsxZip 'xl/workbook.xml' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>'
Add-XlsxEntry $xlsxZip 'xl/_rels/workbook.xml.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>'
$xlsxRows = '<row r="1"><c r="A1" t="inlineStr"><is><t>Name</t></is></c><c r="B1" t="inlineStr"><is><t>Team</t></is></c><c r="C1" t="inlineStr"><is><t>Salary</t></is></c><c r="D1" t="inlineStr"><is><t>Hire date</t></is></c></row>'
$xlsxData = @(@('Alice','Engineering','18000','2024-03-15'),@('Bob','Design','16000','2023-11-02'),@('Cara','Support','15000','2025-01-20'))
for ($i = 0; $i -lt $xlsxData.Count; $i++) {
    $r = $i + 2
    $cells = @()
    for ($c = 0; $c -lt 4; $c++) {
        $col = [char](65 + $c)
        $val = $xlsxData[$i][$c]
        if ($c -eq 2) { $cells += "<c r=`"$col$r`"><v>$val</v></c>" }
        else { $cells += "<c r=`"$col$r`" t=`"inlineStr`"><is><t>$val</t></is></c>" }
    }
    $xlsxRows += "<row r=`"$r`">$($cells -join '')</row>"
}
Add-XlsxEntry $xlsxZip 'xl/worksheets/sheet1.xml' "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><worksheet xmlns=`"http://schemas.openxmlformats.org/spreadsheetml/2006/main`"><sheetData>$xlsxRows</sheetData></worksheet>"
$xlsxZip.Dispose()
$xlsxFs.Dispose()
# v3.14.0: a minimal but valid docx (heading / formatting / list / table) for
# the self-rendered Word preview.
$docxPath = Join-Path $smoke 'test.docx'
$docxFs = [System.IO.File]::Open($docxPath, [System.IO.FileMode]::Create)
$docxZip = New-Object System.IO.Compression.ZipArchive($docxFs,
    [System.IO.Compression.ZipArchiveMode]::Create)
$wNs = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'
Add-XlsxEntry $docxZip '[Content_Types].xml' "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><Types xmlns=`"http://schemas.openxmlformats.org/package/2006/content-types`"><Default Extension=`"rels`" ContentType=`"application/vnd.openxmlformats-package.relationships+xml`"/><Default Extension=`"xml`" ContentType=`"application/xml`"/><Override PartName=`"/word/document.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml`"/><Override PartName=`"/word/styles.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml`"/><Override PartName=`"/word/numbering.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml`"/></Types>"
Add-XlsxEntry $docxZip '_rels/.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
Add-XlsxEntry $docxZip 'word/_rels/document.xml.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/></Relationships>'
Add-XlsxEntry $docxZip 'word/styles.xml' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/></w:style></w:styles>'
Add-XlsxEntry $docxZip 'word/numbering.xml' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/><w:lvlText w:val="•"/></w:lvl></w:abstractNum><w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num></w:numbering>'
$docxBody = "<w:document xmlns:w=`"$wNs`"><w:body>"
$docxBody += '<w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>QuickLook-Next test document</w:t></w:r></w:p>'
$docxBody += '<w:p><w:r><w:t>Plain text, </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>bold</w:t></w:r><w:r><w:rPr><w:i/></w:rPr><w:t>italic</w:t></w:r><w:r><w:rPr><w:color w:val="C00000"/><w:sz w:val="28"/></w:rPr><w:t>big red</w:t></w:r></w:p>'
$docxBody += '<w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Item one</w:t></w:r></w:p>'
$docxBody += '<w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Item two</w:t></w:r></w:p>'
$docxBody += '<w:tbl><w:tr><w:tc><w:tcPr><w:gridSpan w:val="2"/></w:tcPr><w:p><w:r><w:t>Merged header</w:t></w:r></w:p></w:tc></w:tr><w:tr><w:tc><w:p><w:r><w:t>Cell A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Cell B</w:t></w:r></w:p></w:tc></w:tr></w:tbl>'
$docxBody += '</w:body></w:document>'
Add-XlsxEntry $docxZip 'word/document.xml' $docxBody
$docxZip.Dispose()
$docxFs.Dispose()
# v3.15.0: a minimal but valid pptx (two positioned text-box slides) for the
# self-rendered PowerPoint preview.
$pptxPath = Join-Path $smoke 'test.pptx'
$pptxFs = [System.IO.File]::Open($pptxPath, [System.IO.FileMode]::Create)
$pptxZip = New-Object System.IO.Compression.ZipArchive($pptxFs,
    [System.IO.Compression.ZipArchiveMode]::Create)
$pNs = 'http://schemas.openxmlformats.org/presentationml/2006/main'
$aNs = 'http://schemas.openxmlformats.org/drawingml/2006/main'
$rNs = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
Add-XlsxEntry $pptxZip '[Content_Types].xml' "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><Types xmlns=`"http://schemas.openxmlformats.org/package/2006/content-types`"><Default Extension=`"rels`" ContentType=`"application/vnd.openxmlformats-package.relationships+xml`"/><Default Extension=`"xml`" ContentType=`"application/xml`"/><Override PartName=`"/ppt/presentation.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml`"/><Override PartName=`"/ppt/slides/slide1.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.presentationml.slide+xml`"/><Override PartName=`"/ppt/slides/slide2.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.presentationml.slide+xml`"/></Types>"
Add-XlsxEntry $pptxZip '_rels/.rels' "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><Relationships xmlns=`"http://schemas.openxmlformats.org/package/2006/relationships`"><Relationship Id=`"rId1`" Type=`"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument`" Target=`"ppt/presentation.xml`"/></Relationships>"
Add-XlsxEntry $pptxZip 'ppt/_rels/presentation.xml.rels' "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><Relationships xmlns=`"http://schemas.openxmlformats.org/package/2006/relationships`"><Relationship Id=`"rId1`" Type=`"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide`" Target=`"slides/slide1.xml`"/><Relationship Id=`"rId2`" Type=`"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide`" Target=`"slides/slide2.xml`"/></Relationships>"
Add-XlsxEntry $pptxZip 'ppt/presentation.xml' "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><p:presentation xmlns:p=`"$pNs`" xmlns:r=`"$rNs`"><p:sldSz cx=`"12192000`" cy=`"6858000`"/><p:sldIdLst><p:sldId id=`"256`" r:id=`"rId1`"/><p:sldId id=`"257`" r:id=`"rId2`"/></p:sldIdLst></p:presentation>"
$slideXml1 = "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><p:sld xmlns:p=`"$pNs`" xmlns:a=`"$aNs`" xmlns:r=`"$rNs`"><p:cSld><p:spTree>"
$slideXml1 += '<p:sp><p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="457200" y="2286000"/><a:ext cx="11277600" cy="1524000"/></a:xfrm></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:pPr algn="ctr"/><a:r><a:rPr lang="en-US" sz="5400" b="1"/><a:t>QuickLook-Next presentation</a:t></a:r></a:p></p:txBody></p:sp>'
$slideXml1 += '<p:sp><p:nvSpPr><p:cNvPr id="3" name="Subtitle"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="2286000" y="4114800"/><a:ext cx="7620000" cy="914400"/></a:xfrm></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:pPr algn="ctr"/><a:r><a:rPr lang="en-US" sz="2400"/><a:t>custom renderer test</a:t></a:r></a:p></p:txBody></p:sp>'
$slideXml1 += '</p:spTree></p:cSld></p:sld>'
Add-XlsxEntry $pptxZip 'ppt/slides/slide1.xml' $slideXml1
$slideXml2 = "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><p:sld xmlns:p=`"$pNs`" xmlns:a=`"$aNs`" xmlns:r=`"$rNs`"><p:cSld><p:spTree>"
$slideXml2 += '<p:sp><p:nvSpPr><p:cNvPr id="2" name="Body"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="914400" y="914400"/><a:ext cx="10363200" cy="5029200"/></a:xfrm></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang="en-US" sz="2800" b="1"/><a:t>Bullet list</a:t></a:r></a:p><a:p><a:r><a:rPr lang="en-US" sz="2000"/><a:t>First point: heading, text and positioning are all supported</a:t></a:r></a:p></p:txBody></p:sp>'
$slideXml2 += '</p:spTree></p:cSld></p:sld>'
Add-XlsxEntry $pptxZip 'ppt/slides/slide2.xml' $slideXml2
Add-XlsxEntry $pptxZip 'ppt/slides/_rels/slide1.xml.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>'
Add-XlsxEntry $pptxZip 'ppt/slides/_rels/slide2.xml.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>'
$pptxZip.Dispose()
$pptxFs.Dispose()

# v5.0.9: a truncated video for the "unplayable media must not crash the app" guard.
if (Test-Path -LiteralPath (Join-Path $smoke 'test.mp4')) {
    $videoBytes = [IO.File]::ReadAllBytes((Join-Path $smoke 'test.mp4'))
    [IO.File]::WriteAllBytes(
        (Join-Path $smoke 'test-corrupt.mp4'),
        $videoBytes[0..([int]($videoBytes.Length * 0.4))])
}

# ---------- 4. Start + plugin loading ----------
Write-Host "== 4/9 Start and verify plugin loading ==" -ForegroundColor Cyan
$before = Get-LogLength
$p = Start-Process -FilePath $exe -ArgumentList '/autorun /test-tray-menu /test-warmup' -PassThru
$trayMenuSeen = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    if ($null -eq $alive) { break }
    $titles = Get-QuickLookNextWindows $p.Id
    if (($titles -join ' ') -match 'QuickLook-Next Tray Menu') { $trayMenuSeen = $true; break }
}
# Wait for the menu to auto-close and the plugins to finish loading.
Start-Sleep -Seconds 15
$alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
Assert ($null -ne $alive) 'process is alive after startup'
Assert $trayMenuSeen 'tray menu window appeared and closed itself'
$diagFile = Join-Path $smoke 'tray-menu-dwm.txt'
$dwmDiag = if (Test-Path $diagFile) { Get-Content $diagFile -Raw } else { '' }
if ($env:QL_SMOKE_CI -eq '1') {
    # CI runners may not have DWM compositing; skip the Acrylic assertion.
    Write-Host 'SKIP: tray menu Acrylic (CI environment)' -ForegroundColor Yellow
}
else {
    Assert ($dwmDiag -match 'accent-applied=True') 'tray menu material applied (WCA call succeeded)'
# v5.0.2: menu-style surfaces went back to WCA acrylic. This assertion only requires that *a* material was
# reported, so that we notice if it ever degrades to no material at all (host-backdrop is what 5.0.0/5.0.1
# used, and the regex keeps accepting it for compatibility).
Assert ($dwmDiag -match 'material=(host-backdrop|acrylic)') 'tray menu reported the material it used'
}
Assert ($dwmDiag -match 'more-menu-opened=true') 'the More menu reuses the same Acrylic menu path'
Assert ((Get-LogLength) -eq $before) 'plugin loading had no failures (no new log entries)'

# ---------- 5. Preview tests ----------
Write-Host "== 5/9 Preview tests ==" -ForegroundColor Cyan
$previews = @(
    @{ File = 'test.png'; Title = 'test.png' },
    @{ File = 'test.txt'; Title = 'test.txt' },
    @{ File = 'test.md'; Title = 'test.md' },
    @{ File = 'test.json'; Title = 'test.json' },
    @{ File = 'test.zip'; Title = 'test.zip' },
    @{ File = 'test.ttf'; Title = 'test.ttf' }
)
if (Test-Path (Join-Path $smoke 'test.mp4')) {
    $previews += @{ File = 'test.mp4'; Title = 'test.mp4' }
}
# v5.0.9: a video the media player cannot open must show an error panel, not take the app
# down. Found by the 22-sample video matrix (docs/research-video-matrix.md): MediaFailed is
# raised on WPFMediaKit's own worker thread, and touching the visual tree from there used to
# kill the process. The entry expects a log line - the failure has to be reported, not
# silently swallowed - but the process-alive assertion above is the actual guard.
if (Test-Path (Join-Path $smoke 'test-corrupt.mp4')) {
    $previews += @{ File = 'test-corrupt.mp4'; Title = 'test-corrupt.mp4'; ExpectLog = $true }
}
if (Test-Path (Join-Path $smoke 'test.pdf')) {
    $previews += @{ File = 'test.pdf'; Title = 'test.pdf' }
}
# v3.7.0: rare-format previews exercise the on-demand lazy plugin loading.
# PEViewer deliberately shows no window title (full-bleed PE info panel), so
# only the process-alive and no-error assertions apply to it.
$previews += @{ File = 'test-pe.exe'; Title = 'test-pe.exe'; CheckTitle = $false }
$previews += @{ File = 'test.bin'; Title = 'test.bin' }
$previews += @{ File = 'test-mermaid.md'; Title = 'test-mermaid.md' }
$previews += @{ File = 'test-math.md'; Title = 'test-math.md' }
# v3.12.0: self-rendered spreadsheet preview.
$previews += @{ File = 'test.xlsx'; Title = 'test.xlsx' }
# v3.14.0: self-rendered Word document preview.
$previews += @{ File = 'test.docx'; Title = 'test.docx' }
# v3.15.0: self-rendered PowerPoint preview.
$previews += @{ File = 'test.pptx'; Title = 'test.pptx' }
# v3.8.0: folder preview (InfoPanel) coverage - InfoPanel shows no title.
$previews += @{ Folder = $smoke; Title = 'ql-smoke'; CheckTitle = $false }
foreach ($pv in $previews) {
    $before = Get-LogLength
    $target = if ($pv.Folder) { $pv.Folder } else { Join-Path $smoke $pv.File }
    $label = if ($pv.Folder) { $pv.Title } else { $pv.File }
    & $exe $target
    Start-Sleep -Seconds 12
    $alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    Assert ($null -ne $alive) "process is alive after previewing $label"
    if ($pv.CheckTitle -ne $false) {
        $titles = Get-QuickLookNextWindows $p.Id
        Assert (($titles -join ' ') -match [regex]::Escape($pv.Title)) "preview window appeared: $($pv.Title)"
    }
    if ($pv.ExpectLog) {
        Assert ((Get-LogLength) -gt $before) "previewing $label logged an error (expected: corrupt file)"
    }
    else {
        Assert ((Get-LogLength) -eq $before) "previewing $label produced no errors (no new log entries)"
    }

    # v1.2.36: regression guard - the preview window must be centered on the
    # screen that contains it (the off-screen warm-up once broke this).
    if ($pv.File -eq 'test.png') {
        $rectStr = Get-QuickLookNextWindowRect $p.Id $pv.Title
        if ($rectStr) {
            $rp = $rectStr.Split('|')
            $winCenter = [System.Drawing.Point]::new(
                [int]$rp[0] + [int]$rp[2] / 2,
                [int]$rp[1] + [int]$rp[3] / 2)
            $screen = [System.Windows.Forms.Screen]::AllScreens |
                Where-Object { $_.WorkingArea.Contains($winCenter) } |
                Select-Object -First 1
            if ($null -eq $screen) { $screen = [System.Windows.Forms.Screen]::PrimaryScreen }
            $wa = $screen.WorkingArea
            $cx = $wa.Left + $wa.Width / 2
            $cy = $wa.Top + $wa.Height / 2
            Assert ([math]::Abs($winCenter.X - $cx) -lt $wa.Width * 0.15) "preview window is horizontally centered: $($pv.Title)"
            Assert ([math]::Abs($winCenter.Y - $cy) -lt $wa.Height * 0.15) "preview window is vertically centered: $($pv.Title)"
        }
        else {
            Assert $false "preview window rect is readable: $($pv.Title)"
        }
    }
}

# ---------- 6. Performance and memory baseline ----------
# v3.43.0: record "first preview right after startup" latency and idle memory in
# baseline.txt, with a deliberately loose ceiling. Every problem fixed recently
# (the WMI query stalling the first window render for ~2 s, the warm-up that did
# nothing, the controller pool that was not being used) was found by hand-made
# measurements - nothing guarded against them coming back. This is that guard.
Write-Host "== 6/9 Performance and memory baseline ==" -ForegroundColor Cyan

$measureOutput = & pwsh -NoProfile -File (Join-Path $root 'Scripts\measure-preview.ps1') `
    -Files test.png,test.txt,test.md -StartupWaitMs 1500 -Memory
$measureOutput | ForEach-Object { Write-Host $_ }

$baseline = New-Object System.Collections.Generic.List[string]
$firstPreviewMs = @{}
foreach ($line in $measureOutput) {
    if ($line -match '^(?<file>\S+)\s+(?<ms>\d+)\s+ms$') {
        $firstPreviewMs[$Matches['file']] = [int]$Matches['ms']
        $baseline.Add("$($Matches['file'])=$($Matches['ms'])ms")
    }
    elseif ($line -match '^memory\|(?<sample>.+)$') {
        $baseline.Add("memory=$($Matches['sample'])")
    }
}
$baseline | Set-Content -Path (Join-Path $smoke 'baseline.txt') -Encoding UTF8

# The ceiling is deliberately loose: normal is 100-350 ms, and the WMI regression measured 1.2-1.8 s.
foreach ($file in @('test.png', 'test.txt', 'test.md')) {
    if ($firstPreviewMs.ContainsKey($file)) {
        Assert ($firstPreviewMs[$file] -lt 1000) `
            "first preview of $file after startup is within 1000 ms (measured $($firstPreviewMs[$file]) ms)"
    }
    else {
        Assert $false "first preview of $file after startup has a measurement"
    }
}

# ---------- 7. Auto-update (a real file replacement) ----------
# v3.40.0: after the update package is downloaded, a script replaces the files once the app exits. That
# script used to break on xcopy /EXCLUDE:"<file>" (xcopy cannot read a quoted exclude file) - the backup
# was judged failed, the update stopped, the freshly downloaded package was thrown away and the app went
# back to the old version. Here the really generated script performs one full replacement on a copy, and we
# assert: files are new, UserData is kept, the temp folder is cleaned up.
Write-Host "== 7/9 Auto-update flow ==" -ForegroundColor Cyan

# The script waits for the process named QuickLook-Next.exe to exit, so clean up first.
Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force

$updRoot = Join-Path $root 'Build\UpdateSmoke'
Remove-Item -LiteralPath $updRoot -Recurse -Force -ErrorAction SilentlyContinue
$updApp = Join-Path $updRoot 'app'
$updSrc = Join-Path $updRoot 'new'
$updWork = Join-Path $env:TEMP 'QuickLookNext.Update'
New-Item -ItemType Directory -Force -Path $updApp, $updSrc,
    (Join-Path $updApp 'UserData'), $updWork | Out-Null

foreach ($name in @('QuickLook-Next.exe', 'QuickLook-Next.dll', 'QuickLook.Common.dll')) {
    Copy-Item -LiteralPath (Join-Path $root "Build\Release\$name") -Destination $updApp -Force
}
Set-Content -Path (Join-Path $updApp 'old-only.txt') -Value 'old' -Encoding UTF8
Set-Content -Path (Join-Path $updApp 'UserData\keep.txt') -Value 'keep' -Encoding UTF8

# Simulate an "already extracted package": only the entry files the pre-update integrity check needs.
Copy-Item -LiteralPath (Join-Path $root 'Build\Release\QuickLook-Next.exe') -Destination $updSrc -Force
Copy-Item -LiteralPath (Join-Path $root 'Build\Release\QuickLook.Common.dll') -Destination $updSrc -Force
Set-Content -Path (Join-Path $updSrc 'new-only.txt') -Value 'new' -Encoding UTF8

Set-Content -Path (Join-Path $smoke 'update-request.txt') -Encoding UTF8 -Value @(
    $updApp, $updSrc, $updWork, (Join-Path $updWork 'update.log'))

$updScript = Join-Path $env:TEMP 'QuickLookNext-update.cmd'
$updLog = Join-Path $env:TEMP 'QuickLookNext-update.log'
Remove-Item -LiteralPath $updScript, $updLog -Force -ErrorAction SilentlyContinue

Start-Process -FilePath $exe -ArgumentList '/test-update-script' -Wait -WindowStyle Hidden
Assert (Test-Path -LiteralPath $updScript) 'update script was generated'

& cmd.exe /c $updScript | Out-Null
for ($i = 0; $i -lt 60 -and -not (Test-Path -LiteralPath $updLog); $i++) { Start-Sleep -Milliseconds 500 }
$updText = if (Test-Path -LiteralPath $updLog) { Get-Content -LiteralPath $updLog -Raw } else { '' }

Assert ($updText -match 'update ok') 'update script completed the replacement (log says update ok)'
Assert (Test-Path (Join-Path $updApp 'new-only.txt')) 'new-version files are in place'
Assert (-not (Test-Path (Join-Path $updApp 'old-only.txt'))) 'old-version files were cleaned up'
Assert (Test-Path (Join-Path $updApp 'UserData\keep.txt')) 'user data (UserData) was left untouched'
Assert (-not (Test-Path -LiteralPath $updWork)) 'update temp folder was cleaned up (no more piling up packages)'

# The script tries to start the copy at the end; shut that down so it does not affect later steps.
Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$updRoot*" } | Stop-Process -Force

# v3.42.0: preview warm-up - after startup the app should prepare the common preview families in the
# background (the work that made the first preview slow) and write the result to warmup.txt.
$warmUpDiag = Join-Path $smoke 'warmup.txt'
Assert (Test-Path -LiteralPath $warmUpDiag) 'preview warm-up ran (warmup.txt)'
if (Test-Path -LiteralPath $warmUpDiag) {
    $warmed = Get-Content -LiteralPath $warmUpDiag -Raw
    Assert ($warmed -match 'QuickLook\.Plugin\.') "preview warm-up covered preview families: $($warmed.Trim())"
}

# v5.0.1: the update UI used to show Chinese on English systems - the new Update_* strings had only been
# added to zh-CN/zh-TW, and the translation fallback chain is "current language -> parent language -> en ->
# Chinese failsafe", so English users landed on the Chinese fallback text. Here the keys actually used in
# code are checked against the en / zh-CN / zh-TW blocks, and a missing one fails (en is the end of the
# fallback chain, so it must be complete).
$translationFile = Join-Path $root 'QuickLookNext\Translations.config'
[xml]$translations = Get-Content -LiteralPath $translationFile -Raw
$usedUpdateKeys = Get-ChildItem (Join-Path $root 'QuickLookNext') -Recurse -Filter *.cs |
    Select-String -Pattern 'TranslationHelper\.Get\("(Update_[A-Za-z]+)"' -AllMatches |
    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
Assert ($usedUpdateKeys.Count -gt 0) 'found the update UI string keys'
foreach ($locale in 'en', 'zh-CN', 'zh-TW') {
    $missingKeys = @($usedUpdateKeys | Where-Object { $null -eq $translations.Translations.$locale.SelectSingleNode($_) })
    Assert ($missingKeys.Count -eq 0) "update UI strings are complete for $locale (missing: $($missingKeys -join ', '))"
}

# v3.41.0: update prompt - open the real dialog with a fake release; in smoke mode it closes itself after
# 2 seconds and writes a diagnostic (material + size + button labels + UI language), which the assertions
# below use for the material and the strings.
$dialogDiag = Join-Path $smoke 'update-dialog.txt'
Remove-Item -LiteralPath $dialogDiag -Force -ErrorAction SilentlyContinue
Start-Process -FilePath $exe -ArgumentList '/test-update-prompt'
for ($i = 0; $i -lt 30 -and -not (Test-Path -LiteralPath $dialogDiag); $i++) { Start-Sleep -Milliseconds 300 }
$dialogText = if (Test-Path -LiteralPath $dialogDiag) { Get-Content -LiteralPath $dialogDiag -Raw } else { '' }
Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force

Assert ($dialogText -match 'accent-applied=True') 'update dialog uses the same material as the tray menu'
Assert ($dialogText -match 'material=(host-backdrop|acrylic)') 'update dialog reported the material it used'
Assert ($dialogText -match 'update=.*;ignore=') 'update dialog has both the Update now and Ignore buttons'
# v5.0.1: the dialog strings must follow the UI language and must not fall back to the Chinese failsafe.
$uiLang = if ($dialogText -match 'language=([A-Za-z-]+)') { $Matches[1] } else { '' }
Assert ($uiLang -ne '') 'update dialog reported the UI language'
if ($uiLang -notlike 'zh*') {
    Assert ($dialogText -notmatch '[\u4e00-\u9fff]') "update dialog has no Chinese fallback on an English UI (language=$uiLang)"
}

# v3.43.0: download progress panel - the same material + progress really advancing + a switch to
# "installing" when done.
$progressDiag = Join-Path $smoke 'update-progress.txt'
Remove-Item -LiteralPath $progressDiag -Force -ErrorAction SilentlyContinue
Start-Process -FilePath $exe -ArgumentList '/test-update-progress'
for ($i = 0; $i -lt 30 -and -not (Test-Path -LiteralPath $progressDiag); $i++) { Start-Sleep -Milliseconds 300 }
$progressText = if (Test-Path -LiteralPath $progressDiag) { Get-Content -LiteralPath $progressDiag -Raw } else { '' }
Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force

Assert ($progressText -match 'accent-applied=True') 'download progress panel uses the same material'
Assert ($progressText -match 'material=(host-backdrop|acrylic)') 'download progress panel reported the material it used'
Assert ($progressText -match 'detail=100%.*MB') 'download progress panel shows the progress and the downloaded size'
# v5.0.1: the status string follows the UI language too (it used to be asserted as Chinese, which was
# really asserting that bug).
$progressLang = if ($progressText -match 'language=([A-Za-z-]+)') { $Matches[1] } else { '' }
Assert ($progressLang -ne '') 'download progress panel reported the UI language'
if ($progressLang -like 'zh*') {
    Assert ($progressText -match 'status=.*安装') 'progress panel switches to the install message when the download finishes'
}
else {
    Assert ($progressText -match '(?i)status=.*installing') 'progress panel switches to the install message when the download finishes (follows the UI language)'
    Assert ($progressText -notmatch '[\u4e00-\u9fff]') "download progress panel has no Chinese fallback on an English UI (language=$progressLang)"
}

# ---------- 6. Shell integration (the space-bar chain: reading the Explorer selection) ----------
Write-Host "== 8/9 Shell integration ==" -ForegroundColor Cyan
$shellProbe = @"
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
public class ShellProbe2 {
    private static readonly Guid IID_IDataObject = new Guid("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider { [PreserveSig] int QueryService(ref Guid g, ref Guid r, out IntPtr p); }
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(int f);
        [PreserveSig] int InsertMenusSB(IntPtr a, IntPtr b);
        [PreserveSig] int SetMenuSB(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int RemoveMenusSB(IntPtr a);
        [PreserveSig] int SetStatusTextSB(IntPtr a);
        [PreserveSig] int EnableModelessSB(int f);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr a, ushort b);
        [PreserveSig] int BrowseObject(IntPtr a, uint b);
        [PreserveSig] int GetViewStateStream(uint a, out IntPtr b);
        [PreserveSig] int GetControlWindow(uint a, out IntPtr b);
        [PreserveSig] int SendControlMsg(uint a, uint b, IntPtr c, IntPtr d, out IntPtr e);
        [PreserveSig] int QueryActiveShellView(out IntPtr ppshv);
        [PreserveSig] int OnViewWindowActive(IntPtr a);
        [PreserveSig] int SetToolbarItems(IntPtr a, uint b, uint c);
    }
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(int f);
        [PreserveSig] int TranslateAccelerator(IntPtr a);
        [PreserveSig] int EnableModeless(int f);
        [PreserveSig] int UIActivate(uint s);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(IntPtr a, IntPtr b, uint c, IntPtr d, out IntPtr e);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(out IntPtr a);
        [PreserveSig] int AddPropertySheetPages(uint a, IntPtr b, IntPtr c);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(IntPtr a, uint b);
        [PreserveSig] int GetItemObject(uint u, ref Guid r, out IntPtr p);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int DragQueryFile(IntPtr h, uint i, StringBuilder b, int c);
    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref STGMEDIUM m);
    public static string Probe() {
        Type swType = Type.GetTypeFromCLSID(CLSID_ShellWindows);
        object sw = Activator.CreateInstance(swType);
        int count = (int)swType.InvokeMember("Count", BindingFlags.GetProperty, null, sw, null);
        for (int i = 0; i < count; i++) {
            object disp = swType.InvokeMember("Item", BindingFlags.InvokeMethod, null, sw, new object[] { i });
            var sp = (IServiceProvider)disp;
            Guid iid = IID_IShellBrowser;
            IntPtr sbPtr;
            if (sp.QueryService(ref iid, ref iid, out sbPtr) != 0) continue;
            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(sbPtr);
            IntPtr psvPtr;
            if (browser.QueryActiveShellView(out psvPtr) != 0 || psvPtr == IntPtr.Zero) continue;
            var view = (IShellView)Marshal.GetObjectForIUnknown(psvPtr);
            Guid diid = IID_IDataObject;
            IntPtr daoPtr;
            if (view.GetItemObject(0x1, ref diid, out daoPtr) != 0 || daoPtr == IntPtr.Zero) continue;
            var dao = (IDataObject)Marshal.GetObjectForIUnknown(daoPtr);
            var fe = new FORMATETC { cfFormat = 15, ptd = IntPtr.Zero, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            STGMEDIUM sm;
            try { dao.GetData(ref fe, out sm); }
            catch { return string.Empty; }
            try {
                var sb = new StringBuilder(32767);
                return DragQueryFile(sm.unionmember, 0, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
            }
            finally { ReleaseStgMedium(ref sm); }
        }
        return string.Empty;
 }
}
"@
Add-Type -TypeDefinition $shellProbe

# The selection probe depends on Explorer actually exposing a live file
# selection, which can be unavailable in non-interactive / minimized sessions
# (explorer.exe may refuse to open a /select window at all). Retry a few times;
# if no Explorer window can provide a selection, SKIP instead of failing - the
# app's own selection chain is exercised by the same COM probe whenever
# Explorer cooperates.
$probeResult = $null
for ($attempt = 1; $attempt -le 3; $attempt++) {
    $selWin = Get-Process explorer -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -like '*ql-smoke*' } | Select-Object -First 1
    if ($selWin) { $selWin.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2 }

    Start-Process explorer.exe "/select,`"$smoke\test.png`""
    Start-Sleep -Seconds 8

    $probeResult = [ShellProbe2]::Probe()
    if (-not [string]::IsNullOrWhiteSpace($probeResult)) { break }
}

if (-not [string]::IsNullOrWhiteSpace($probeResult)) {
    Assert $true "Explorer selection read path (COM probe) returned: $probeResult"
}
else {
    Write-Host "SKIP: no Explorer window in this environment for the selection probe to read (does not affect app behaviour)" -ForegroundColor Yellow
}

# ---------- 7. Cleanup ----------
Write-Host "== 9/9 Cleanup ==" -ForegroundColor Cyan
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
# v3.31.0: also clean up anything a preview request may have spawned while the
# main instance was gone, so the next run starts from a known state.
Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force

if ($failed) {
    Write-Host "`n=== TESTS FAILED ===" -ForegroundColor Red
    exit 1
}
Write-Host "`n=== ALL TESTS PASSED ===" -ForegroundColor Green
exit 0
