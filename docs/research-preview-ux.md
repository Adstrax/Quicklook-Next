# Preview experience: investigation and feasibility notes

> Status: **research and verification only — no product code changed, no release published.** Recorded 2026-09-17.
>
> Origin: a user asked "can the preview window remember its last size when previewing (video), or use a
> custom window size". After checking upstream we decided **not** to implement that request (see §3) and
> instead verified two more valuable things: **←/→ to switch files inside a folder** and
> **large-image preview protection**. This document records the feasibility work for those two, plus a
> scheduling reference drawn from upstream's popular issues.

## 1. Summary

| Item | Conclusion | Key evidence |
|---|---|---|
| ←/→ to switch to the previous/next file in the same folder | **Already implemented, nothing to build** (see §2.5; the "programmatically change the Explorer selection" approach described below is the rejected alternative) | `KeystrokeDispatcher` + `FocusMonitor` + `ViewWindowManager.SwitchPreview` |
| Large-image preview protection | **Necessary** | 64-megapixel PNG: first frame in 287 ms (speed is fine), but private memory peaks at **1.2–1.7 GB** (243 MB for an ordinary image), and 429 MB is still held after closing (baseline 203 MB) |
| Remembering the preview window size | **Not doing it** | Every comparable request upstream over 8 years was rejected (#169/#821/#492/#1196); a fixed size forces one aspect ratio onto all content → letterboxing / empty space |

## 2. ←/→ file navigation: feasibility

> **Correction (2026-09-19)**: this **feature has been implemented for a long time** — see §2.5. What
> follows is the feasibility work for an "alternative implementation" (making this app change the
> Explorer selection itself), and the conclusion is **do not adopt it**: the existing implementation is
> simpler and does not conflict with the plugins' use of the arrow keys.

### 2.5 The existing implementation (read this first; do not build it again)

Arrow-key file switching is an **existing** feature of this repo. The mechanism is "give the key to the
file manager, then follow the selection":

1. The global keyboard hook `GlobalKeyboardHook.HookProc` only intercepts when the key event is already
   `Handled` (`return kea.Handled ? 1 : CallNextHookEx(...)`); the arrow keys are **never swallowed**, so
   they reach the file manager as usual → Explorer moves the selection itself (the direction follows the
   view/sort order, just like macOS Quick Look);
2. On key-up, `KeystrokeDispatcher` sends `PipeMessages.Switch`; when `ViewWindowManager.SwitchPreview()`
   is called without a path it **re-reads the current selection** and previews that
   (`NativeMethods.QuickLookNext.GetCurrentSelection()`);
3. Separately, `FocusMonitor` also sends a `Switch` (with the new path) whenever the selection or focus
   changes, so the two paths cover for each other.

This also yields a ready-made "key ownership" rule: **while Explorer holds focus the arrow keys switch
files; once the preview window is clicked and takes focus the arrow keys belong to the plugin** (video
seek, PDF paging, ...) — the two never conflict because focus separates them naturally.

The "not done" conclusion in upstream issue
[#691](https://github.com/QL-Win/QuickLook/issues/691) is outdated information: the difficulty at the time
was "the preview window must not steal input", and the solution that arrived later is exactly this
"don't intercept the arrow keys + follow the selection" approach.

### 2.1 Measured output

```
PASS: IShellView -> IFolderView available
PASS: ItemCount = 18 (items can be enumerated in view order)
view order (first 8): America | Artificial | Beauty | CutePet | Economics | Japan | Jing | Lin
initial state: selection=(none), marked index=-1, focused index=0
target: item 1 = Artificial
(1) no activation + IFolderView::SelectItem: hr=0x00000000 -> selection=Artificial  PASS
(2) ACTIVATE_NOFOCUS(hr=0x00000000) + IFolderView::SelectItem: hr=0x00000000 -> selection=Artificial  PASS
(3) IShellView::SelectItem(pidl): hr=0x00000000 -> selection=Artificial  PASS
past the last item (index 18): hr=0x80004005 (no automatic wrap-around; wrapping is up to the app)
restored: selection=(none, same as the initial state)
```

（The probe only touches one already-open folder window, restores it immediately, and changes no files.）

### 2.2 Why this route works

- The app **already reads the selection** via
  `IShellBrowser::QueryActiveShellView → IShellView::GetItemObject(SVGIO_SELECTION)`
  (`QuickLookNext/NativeMethods/QuickLook.cs`); the only missing piece is querying one more interface,
  `IFolderView`, on the same `IShellView` — and the probe proves that step works
  (`PASS: IShellView -> IFolderView available`).
- `IFolderView::ItemCount(SVGIO_ALLVIEW)` + `Item(i)` return **Explorer's current display order**
  (including the user's sort settings), so "the next item" matches what the user sees on screen and we do
  not have to sort the directory ourselves.
- `IFolderView::SelectItem(index, SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED | SVSI_ENSUREVISIBLE)`
  takes effect **without the view being activated** (case (1) above is the successful run with no
  activation at all) — which is the decisive point for a preview tool that must not steal focus.
- The app is already **following Explorer's selection events** (see the CHANGELOG entry "follow the
  Explorer selection from events instead of a 500 ms poll"), so as soon as the selection changes the
  preview switches to the new file automatically.
- Going past the end returns `E_FAIL` and leaves the selection unchanged → wrap-around (last item →
  first item) would have to be implemented by the app itself.

### 2.3 Implementation sketch (about 4 places to change)

1. `QuickLookNext/NativeMethods/QuickLook.cs`: add an `IFolderView` interface declaration
   (`ItemCount` / `Item` / `GetFocusedItem` / `SelectItem`) and an internal helper
   `TryMoveSelection(int delta)` (find the current item → target item → handle wrap-around →
   `SelectItem`).
2. Global keyboard hook: intercept ←/→ when "a preview is open **and** the preview came from an Explorer
   selection", and let everything else through unchanged.
3. Target window: use the `IShellBrowser` of **the Explorer window that provided the current preview**
   (so multiple windows cannot make it move the wrong one).
4. Wrap-around and bounds: `index - 1 < 0 → count - 1`; `index + 1 >= count → 0`.

### 2.4 Open questions / risks

- **Multi-selection**: base it on `GetFocusedItem` (or the marked item) and only move the "current item"
  when several are selected.
- **Virtual folders** (search results, libraries): `Item(i)` order is the view order, so it works; path
  resolution should always go through `SHGetNameFromIDList`.
- **Key ownership**: when the preview window itself has keyboard focus, should ←/→ go to the plugin
  (video rewind/forward, image paging)? A priority has to be chosen. Suggested: preview window focused →
  plugin; focus elsewhere (the typical space-key preview case) → file navigation.
- **Source check**: a preview may have been opened from the command line or a pipe (not from an Explorer
  selection); that case must be skipped outright.

## 3. Why we are not implementing "remember the preview window size"

History of comparable requests upstream (QL-Win/QuickLook):

| issue | Request | Upstream answer |
|---|---|---|
| [#169](https://github.com/QL-Win/QuickLook/issues/169) (2018) | Remember window size and position | Original author xupefei: the size is **only kept while switching previews** and is restored on close, **"This is intended"**; when it was later requested as a persisted setting the maintainer replied that it is hard to satisfy every plugin at the same time |
| [#821](https://github.com/QL-Win/QuickLook/issues/821) | Remember sizes per file type | emako: **"No plan to support"** |
| [#492](https://github.com/QL-Win/QuickLook/issues/492) | Remember the last size | emako: **"No supported plans"** |
| [#1196](https://github.com/QL-Win/QuickLook/issues/1196) → #1078 → #608 | The same request repeated | Closed as duplicates all the way, eventually pointing back at #169 |
| [#525](https://github.com/QL-Win/QuickLook/issues/525) (opened by the original author) | The video window does not snap to the real video size | xupefei: **"the video plugin detects the real resolution and fits the window to it; when it cannot detect it, it falls back to the default size, which produces the odd borders"** |

The code agrees: upstream `QuickLook/ViewerWindow.xaml.cs` is exactly this repo's
`_customWindowSize` (**in memory only, valid for the session**), and upstream `OPTIONS.md` has **no window
size or position options at all**.

Conclusion: a preview window whose size is "determined by the content" is deliberate design. Pinning it
means forcing the same aspect ratio onto every file — video gets letterbox bars and images get centred
with empty space. **5.0.4 tried persisting it, and the result was exactly the "huge black bars and empty
space" users reported, so it was withdrawn the same day** (the release was deleted and the latest version
went back to 5.0.3; the commits on the branch still need reverting, see §5).

## 4. Large-image preview protection: the data

Measured on the same machine, with local builds:

| File | Request → content ready | Peak private memory | Peak working set | After closing the preview |
|---|---|---|---|---|
| `test.png` (ordinary image) | 108 ms | 243 MB | 335 MB | — |
| `big8000.png` (8000x8000, 64 megapixels) | 287 ms | **1,193–1,682 MB** | 1,657 MB | 429 MB (baseline 203 MB) |

Conclusions:

- **Speed is not the problem**: the first frame goes through the thumbnail path, and 64 megapixels still
  produces content in 287 ms;
- **memory is the risk**: 64 MP already costs 1.2–1.7 GB; one step up (panoramas/TIFFs beyond 100
  megapixels) on an 8 GB machine is the crash scenario from upstream
  [#1054 "Crash when previewing large images"](https://github.com/QL-Win/QuickLook/issues/1054);
- **closing does not return everything** (+226 MB), so the large-image path should reclaim memory
  explicitly when it closes;
- the hooks already exist: `MetaProvider.GetSize()` (reads the pixel count from the file header only;
  currently used to compute the window size), plus `AnimatedImage`'s thumbnail path and
  `DecodedImageCache`.

### Fix sketch

1. A pixel-count threshold (suggested default 40 MP, configurable): above it, **render only the
   thumbnail** and say so in the title/hint ("decode at the original size by switching to 1:1 (or
   zooming in)");
2. let the 1:1 / zoom-in actions trigger a full-resolution decode (hooked onto the existing `ZoomToFit` /
   `_zoomFactor` logic);
3. force a GC and clear the decode cache after closing a large-image preview (**large-image path only**,
   so ordinary images keep their fast first frame);
4. add the same guard rails to the ImageMagick path (TIFF/PSD/DICOM and friends).

### 5.0.5: what shipped, plus a re-measurement (2026-09-19)

The "decode pixel limit" is implemented (`DecodePixelLimit`, default 40 MP, plugin option
`MaxDecodePixels`, `0` = off; it covers both the WIC and the ImageMagick path), the title is annotated
with "preview scaled to the 40 MP limit", and closing a large-image preview reclaims memory explicitly.

A/B on the same 64-megapixel JPEG:

| Decode limit | Peak | Steady state |
|---|---|---|
| Off (previous behaviour) | 1,705 MB | 1,424 MB |
| **40 MP (new default)** | **1,425 MB** | **1,146 MB** |
| 8 MP (forced, for diagnosis only) | 1,038 MB | — |

**Conclusion: the guard rail only solves part of it (about -17%).** Pushing the limit down to 8 MP still
left a ~1 GB peak, which means the remaining cost is **unrelated to the decode size** and comes from the
display layer: `NativeProvider.GetThumbnail` scales the thumbnail into a `TransformedBitmap` in "original
image coordinates" (8000x8000), and WPF then lays out and renders inside that enormous coordinate space.
Really fixing it requires:

1. the thumbnail keeps its own pixel coordinate space (no upscaling to the original image's coordinates);
2. the panel's zoom/pan math switches from "original image pixel size" to "decoded pixel size + a scale
   factor";
3. higher resolutions are decoded on demand only when the user zooms past some threshold (say 100%), and
   under the same pixel limit.

That touches the panel's zoom/pan logic (a large regression surface), so it is being done as its own step.

### 5.0.6: the follow-up (same day, 2026-09-19)

An experiment confirmed the diagnosis above: removing only the "upscale the thumbnail to the original
image's coordinate space" step (nothing else) took the peak from 1,425 → **911 MB** (1,038 → 437 MB at the
8 MP limit). But dropping it outright meant **frames no longer fit the window after they loaded** (the
thumbnail geometry is not the frame geometry, so the panel does not recompute the fit; the measured
screenshot showed an over-zoomed, cropped state).

The final approach: **upscale the thumbnail to the decoded size of the rendered frame** (under the same
40 MP limit). Both then share one geometry → the fit is correct (verified with screenshots) and the peak
still comes down:

| Version | Peak | Steady state |
|---|---|---|
| Before 5.0.4 (no guard rail) | 1,705 MB | 1,424 MB |
| 5.0.5 (decode limit only) | 1,425 MB | 1,146 MB |
| **5.0.6 (coordinate space = decoded size)** | **1,156 MB** | — |

The zoom badge is converted the same way (`ZoomDisplayFactor = ZoomFactor × decoded size / real size`), so
100% still means "1:1 relative to the original image". At this point the "large-image protection" item can
be considered complete; the only further reduction left is "decode on demand when zooming in" (diminishing
returns).

## 5. Popular upstream issues (scheduling reference)

Filtered after sorting by open-issue comment count and total 👍 count ("where we stand" was verified
against the code):

| Upstream | Request | Upstream status | Our opportunity |
|---|---|---|---|
| [#691](https://github.com/QL-Win/QuickLook/issues/691) / [#1979](https://github.com/QL-Win/QuickLook/issues/1979) | In-app ←/→ file switching | old conclusion: "not done" | **We already have it**: don't intercept the arrow keys + follow the Explorer selection, see §2.5 |
| [#1054](https://github.com/QL-Win/QuickLook/issues/1054) | Large-image preview crashes | open, 11 comments | see §4 |
| [#827](https://github.com/QL-Win/QuickLook/issues/827) / [#1956](https://github.com/QL-Win/QuickLook/issues/1956) | Wrong sizes in mixed-DPI multi-monitor setups / Markdown renders wrong after a scaling change | open | refit on DPI changes + re-layout WebView2 |
| [#1608](https://github.com/QL-Win/QuickLook/issues/1608) | Extract text from images (OCR) | open, 10 comments | the built-in `Windows.Media.Ocr`; we already reference the WinRT projection, so zero new dependencies |
| [#1933](https://github.com/QL-Win/QuickLook/issues/1933) | Show/clear cache usage | open | we already have "open the data folder" and profile repair logic; adding a usage readout is enough |
| [#1571](https://github.com/QL-Win/QuickLook/issues/1571) | Native ARM64 support | open, 14 comments | `pack-release.ps1` already has `-Architecture arm64`, but Magick/LAV/pdfium/MediaInfo have no ARM64 native libraries |
| [#1844](https://github.com/QL-Win/QuickLook/issues/1844) / [#1528](https://github.com/QL-Win/QuickLook/issues/1528) / [#1768](https://github.com/QL-Win/QuickLook/issues/1768) / [#1968](https://github.com/QL-Win/QuickLook/issues/1968) | Lots of videos that will not open (upstream after 4.x moved to WPFMediaKit) | open | we use LAVFilters, which should be more stable; one regression pass over long videos / HEVC / MKV / network paths is enough to back that claim up |
| [#1987](https://github.com/QL-Win/QuickLook/issues/1987) | Remember window size/position | open | **not doing it**, see §3 |

Where we are **already ahead** of upstream (people still complain there, we have shipped it): auto-close
on focus loss (#484), startup and first-preview performance (upstream #1953 measures about 500 ms of
delay; ours is 96 ms to start and 100–350 ms to first preview), the plugin manager (#1916), and
binary/Hex viewing (#290).

## 6. To-do and handover

1. **Revert the 5.0.4 "remember window size" persistence** (keeping the genuine fixes it also carried:
   plugin-requested sizes no longer pollute the user's size, the `CanResize` gating for fixed-size
   previews, and the "reset window size" menu item) — this was the content of 5.0.5 and is **not shipped
   yet**;
2. the large-image guard rail (§4) — **done** (5.0.5 added the decode limit, 5.0.6 moved the coordinate
   space to the decoded size, taking the 64-megapixel peak from 1,705 → 1,156 MB);
3. ~~←/→ file navigation~~ — **the feature already exists**, nothing to build (see §2.5); this item has
   been removed from the plan;
4. later candidates: image OCR (#1608), cache usage and cleanup (#1933), ARM64 (#1571), video robustness
   regression.

## 7. How to reproduce

- Selection probe: `pwsh -NoProfile -File .\Scripts\probe-explorer-selection.ps1`
  (picks the first folder window for the check and restores it immediately)
- Large-image measurement: `pwsh -NoProfile -File .\Scripts\measure-preview.ps1 -Files <large image>
  -StartupWaitMs 1500` (duration); memory peaks come from sampling the process's `PrivateMemorySize64`
  externally
