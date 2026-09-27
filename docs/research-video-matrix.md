# Video preview robustness regression (2026-09-19)

We walked the whole "preview a video" path with a representative batch of files: how fast it opens,
whether a picture appears, whether failures degrade gracefully, and whether anything crashes.
The sample matrix covers containers / codecs / variants, **22 samples** in total, all generated with
ffmpeg (the scripts live in the repo, so the run is repeatable).

## Results

| | |
|---|---|
| 22/22 samples preview normally | First frame in 299–1422 ms (median ≈ 350 ms) |
| Crashes | **0** (before the fix: see find #1 below) |
| Timeouts / no preview | **0** |
| Deliberately broken files | 2 (0-byte, truncated), both **fail gracefully** and log the error instead of crashing |

Formats covered: H.264/MP4, H.265 (8-bit + 10-bit)/MP4+MKV, AV1/MKV, VP9+Opus/WebM, MPEG-2/AVI,
WMV2+WMA/WMV, H.264/MOV, H.264/TS, H.264+MP3/FLV, Theora+Vorbis/OGV, 4K H.264, a 6-minute long video,
portrait 240×320, landscape with rotation metadata, variable frame rate, no audio track, audio only
(MP3/M4A), Chinese characters plus spaces in the path, a truncated file, and a 0-byte file.

## find #1: an unplayable video **crashed the entire app** (fixed)

The first two lines of `QuickLook.Plugin.VideoViewer.ViewerPanel.MediaFailed` cleared the thumbnail
(`videoThumbnail.Source` / `Visibility`) directly, but **WPFMediaKit raises that callback on its own
worker thread** — touching the visual tree from a non-UI thread throws
`InvalidOperationException: The calling thread cannot access this object`, and because that exception
landed on a worker thread with no handler, **the process exited immediately**. The two 8-line stacks in
the log (0-byte file, truncated file) were exactly this.

What the user saw: previewing an unplayable video (corrupt file, missing codec, a `.mp4` in an
unsupported container) made QuickLook vanish altogether, instead of reporting "this video cannot be
played". This is the same class of symptom as upstream
[#1768](https://github.com/QL-Win/QuickLook/issues/1768) "Cannot open videos".

Fix (5.0.9): every UI operation is now dispatched to the window's Dispatcher; the user sees a one-line
message (`VV_PlaybackFailed`: 无法播放此视频 / This video could not be played), and the full exception is
written to the log. The **raw stack trace** that used to be displayed in the panel was replaced by that
message.

## find #2: a trap in the script itself (fixed; recorded so it is not repeated)

The first version of the scan script requested a preview with `Start-Process -ArgumentList $path` —
PowerShell's `-ArgumentList` **does not add quotes automatically**, so a path containing a space arrived
as two arguments in the child process and the app received a truncated path; that looked like
"video files with spaces fail to preview". It was briefly misdiagnosed as "Chinese paths are broken";
after isolating the variables we confirmed that Chinese was fine and the space was the problem, and that
it was the script's fault rather than the app's. After the fix, `中文 名称 测试.mp4` previews normally.

## How to re-run

```powershell
# 1) requires ffmpeg (winget install --id Gyan.FFmpeg -e); generates the 22 samples into ql-smoke\video-matrix
pwsh -NoProfile -File .\Scripts\make-video-matrix.ps1

# 2) previews each sample and reports duration / crashes / new log lines / window title; writes ql-smoke\video-matrix-results.txt
pwsh -NoProfile -File .\Scripts\run-video-matrix.ps1
```

The scan script **restarts the app automatically** with `/test-timing` if a sample kills it, so a single
crash does not contaminate the remaining samples.

## Locked in as a guard

`test.ps1` now truncates `test.mp4` into `test-corrupt.mp4` and previews it first (no ffmpeg needed to
produce it):

- it asserts the **process is still alive** (this is the guard for find #1: a crash fails the whole smoke run)
- it asserts an **error log entry was actually written** (failures must be reported, not swallowed silently)

## Not covered

- Network paths (UNC / mapped drives) and very long paths (more than 260 characters): these need a real
  environment and were not included
- HDR / Dolby Vision, and codecs that need extra decoders (for example the AV1 hardware-decode path):
  the samples are all software-decode friendly
- Dual-GPU switching: upstream [#1968](https://github.com/QL-Win/QuickLook/issues/1968) points at
  iGPU/dGPU switching; we already have the `UseHardwareAcceleration` switch (OPTIONS.md), but no
  automated way to produce a dual-GPU environment
- In-playback interaction (pause / seek / volume): only "a frame appears + no crash" was tested;
  interaction is still covered by manual testing
