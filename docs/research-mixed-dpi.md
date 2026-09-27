# Mixed-DPI multi-monitor setups: why the preview window used to span three screens, and the fix

> Status: **fixed and shipped in 5.0.10.** Recorded 2026-09-19.
>
> Origin: upstream [QL-Win/QuickLook#827](https://github.com/QL-Win/QuickLook/issues/827)
> — on a machine with a 4K laptop (internal 250%, external 175%) plus a 1080p monitor (100%),
> previewing a **large landscape image** on the external screen made the preview window span three
> monitors; portrait and small images were fine.

## 1. Summary

| Item | Conclusion |
|---|---|
| Root cause | **The monitor used to compute the size** and **the monitor used to place the window** were not the same one: the plugin measured against the screen holding the foreground window, while window placement used the screen the window itself was on |
| Why only landscape images broke | The fit ratio is `min(width ratio, height ratio)`: a landscape image is driven by **width**, so once a widescreen DIP size was scaled up onto a lower-scaling screen, the pixel width exceeded that screen |
| Why arrow-key switching still looked fine | That path takes the "existing window" branch, where the placement reference is still the window's own screen, so both sides agreed |
| Fix | (1) hand the **target screen** to the plugin when the preview starts (`ContextObject.HostDesktopSize`); (2) **clamp** against that same screen before applying the size; (3) refit when the DPI changes |
| What can be verified on this machine | single-screen results are **pixel-identical** to before the fix (see §4); the sizing math itself is covered by 12 unit tests |

## 2. Root cause

Two sizing references, each coming from a different screen:

1. **The plugin computes the size**: `ContextObject.SetPreferredSizeFit()`
   (`QuickLook.Common/Plugin/ContextObject.cs`) calls `WindowHelper.GetCurrentDesktopSize()`, whose
   `GetCurrentDesktopRectInPixel()` uses **`GetForegroundWindow()`** — that is, the screen the file
   manager is on. The plugin then hands `PreferredSize` to the host (images 0.8, PDF 0.9, Office 0.8
   ... all come through this entry point).

2. **The host places the window**: `ResizeAndCentreNewWindow()` in `ViewerWindow.Actions.cs` centres
   using `GetCurrentDesktopRectInPixel()` + `GetCurrentScaleFactor()`, while
   `ResizeAndCentreExistingWindow()` uses `GetDesktopRectFromWindowInPixel(this)` — **the screen the
   preview window itself is on**.

So "the size" and "the landing spot" came from two different screens:

- A size computed on the foreground window's screen (where the DIP desktop is larger, e.g.
  1920x1040@100%) would land on a higher-scaling screen (1536x864 DIP @250%), where converting to pixels
  multiplies by 2.5 — the landscape "width fills the screen" shape triggered first, so the window ended
  up wider than the screen and spanned multiple monitors.
- A portrait image is driven by **height**, and height has a 10% margin (`limitPercentY`) covering it,
  so it looked fine.

On top of that, `ResizeAndCentre*` only **clamped the position** (pulling the left/top edge back onto
the screen) and never clamped the size — when `pxNewRect` was wider than the screen the window still
overflowed, which is exactly the "three screens joined together" look in the screenshot.

## 3. The fix

Three changes, all on the principle that "the size and the landing spot must use the same screen":

1. **Hand the target screen to the plugin** (`BeginShow` in `ViewerWindow.Actions.cs`): on every preview
   start, before `Prepare`, set
   `ContextObject.HostDesktopSize = GetTargetDesktopSizeInDip()` — the target screen is the foreground
   window's screen (the centring reference for a new window), falling back to the preview window's own
   screen and then to the primary screen. `SetPreferredSizeFit()` prefers it and only falls back to the
   old `GetCurrentDesktopSize()` when it is unavailable.

2. **Clamp before applying the size** (`ComputeWindowSize(clampToDesktop: true)`): run `ClampToDesktop`
   against the DIP work area of the **screen it will land on**. Even if the plugin hard-codes a size, or
   the user moves the mouse to another screen after the preview opened, the window will never be larger
   than the screen it is about to land on. **Sizes the user dragged out themselves are not clamped** —
   that is the user's intent, not a sizing mistake. Later size requests from the plugin
   (`ApplyPreferredSizeNow`, e.g. once a PDF has measured its pages) are clamped the same way.

3. **Refit after a DPI change** (`ViewerWindow.OnDpiChanged` → `RefitWindowToItsOwnDesktop`): Windows
   preserves a window's **physical size** when it moves across screens, so the DIP size changes — a
   window that looked right on a 250% 4K screen can end up several screens wide on a 100% 1080p screen.
   As soon as the DPI changes, clamp again against the current screen (and mark that adjustment as a
   "host correction" so it never lands in the user's size memory).

The sizing math lives in `QuickLook.Common/Helpers/PreviewWindowSizing.cs` (pure functions):

- `FitRatio(content, desktop, maxRatio)`: takes `min(width ratio, height ratio)` and **never scales up**
  (capped at 1);
- bad input cannot produce a broken window: `maxRatio` of 0/negative/NaN is treated as "the whole
  screen", and a failed desktop query (0x0) returns the input unchanged, so neither case squashes the
  preview to 0 pixels;
- `ClampToDesktop(size, desktop)`: only shrinks, never grows, and does nothing when the desktop is
  unknown.

## 4. Verification

### 4.1 Unit tests (no second screen required)

`QuickLook.Tests/PreviewWindowSizingTests.cs`, 12 cases, covering the two extreme screens from #827
(4K@250% → 1536x864 DIP, 1080p@100% → 1920x1040 DIP) and the wide-and-flat 4289x631 shape:

- something that already fits is not scaled up; landscape converges by width, portrait by height; a cap
  above 1 is treated as 1;
- invalid ratios (0/negative/NaN) and an unknown desktop never produce a 0-sized window;
- clamping only shrinks and never grows, and does nothing when the desktop is unknown;
- end-to-end shape: a page measured at 90% on a 1080p screen (936 DIP high) placed on a 250% 4K screen
  is 2340 px tall before clamping, which exceeds the 2160 px panel (the "spans multiple screens" case),
  and exactly 2160 px after clamping, so it lands inside the screen.

Test suite: **60/60 passing** (48 existing + 12 new).

### 4.2 Single-screen invariance (reproducible on this machine)

The change goes through the new "target screen" path, so we have to prove on a single-screen machine that
the result **did not change**. This machine: one screen, 1536x960 DIP @200% (physical 3072x1920).

How to reproduce (the hidden switch `/test-preview-diag` writes every placement result to
`<smokeDir>\preview-rect.txt`):

```powershell
pwsh -NoProfile -Command {
  $env:QL_SMOKE_DIR = '<repo>\ql-smoke'
  Start-Process '<repo>\Build\Release\QuickLook-Next.exe' -ArgumentList '/autorun','/test-preview-diag'
  Start-Sleep 3
  Start-Process '<repo>\Build\Release\QuickLook-Next.exe' -ArgumentList '<repo>\ql-smoke\big-image-6000x4000.png'
  Start-Sleep 3
  Get-Content '<repo>\ql-smoke\preview-rect.txt'
}
```

For a 6000x4000 image:

| | Before the fix | After the fix |
|---|---|---|
| Window (DIP) | 1154x770 | 1154x770 |
| Window (pixels) | 2308x1540 @ (382,190) | 2308x1540 @ (382,190) |

Pixel-identical, which shows that on a single screen this change is a **path replacement** rather than a
behaviour change. We also previewed a png / pdf / md each: the process stayed alive and every window was
centred inside `monitorPx=(0,0,3072,1920)`.

### 4.3 The placement rules are now testable on their own (v5.5.0)

The 9/10 rule set that "keeps the centre and then pulls the window back onto the screen" was extracted
from `ViewerWindow` into `QuickLookNext/Helpers/WindowPlacement.cs` (pure functions), so the mixed-DPI
geometry can be tested **without a second screen**:

- `WindowPlacementTests` uses the machine from that issue (4K@250% + 1080p@100%) to verify: the left
  third keeps the left edge, the right/bottom thirds keep the right/bottom edges, and the middle gets
  pushed back onto the screen;
- there is a dedicated case for the **full "clamp the size, then place" chain**: a 2600x1400 DIP window
  is clamped to 1920x1080 on a 1080p screen and lands at `(0,0)`, fully on screen;
- the tests also exposed a pre-existing edge case: right/bottom anchors are positioned from the old
  window's right/bottom edge, so a window that grows a lot in one step could be pushed out past the
  **opposite** edge (measured: 72 px off the left edge of a 250% panel). Placement now pulls the window
  back on screen once at the end, which is a no-op for every branch when the window fits (offset 0), so
  the original anchor semantics are unchanged.

### 4.4 Re-checking on real hardware: `/test-monitor-refit`

This machine only has one screen, so real mixed DPI cannot be reproduced here; that is why this switch
exists. It runs the same fit / clamp / place functions from production code once per screen and writes
the results to `<smokeDir>\monitor-refit.txt`.

```powershell
pwsh -NoProfile -Command {
  $env:QL_SMOKE_DIR = '<repo>\ql-smoke'
  Start-Process '<repo>\Build\Release\QuickLook-Next.exe' -ArgumentList '/test-monitor-refit'
  Start-Sleep 5
  Get-Content '<repo>\ql-smoke\monitor-refit.txt'
}
```

Output on this machine (single screen, 3072x1920 @200%):

```
monitors=1
primary=\\.\DISPLAY1

[0] \\.\DISPLAY1 primary=True
    boundsPx=(0,0,3072,1920) workPx=(0,0,3072,1920) scale=2 workDip=1536x960
    image 4289x631 @0.8 -> 1228.8x180.8 dip = 2458x362 px
    window 2600x1400 dip -> clamped 1536x960 dip = 3072x1920 px
    placement from (1843,960,768,384) -> (0,0) inside=True
```

On a mixed-DPI machine there will be one such record per screen, and there is exactly one thing to look
at: **`inside=True`** — plus the sizes being sensible on both the higher-scaling side (larger pixel size)
and the lower-scaling side (smaller DIP desktop).

### 4.5 What this machine **cannot** verify (recorded honestly)

There is only one screen here, so the **real** mixed-DPI effect cannot be reproduced locally. The evidence
above is "the math is correct + zero single-screen regression + unit tests for the placement rules +
per-screen diagnostics", not "we saw it fixed on a two-screen machine". To re-check on real hardware: run
`/test-monitor-refit` from §4.4 first and look for `inside=True`, then use the commands from §4.2 to
preview a landscape image, a portrait image and a PDF once each, and check whether
`px=W×H at=(x,y)` in `preview-rect.txt` falls entirely inside `monitorPx=(l,t,w,h)`.

## 5. Related work and follow-ups

- Upstream #827 was reported in 2021 and was still tagged "cannot be resolved before upgrading to .NET
  Framework 4.7.2" in 2025
  ([#1504](https://github.com/QL-Win/QuickLook/issues/1504)); this repo's placement code already queried
  per screen, and what this change adds is "both sides use the same reference + clamp on placement".
- Upstream [#1956](https://github.com/QL-Win/QuickLook/issues/1956) "Markdown renders incorrectly after
  changing screen scaling" shares its root with this document (content is not refitted after a DPI
  change); this change's `OnDpiChanged` hook leaves the entry point ready for it, but it was **not**
  verified against that issue, so it does not count as resolved yet.
- Still not done: **actively** recomputing the plugin content size when the user drags the preview window
  to another screen (today it only refits on open and on a DPI change).
