# Creates a reusable preview test set on the desktop. The media is synthetic
# and short; the Office files are generated through the installed Office
# applications when available. Existing unrelated files in the target folder
# are never deleted.

[CmdletBinding()]
param(
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([string]::IsNullOrWhiteSpace($Path)) {
    $Path = Join-Path ([Environment]::GetFolderPath('Desktop')) 'QKN-Test'
}

$Path = [IO.Path]::GetFullPath($Path)
$utf8 = [Text.UTF8Encoding]::new($false)

$text = Join-Path $Path '01-Text'
$images = Join-Path $Path '02-Images'
$video = Join-Path $Path '03-Video'
$audio = Join-Path $Path '04-Audio'
$office = Join-Path $Path '05-Office'
$web = Join-Path $Path '06-Web-and-Mail'
$archives = Join-Path $Path '07-Archives'
$fonts = Join-Path $Path '08-Fonts'
$other = Join-Path $Path '09-Other'

foreach ($folder in @($Path, $text, $images, $video, $audio, $office, $web, $archives, $fonts, $other)) {
    [void][IO.Directory]::CreateDirectory($folder)
}

function Write-TextFile {
    param([string]$FilePath, [string]$Content)
    [IO.File]::WriteAllText($FilePath, $Content, $utf8)
}

function Invoke-Ffmpeg {
    param([string[]]$Arguments)
    if ($null -eq $script:Ffmpeg) {
        return
    }

    & $script:Ffmpeg @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg failed with exit code $LASTEXITCODE"
    }
}

function New-TextSamples {
    Write-TextFile (Join-Path $text 'README.txt') @'
QuickLook-Next 文本预览样本

这个文件夹用于测试：
- 编码识别：UTF-8、中文、长行、空行。
- 文本大文件：sample-large.log。
- 代码高亮：sample.cs、sample.ts、sample.ps1。
- 结构化文本：JSON、XML、YAML、TOML、INI。
- Markdown 的标题、表格、代码块、引用和任务列表。
- TypeScript 源码不能被误判为 MPEG-TS 视频。
'@

    Write-TextFile (Join-Path $text 'Markdown-Sample.md') @'
# QuickLook-Next Markdown 测试

> 这段引用用于检查背景、边距和文字对比度。

## 表格

| 项目 | 值 | 说明 |
| --- | --- | --- |
| 版本 | 6.0.0 | 稳定基线 |
| 主题 | 浅色 / 深色 | 跟随系统 |
| 测试 | 快速切换 | 观察闪烁和旧内容残留 |

## 代码

```csharp
var preview = new PreviewWindow();
preview.Show(path);
```

- [x] 标题层级
- [x] 表格
- [x] 代码块
- [ ] 你的下一项测试

## 相对路径图片

![渐变图片](../02-Images/Gradient-1200x800.png)
'@

    Write-TextFile (Join-Path $text 'sample.json') @'
{
  "name": "QuickLook-Next",
  "version": "6.0.0",
  "formats": ["markdown", "html", "office", "video"],
  "nested": {
    "enabled": true,
    "count": 3
  }
}
'@

    Write-TextFile (Join-Path $text 'sample.xml') @'
<?xml version="1.0" encoding="utf-8"?>
<preview name="QuickLook-Next" version="6.0.0">
  <family>Markdown</family>
  <family>Office</family>
  <family>Video</family>
  <notes>XML syntax highlighting sample.</notes>
</preview>
'@

    Write-TextFile (Join-Path $text 'sample.yaml') @'
name: QuickLook-Next
version: 6.0.0
features:
  - preview
  - quick-switch
  - cache
settings:
  theme: system
  warmup: three-families
'@

    Write-TextFile (Join-Path $text 'sample.toml') @'
[application]
name = "QuickLook-Next"
version = "6.0.0"

[preview]
warmup = true
families = 3
'@

    Write-TextFile (Join-Path $text 'sample.ini') @'
[QuickLookNext]
Theme=System
WarmUp=ThreeFamilies
WebView2IdleTimeoutSeconds=300
'@

    $csv = [Text.StringBuilder]::new()
    [void]$csv.AppendLine('序号,名称,类型,大小MB,备注')
    for ($i = 1; $i -le 250; $i++) {
        [void]$csv.AppendLine("$i,样本文件-$i,$(if ($i % 3 -eq 0) {'视频'} else {'文档'}),$([Math]::Round($i * 0.37, 2)),快速滚动测试")
    }
    Write-TextFile (Join-Path $text 'sample.csv') $csv.ToString()

    $tsv = [Text.StringBuilder]::new()
    [void]$tsv.AppendLine("编号`t名称`t状态`t数值")
    for ($i = 1; $i -le 120; $i++) {
        [void]$tsv.AppendLine("$i`t项目-$i`t$(if ($i % 2) {'完成'} else {'进行中'})`t$($i * 11)")
    }
    Write-TextFile (Join-Path $text 'sample.tsv') $tsv.ToString()

    $log = [Text.StringBuilder]::new()
    for ($i = 1; $i -le 1800; $i++) {
        [void]$log.AppendLine(('{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] preview={2} elapsed={3}ms' -f
            (Get-Date).AddSeconds(-($i * 2)), $(if ($i % 9 -eq 0) {'WARN'} else {'INFO'}), "file-$i", ($i % 240)))
    }
    Write-TextFile (Join-Path $text 'sample-large.log') $log.ToString()

    Write-TextFile (Join-Path $text 'sample.cs') @'
namespace QuickLookNext.Sample;

public sealed class PreviewSample
{
    public string Name { get; init; } = "QuickLook-Next";

    public bool CanPreview(string path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
'@

    Write-TextFile (Join-Path $text 'sample.ts') @'
interface PreviewOptions {
    readonly path: string;
    readonly fullscreen?: boolean;
}

export function openPreview(options: PreviewOptions): void {
    console.log(`preview: ${options.path}`, options.fullscreen === true);
}
'@

    Write-TextFile (Join-Path $text 'sample.ps1') @'
param(
    [string]$FilePath = 'D:\Desktop\QKN-Test'
)

Get-ChildItem -LiteralPath $FilePath -File |
    Sort-Object Name |
    Select-Object Name, Length
'@

    Write-TextFile (Join-Path $text 'sample.rst') @'
QuickLook-Next RST sample
==========================

This file checks reStructuredText rendering.

* first item
* second item

.. code-block:: text

   preview --path sample.rst
'@

    Write-TextFile (Join-Path $text 'sample.adoc') @'
= QuickLook-Next AsciiDoc Sample
:toc:

== Overview

This file checks AsciiDoc rendering.

* one
* two

[source,csharp]
----
var preview = new PreviewWindow();
----
'@

    Write-TextFile (Join-Path $text 'sample.reg') @'
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\QuickLookNext\Sample]
"Theme"="System"
"PreviewCount"=dword:00000003
"Path"="D:\\Desktop\\QKN-Test"
'@

    Write-TextFile (Join-Path $text 'sample.url') @'
[InternetShortcut]
URL=https://github.com/Adstrax/Quicklook-Next
IconIndex=0
'@
}

function New-ImageSamples {
    Add-Type -AssemblyName System.Drawing

    function New-GradientImage {
        param(
            [string]$FilePath,
            [int]$Width,
            [int]$Height,
            [string]$Caption,
            [bool]$Transparent
        )

        $pixelFormat = if ($Transparent) {
            [Drawing.Imaging.PixelFormat]::Format32bppArgb
        } else {
            [Drawing.Imaging.PixelFormat]::Format24bppRgb
        }

        $bitmap = [Drawing.Bitmap]::new($Width, $Height, $pixelFormat)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit

            if ($Transparent) {
                $graphics.Clear([Drawing.Color]::Transparent)
            }

            $rect = [Drawing.Rectangle]::new(0, 0, $Width, $Height)
            $brush = [Drawing.Drawing2D.LinearGradientBrush]::new(
                $rect,
                [Drawing.Color]::FromArgb(255, 24, 86, 150),
                [Drawing.Color]::FromArgb(255, 37, 184, 160),
                [Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
            try {
                $graphics.FillRectangle($brush, $rect)
            } finally {
                $brush.Dispose()
            }

            $overlay = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(72, 0, 0, 0))
            try {
                $graphics.FillEllipse($overlay, [int]($Width * 0.12), [int]($Height * 0.15),
                    [int]($Width * 0.76), [int]($Height * 0.70))
            } finally {
                $overlay.Dispose()
            }

            $font = [Drawing.Font]::new('Segoe UI', [Math]::Max(22, [int]($Width / 18)),
                [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
            $white = [Drawing.Brushes]::White
            $format = [Drawing.StringFormat]::new()
            try {
                $format.Alignment = [Drawing.StringAlignment]::Center
                $format.LineAlignment = [Drawing.StringAlignment]::Center
                $box = [Drawing.RectangleF]::new(20, 40, $Width - 40, $Height - 80)
                $graphics.DrawString($Caption, $font, $white, $box, $format)
            } finally {
                $format.Dispose()
                $font.Dispose()
            }
        } finally {
            $graphics.Dispose()
        }

        try {
            switch ([IO.Path]::GetExtension($FilePath).ToLowerInvariant()) {
                '.png' { $bitmap.Save($FilePath, [Drawing.Imaging.ImageFormat]::Png) }
                '.jpg' { $bitmap.Save($FilePath, [Drawing.Imaging.ImageFormat]::Jpeg) }
                '.bmp' { $bitmap.Save($FilePath, [Drawing.Imaging.ImageFormat]::Bmp) }
                '.tif' { $bitmap.Save($FilePath, [Drawing.Imaging.ImageFormat]::Tiff) }
                default { throw "Unsupported image extension: $FilePath" }
            }
        } finally {
            $bitmap.Dispose()
        }
    }

    New-GradientImage (Join-Path $images 'Gradient-1200x800.png') 1200 800 'QuickLook-Next 1200 x 800' $false
    New-GradientImage (Join-Path $images 'Photo-Quality.jpg') 1600 1000 'JPEG photo-quality sample' $false
    New-GradientImage (Join-Path $images 'Portrait-600x1200.png') 600 1200 'Portrait 600 x 1200' $false
    New-GradientImage (Join-Path $images 'Transparent-Alpha.png') 800 800 'Transparent PNG' $true
    New-GradientImage (Join-Path $images 'Sample-800x600.bmp') 800 600 'BMP 800 x 600' $false
    New-GradientImage (Join-Path $images 'Scan-Sample.tif') 900 1200 'TIFF 900 x 1200' $false

    Write-TextFile (Join-Path $images 'Vector-Sample.svg') @'
<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="640" viewBox="0 0 1000 640">
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#155e9b"/>
      <stop offset="1" stop-color="#2ec4a6"/>
    </linearGradient>
  </defs>
  <rect width="1000" height="640" rx="36" fill="url(#g)"/>
  <circle cx="500" cy="290" r="170" fill="#ffffff" fill-opacity="0.16"/>
  <text x="500" y="310" text-anchor="middle" font-family="Segoe UI, sans-serif"
        font-size="58" font-weight="700" fill="#ffffff">SVG Preview</text>
  <text x="500" y="380" text-anchor="middle" font-family="Segoe UI, sans-serif"
        font-size="28" fill="#ffffff" fill-opacity="0.82">QuickLook-Next test asset</text>
</svg>
'@

    if ($script:Ffmpeg) {
        Invoke-Ffmpeg @(
            '-y', '-loglevel', 'error',
            '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=12',
            '-t', '3', '-vf', 'fps=12,scale=640:-1:flags=lanczos',
            (Join-Path $images 'Animated-640x360.gif'))

        Invoke-Ffmpeg @(
            '-y', '-loglevel', 'error',
            '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=15',
            '-t', '2', '-c:v', 'libwebp_anim', '-loop', '0', '-q:v', '72',
            (Join-Path $images 'Animated-640x360.webp'))
    }
}

function New-MediaSamples {
    if (-not $script:Ffmpeg) {
        Write-TextFile (Join-Path $video 'README.txt') 'ffmpeg was not available, so video/audio samples were skipped.'
        return
    }

    $mp4 = Join-Path $video 'Video-H264-1280x720.mp4'

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=30',
        '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000',
        '-t', '5', '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '24',
        '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '128k', '-shortest', $mp4)

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=360x640:rate=30',
        '-f', 'lavfi', '-i', 'sine=frequency=660:sample_rate=48000',
        '-t', '4', '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '24',
        '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '128k', '-shortest',
        (Join-Path $video 'Video-Portrait-360x640.mp4'))

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=30',
        '-f', 'lavfi', '-i', 'sine=frequency=520:sample_rate=48000',
        '-t', '4', '-c:v', 'libvpx-vp9', '-b:v', '700k',
        '-deadline', 'realtime', '-cpu-used', '8',
        '-c:a', 'libopus', '-b:a', '96k', '-shortest',
        (Join-Path $video 'Video-VP9-640x360.webm'))

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-i', $mp4, '-c', 'copy', (Join-Path $video 'Video-H264.mkv'))

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-i', $mp4, '-c', 'copy', (Join-Path $video 'Video-H264.mov'))

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-i', $mp4, '-c', 'copy', '-f', 'mpegts',
        (Join-Path $video 'Video-MPEG-TS.ts'))

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=25',
        '-f', 'lavfi', '-i', 'sine=frequency=380:sample_rate=44100',
        '-t', '4', '-c:v', 'mpeg4', '-q:v', '5',
        '-c:a', 'libmp3lame', '-b:a', '96k', '-shortest',
        (Join-Path $video 'Video-MPEG4-640x360.avi'))

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000',
        '-t', '5', (Join-Path $audio 'Audio-Sine-440Hz.wav'))

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'sine=frequency=523.25:sample_rate=44100',
        '-t', '5', '-c:a', 'libmp3lame', '-b:a', '160k',
        (Join-Path $audio 'Audio-Tone-160k.mp3'))

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'sine=frequency=659.25:sample_rate=48000',
        '-t', '5', '-c:a', 'flac',
        (Join-Path $audio 'Audio-Lossless.flac'))

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'sine=frequency=392:sample_rate=48000',
        '-t', '5', '-c:a', 'aac', '-b:a', '128k',
        (Join-Path $audio 'Audio-AAC.m4a'))

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'sine=frequency=294:sample_rate=48000',
        '-t', '5', '-c:a', 'libopus', '-b:a', '96k',
        (Join-Path $audio 'Audio-Opus.ogg'))

    Invoke-Ffmpeg @(
        '-y', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'sine=frequency=349.23:sample_rate=48000',
        '-t', '5', '-c:a', 'libopus', '-b:a', '96k',
        (Join-Path $audio 'Audio-Opus.opus'))

    $midi = Join-Path $env:WINDIR 'Media\flourish.mid'
    if (Test-Path -LiteralPath $midi) {
        Copy-Item -LiteralPath $midi -Destination (Join-Path $audio 'Audio-MIDI.mid') -Force
    }

    Write-TextFile (Join-Path $video 'Playlist-Sample.m3u') @'
#EXTM3U
Video-H264-1280x720.mp4
Video-VP9-640x360.webm
..\04-Audio\Audio-Tone-160k.mp3
'@
}

function New-OfficeSamples {
    $imagePath = Join-Path $images 'Gradient-1200x800.png'

    try {
        $word = New-Object -ComObject Word.Application
        $word.Visible = $false
        $word.DisplayAlerts = 0
        $doc = $null
        try {
            $doc = $word.Documents.Add()

            $p = $doc.Paragraphs.Add()
            $p.Range.Text = 'QuickLook-Next 文档测试'
            $p.Range.Style = $doc.Styles.Item(-2)

            $p = $doc.Paragraphs.Add()
            $p.Range.Text = '这份文档包含标题、正文、表格、项目符号和图片，用于检查 Office 预览的排版、滚动和快速切换。'

            $p = $doc.Paragraphs.Add()
            $p.Range.Text = '第一部分：文本与字体'
            $p.Range.Style = $doc.Styles.Item(-3)

            $p = $doc.Paragraphs.Add()
            $p.Range.Text = '正文使用中英文混排：QuickLook-Next preview sample 0123456789。'

            $range = $doc.Range()
            $range.Collapse(0)
            $table = $doc.Tables.Add($range, 4, 3)
            $table.Cell(1, 1).Range.Text = '测试项'
            $table.Cell(1, 2).Range.Text = '结果'
            $table.Cell(1, 3).Range.Text = '备注'
            $table.Cell(2, 1).Range.Text = '标题'
            $table.Cell(2, 2).Range.Text = '正常'
            $table.Cell(2, 3).Range.Text = '检查字体大小'
            $table.Cell(3, 1).Range.Text = '表格'
            $table.Cell(3, 2).Range.Text = '正常'
            $table.Cell(3, 3).Range.Text = '检查列宽'
            $table.Cell(4, 1).Range.Text = '图片'
            $table.Cell(4, 2).Range.Text = '待检查'
            $table.Cell(4, 3).Range.Text = '检查缩放'
            $table.Range.Font.Size = 11

            $doc.Content.InsertParagraphAfter()
            [void]$doc.InlineShapes.AddPicture($imagePath, $false, $true)

            $p = $doc.Paragraphs.Add()
            $p.Range.Text = '第二部分：长段落'
            $p.Range.Style = $doc.Styles.Item(-3)

            $long = ('QuickLook-Next 的文档预览应保持页面宽度、文字对比度和滚动位置。' +
                '这一段重复用于制造足够长的页面，以便检查窗口调整和连续切换。 ' * 18)
            $p = $doc.Paragraphs.Add()
            $p.Range.Text = $long

            $docx = Join-Path $office 'Document-With-Table.docx'
            $doc.SaveAs2($docx, 16)

            $doc.ExportAsFixedFormat((Join-Path $office 'Document-With-Table.pdf'), 17)

            $doc.SaveAs2((Join-Path $office 'Document-Legacy.doc'), 0)
        } finally {
            try { if ($null -ne $doc) { $doc.Close($false) } } catch { }
            try { $word.Quit() } catch { }
            try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($word) } catch { }
        }
    }
    catch {
        Write-Warning "Word sample generation failed at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)"
    }

    try {
        $excel = New-Object -ComObject Excel.Application
        $excel.Visible = $false
        $excel.DisplayAlerts = $false
        $book = $null
        try {
            $book = $excel.Workbooks.Add()
            $sheet = $book.Worksheets.Item(1)
            $sheet.Name = '销售数据'
            $headers = @('日期', '产品', '数量', '单价', '金额')
            for ($c = 1; $c -le $headers.Count; $c++) {
                $sheet.Cells.Item(1, $c).Value2 = $headers[$c - 1]
            }
            $sheet.Range('A1:E1').Font.Bold = $true
            $sheet.Range('A1:E1').Interior.Color = 15773696
            $sheet.Range('A1:E1').Font.Color = 16777215

            for ($r = 2; $r -le 42; $r++) {
                $sheet.Cells.Item($r, 1).Value2 = (Get-Date).AddDays(-($r - 2)).ToString('yyyy-MM-dd')
                $sheet.Cells.Item($r, 2).Value2 = @('键盘', '显示器', '扩展坞', '摄像头')[$r % 4]
                $sheet.Cells.Item($r, 3).Formula = [string]((($r * 7) % 29) + 1)
                $sheet.Cells.Item($r, 4).Formula = (39.9 + ($r % 6) * 25).ToString(
                    '0.00', [Globalization.CultureInfo]::InvariantCulture)
                $sheet.Cells.Item($r, 5).Formula = "=C$r*D$r"
            }
            $sheet.Range('D2:E42').NumberFormat = '¥#,##0.00'
            $sheet.Range('A1:E42').Borders.LineStyle = 1
            [void]$sheet.Columns.AutoFit()
            $sheet.Activate()
            [void]$sheet.Range('A1').Select()

            $notes = $book.Worksheets.Add()
            $notes.Name = '说明'
            $notes.Cells.Item(1, 1).Value2 = 'QuickLook-Next 表格预览样本'
            $notes.Cells.Item(2, 1).Value2 = '第二张工作表用于检查工作表标签和切换。'
            $notes.Cells.Item(4, 1).Value2 = '公式位于主表的 E 列。'
            [void]$notes.Columns.AutoFit()

            $book.SaveAs((Join-Path $office 'Spreadsheet-Formulas.xlsx'), 51)

            $book.ExportAsFixedFormat(0, (Join-Path $office 'Spreadsheet-Formulas.pdf'))

            $book.SaveAs((Join-Path $office 'Spreadsheet-Legacy.xls'), 56)
        } finally {
            try { if ($null -ne $book) { $book.Close($false) } } catch { }
            try { $excel.Quit() } catch { }
            try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel) } catch { }
        }
    }
    catch {
        Write-Warning "Excel sample generation failed at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)"
    }

    try {
        $powerPoint = New-Object -ComObject PowerPoint.Application
        try { $powerPoint.Visible = $false } catch { }
        try { $powerPoint.DisplayAlerts = 1 } catch { }
        $presentation = $null
        try {
            $presentation = $powerPoint.Presentations.Add()

            $slide1 = $presentation.Slides.Add(1, 1)
            $slide1.Shapes.Title.TextFrame.TextRange.Text = 'QuickLook-Next 演示文稿测试'
            $slide1.Shapes.Item(2).TextFrame.TextRange.Text = '标题页、正文页、图片页'

            $slide2 = $presentation.Slides.Add(2, 2)
            $slide2.Shapes.Title.TextFrame.TextRange.Text = '第二页：快速切换'
            $slide2.Shapes.Item(2).TextFrame.TextRange.Text =
                "检查标题层级`r`n检查项目符号`r`n检查长文本换行`r`n检查深色和浅色背景"

            $slide3 = $presentation.Slides.Add(3, 5)
            $slide3.Shapes.Title.TextFrame.TextRange.Text = '第三页：图片与比例'
            [void]$slide3.Shapes.AddPicture($imagePath, 0, -1, 110, 145, 740, 420)

            $pptx = Join-Path $office 'Presentation-Three-Slides.pptx'
            $presentation.SaveAs($pptx, 24)

            $presentation.SaveAs((Join-Path $office 'Presentation-Three-Slides.pdf'), 32)

            $presentation.SaveAs((Join-Path $office 'Presentation-Legacy.ppt'), 1)
        } finally {
            try { if ($null -ne $presentation) { $presentation.Close() } } catch { }
            try { $powerPoint.Quit() } catch { }
            try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($powerPoint) } catch { }
        }
    }
    catch {
        Write-Warning "PowerPoint sample generation failed at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)"
    }

    Write-TextFile (Join-Path $office 'Rich-Text-Sample.rtf') @'
{\rtf1\ansi\deff0
{\fonttbl{\f0 Segoe UI;}{\f1 Consolas;}}
\f0\fs32\b QuickLook-Next RTF Sample\b0\par
\fs22 This paragraph checks basic formatting in the native RTF preview.\par
\f1\fs20 preview --path "Rich-Text-Sample.rtf"\f0\par
}
'@

    if (-not (Test-Path -LiteralPath (Join-Path $office 'Document-With-Table.pdf'))) {
        Write-Warning 'No PDF sample was produced; install Word or add a PDF manually.'
    }
}

function New-MailSamples {
    $plain = @'
QuickLook-Next 邮件预览测试

这是一封纯文本邮件，用于检查编码、换行和长段落。
附件中的 attachment.txt 只包含测试文字。
'@
    $html = @'
<!doctype html>
<html><head><meta charset="utf-8">
<style>
body { font-family: Segoe UI, sans-serif; color: #1d2733; padding: 18px; }
.card { border: 1px solid #c8d3df; border-radius: 10px; padding: 16px; background: #f6f9fc; }
h1 { color: #155e9b; font-size: 24px; margin-top: 0; }
</style></head>
<body><div class="card">
<h1>QuickLook-Next 邮件预览测试</h1>
<p>这是一封 <strong>HTML 邮件</strong>，包含标题、样式、表格和一段较长的中文正文。</p>
<table border="1" cellpadding="6" cellspacing="0">
<tr><th>项目</th><th>状态</th></tr>
<tr><td>HTML 正文</td><td>正常</td></tr>
<tr><td>附件</td><td>正常</td></tr>
</table>
<p>快速连续切换邮件时，应保持背景正常，不出现旧邮件残留。</p>
</div></body></html>
'@
    $attachment = "QuickLook-Next attachment sample.`r`nThis file is safe test content.`r`n"

    $plain64 = [Convert]::ToBase64String($utf8.GetBytes($plain))
    $html64 = [Convert]::ToBase64String($utf8.GetBytes($html))
    $attachment64 = [Convert]::ToBase64String($utf8.GetBytes($attachment))

    $lines = @(
        'From: QKN Test <qkn@example.test>',
        'To: Preview Test <preview@example.test>',
        'Subject: QKN HTML mail sample',
        'Date: Fri, 17 Oct 2026 12:00:00 +0800',
        'MIME-Version: 1.0',
        'Content-Type: multipart/mixed; boundary="qkn-mixed"',
        '',
        '--qkn-mixed',
        'Content-Type: multipart/alternative; boundary="qkn-alt"',
        '',
        '--qkn-alt',
        'Content-Type: text/plain; charset=utf-8',
        'Content-Transfer-Encoding: base64',
        '',
        $plain64,
        '--qkn-alt',
        'Content-Type: text/html; charset=utf-8',
        'Content-Transfer-Encoding: base64',
        '',
        $html64,
        '--qkn-alt--',
        '',
        '--qkn-mixed',
        'Content-Type: text/plain; name="attachment.txt"',
        'Content-Disposition: attachment; filename="attachment.txt"',
        'Content-Transfer-Encoding: base64',
        '',
        $attachment64,
        '--qkn-mixed--',
        ''
    )
    Write-TextFile (Join-Path $web 'Mail-HTML-with-Attachment.eml') ($lines -join "`r`n")

    Write-TextFile (Join-Path $web 'Standalone-HTML.html') @'
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>QuickLook-Next HTML sample</title>
<style>
:root { color-scheme: light dark; }
body { margin: 0; font-family: "Segoe UI", sans-serif; background: #f3f7fb; color: #172230; }
main { max-width: 880px; margin: 0 auto; padding: 48px 28px; }
.hero { border-radius: 24px; padding: 42px; color: white;
  background: linear-gradient(135deg, #155e9b, #2ec4a6); }
h1 { margin: 0 0 12px; font-size: 44px; }
.grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; margin-top: 24px; }
.card { background: white; border: 1px solid #d6e1eb; border-radius: 16px; padding: 20px; }
code { font-family: Consolas, monospace; }
@media (prefers-color-scheme: dark) {
  body { background: #101820; color: #eef4fa; }
  .card { background: #182530; border-color: #314352; }
}
</style>
</head>
<body><main>
  <section class="hero">
    <h1>HTML Preview</h1>
    <p>检查滚动、主题、字体和布局。</p>
  </section>
  <section class="grid">
    <article class="card"><h2>Markdown</h2><p>连续切换时不应闪烁。</p></article>
    <article class="card"><h2>Office</h2><p>旧文档不能覆盖新文档。</p></article>
    <article class="card"><h2>Media</h2><p>窗口尺寸应与内容匹配。</p></article>
  </section>
  <p><code>QuickLook-Next / 6.0.0</code></p>
</main></body></html>
'@

    Write-TextFile (Join-Path $web 'Interactive-HTML.html') @'
<!doctype html>
<html><head><meta charset="utf-8"><title>Interactive HTML</title>
<style>
body { font-family: Segoe UI, sans-serif; display: grid; place-items: center;
  min-height: 100vh; margin: 0; background: #111827; color: #f9fafb; }
button { font-size: 20px; padding: 14px 24px; border: 0; border-radius: 12px;
  background: #2ec4a6; color: #06221c; cursor: pointer; }
.box { text-align: center; }
</style></head>
<body><div class="box">
<h1>Interactive HTML</h1>
<p id="value">0</p>
<button onclick="const v=document.getElementById('value');v.textContent=Number(v.textContent)+1">Click me</button>
</div></body></html>
'@
}

function New-ArchiveSamples {
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $stage = Join-Path ([IO.Path]::GetTempPath()) ('QKN-Test-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage)
    try {
        Write-TextFile (Join-Path $stage 'inside.txt') "This file is inside the archive.`r`nQuickLook-Next sample."
        Copy-Item -LiteralPath (Join-Path $images 'Gradient-1200x800.png') -Destination (Join-Path $stage 'preview.png') -Force
        [void][IO.Directory]::CreateDirectory((Join-Path $stage 'nested'))
        Write-TextFile (Join-Path $stage 'nested\notes.md') "# Nested folder`r`nArchive navigation sample."

        $zip = Join-Path $archives 'Archive-Sample.zip'
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

        $tar = Join-Path $archives 'Archive-Sample.tar'
        $tgz = Join-Path $archives 'Archive-Sample.tar.gz'
        & tar -cf $tar -C $stage .
        if ($LASTEXITCODE -ne 0) { throw 'tar creation failed' }
        & tar -czf $tgz -C $stage .
        if ($LASTEXITCODE -ne 0) { throw 'tar.gz creation failed' }
    } finally {
        $resolvedStage = [IO.Path]::GetFullPath($stage)
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if ($resolvedStage.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedStage).StartsWith('QKN-Test-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
}

function New-FontSamples {
    $systemFonts = Join-Path $env:WINDIR 'Fonts'
    $fontFiles = @(
        @{ Source = 'segoeui.ttf'; Destination = 'SegoeUI.ttf' },
        @{ Source = 'segmdl2.ttf'; Destination = 'Segoe-MDL2-Icons.ttf' },
        @{ Source = 'msyh.ttc'; Destination = 'Microsoft-YaHei.ttc' },
        @{ Source = 'Noto Sans SC (TrueType).otf'; Destination = 'NotoSansSC.otf' }
    )

    foreach ($font in $fontFiles) {
        $source = Join-Path $systemFonts $font.Source
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $fonts $font.Destination) -Force
        }
    }
}

function New-OtherSamples {
    $bytes = [byte[]]::new(128 * 1024)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [byte](($i * 31 + 17) % 256)
    }
    [IO.File]::WriteAllBytes((Join-Path $other 'Unknown-Binary.bin'), $bytes)

    $release = Join-Path (Split-Path -Parent $PSScriptRoot) 'Build\Release'
    foreach ($copy in @(
        @{ Source = 'QuickLook-Next.exe'; Destination = 'Sample-Executable.exe' },
        @{ Source = 'QuickLook.Common.dll'; Destination = 'Sample-Library.dll' }
    )) {
        $source = Join-Path $release $copy.Source
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $other $copy.Destination) -Force
        }
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        $db = Join-Path $other 'Database-Sample.db'
        $code = @'
import sqlite3
import sys

connection = sqlite3.connect(sys.argv[1])
connection.executescript("""
DROP TABLE IF EXISTS preview_items;
CREATE TABLE preview_items (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    family TEXT NOT NULL,
    score REAL NOT NULL
);
INSERT INTO preview_items (name, family, score) VALUES
    ('Markdown', 'text', 98.5),
    ('Office', 'document', 96.0),
    ('Video', 'media', 94.5);
""")
connection.commit()
connection.close()
'@
        $code | & $python.Source - $db
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'SQLite sample generation failed.'
        }
    }

    Write-TextFile (Join-Path $other 'Readme-Notes.txt') @'
这个目录用于检查未知二进制、可执行文件、DLL 和 SQLite 数据库。
Unknown-Binary.bin 不是媒体文件，VideoViewer 不应误判。
Database-Sample.db 可以用于检查数据库预览。
'@
}

function Write-Readme {
    $readme = @'
# QKN-Test 预览测试素材

这个文件夹由 QuickLook-Next 的 `Scripts\New-QknTestFolder.ps1` 生成，用于集中测试常见预览格式。所有媒体都是合成的短样本；Office 文件由本机 Office 生成。

## 推荐测试顺序

1. `01-Text`：连续切换 Markdown、TXT、JSON、CSV、代码文件，检查闪烁、背景和旧内容残留。
2. `02-Images`：检查 PNG、JPEG、WebP、GIF、SVG、BMP、TIFF 的尺寸和缩放；尤其看竖图、透明图和动图。
3. `03-Video`：检查 MP4、WebM、MKV、MOV、AVI、MPEG-TS 的窗口比例与空白区域。
4. `04-Audio`：检查 WAV、MP3、FLAC、M4A、Ogg、Opus 和 MIDI 的封面/标签/播放状态。
5. `05-Office`：快速切换 DOCX、XLSX、PPTX，再切到 Markdown/HTML，确认旧文档不会回写。
6. `06-Web-and-Mail`：检查 HTML 样式、交互脚本和 EML 邮件正文。
7. `07-Archives`：检查 ZIP、TAR、TAR.GZ 的目录导航。
8. `08-Fonts`：检查 TTF、TTC、OTF 的字体列表和字形页。
9. `09-Other`：检查未知二进制、EXE、DLL 和 SQLite 数据库。

## 特别建议

- 在同一个文件夹里按住方向键或使用鼠标侧键快速切换。
- 使用 `03-Video\Video-MPEG-TS.ts` 和 `01-Text\sample.ts` 检查“TypeScript 与 MPEG-TS”的区分。
- 打开 Markdown 后立刻切到 Office，再切回 Markdown，检查是否白屏或显示旧内容。
- 关闭预览后等待 WebView2 空闲回收，观察进程和临时目录是否仍在增长。

重新生成：

```powershell
powershell -ExecutionPolicy Bypass -File "E:\Codex\QK-Lite\QuickLook-Next\Scripts\New-QknTestFolder.ps1"
```
'@
    Write-TextFile (Join-Path $Path 'README.md') $readme
}

$script:Ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source

New-TextSamples
New-ImageSamples
New-MediaSamples
New-OfficeSamples
New-MailSamples
New-ArchiveSamples
New-FontSamples
New-OtherSamples
Write-Readme

$files = @(Get-ChildItem -LiteralPath $Path -Recurse -File)
$bytes = ($files | Measure-Object Length -Sum).Sum
Write-Host ("QKN-Test ready: {0} files, {1:N1} MB, {2}" -f $files.Count, ($bytes / 1MB), $Path)
