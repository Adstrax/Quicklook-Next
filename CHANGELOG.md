# QuickLook-Next Changelog

> The English changelog.
>
> - **5.x** — this fork's current line — is described in full below.
> - **4.x** notes are kept verbatim from upstream QuickLook, which this fork is based on.
> - **3.x and earlier** get one line per release here (closely related releases share a line); their
>   detailed notes are kept in [CHANGELOG.zh-CN.md](CHANGELOG.zh-CN.md).

## QuickLook-Next 5.6.0

### The plugin panel can browse and install third-party plugins now

- The plugin manager has a second view. **Browse** lists the plugins the upstream wiki points at - 43
  of them today - with their publisher, version, size and description, and installs one with a single
  click. No more hunting for a `.qlplugin` in a browser and previewing the file to install it.
- Nothing is mirrored here: every row points at the file its own author publishes. The catalogue is
  one 30 KB JSON file in this repository (`plugins/index.json`), regenerated with
  `Scripts/build-plugin-index.ps1` from the wiki plus the GitHub API.
- **Every entry is pinned to exact bytes.** The catalogue records the size and SHA-256 of the release
  asset; the download is checked against both before a single file is written, and a mismatch aborts
  the install and deletes the download. An entry is dropped unless its URL is https on a GitHub host,
  its size is plausible and its hash is complete. GitHub only reports a digest for assets uploaded
  after it started doing so, so the generator downloads and hashes the older ones itself - 15 of the
  43 needed that.
- A package only ever installs into the folder its own metadata names, and that name has to be a
  `QuickLook.Plugin.*` namespace, so a package cannot pick its own destination. Upgrading over a
  plugin the app has loaded parks the old folder as `*.uninstalled`, exactly like an uninstall does.
- The confirmation shows the publisher, the source repository and the size before anything is
  downloaded, and offers a restart when the install finishes.
- Hidden switch `/test-plugin-catalog` writes what the live list returns
  (`<smokeDir>\plugin-catalog.txt`), and nine new unit tests cover the trust boundary: which entries
  are allowed through and which folder an install may target.

## QuickLook-Next 5.5.1

### English-first repository, and English where a translation is missing

- The repository reads English now, not just the README: this changelog, the three research notes
  under `docs/`, every script (`test.ps1`, `bench.ps1`, `Scripts/*`) and the comments that were still
  Chinese in the C#/XAML sources. The Chinese changelog and README live on as
  `CHANGELOG.zh-CN.md` and `README.zh-CN.md`, and the Chinese screenshots moved to
  `docs/screenshots/zh-CN/`.
- The same problem existed in strings users actually see: the update dialog, the download progress
  panel, the "WebView2 failed to initialize" notice, the "this document cannot be read" pages
  (Office and Markdown), the DbViewer password prompt and the font previewer all carried Chinese
  **failsafe** text. When a translation key was missing, an English user got Chinese - the update
  path was exactly the bug reported against 5.0.1. Those fallbacks now mirror the `en` block of
  `Translations.config`.
- Deliberately still Chinese: the language packs themselves (`Translations.config`, so the Chinese UI
  keeps working), the usage note packaged as `Readme.txt`, and the OCR test data (it exists to test
  Han word joining and full-width punctuation).
- No behaviour change elsewhere: build is clean (0 warnings, 0 errors) and the unit tests are 94/94.

## QuickLook-Next 5.5.0

### Mixed-DPI placement is testable now, not just "trusted"

- The 9/10 placement rule moved out of `ViewerWindow` into the pure helper
  `QuickLookNext/Helpers/WindowPlacement`, so mixed-DPI geometry can be unit tested without a second
  screen. Four new tests use the geometry from the #827 report (4K at 250% + 1080p at 100%),
  including "clamp the size to the screen, then place it".
- A real edge case came out of those tests: the right/bottom anchors position the window from the
  *old* edge, so a window that grows a lot was pushed off the opposite side (measured: 72 px off the
  left edge on a 250% panel). Placement now pulls the result back inside once more; it is a no-op
  whenever the window fits.
- New hidden switch `/test-monitor-refit`: runs the same sizing and placement functions once per
  screen and writes `monitor-refit.txt` (pixel bounds and work area, scale, DIP size, a fitted
  sample image, a clamped oversized window, and the resulting position with `inside=true/false`).
- The package usage note is `Readme.txt` now (Latin name like every other file in the archive).
- Unit tests 94/94; the single-screen baseline is unchanged (6000×4000 image still lands at
  `2308×1540 @(382,190)`), so the refactor is behaviour-preserving.

## QuickLook-Next 5.4.0

### Plugin manager: search box

- Opens with the caret in the search box; typing filters name and description, the status line
  reports "3 of 25 shown", and Esc clears the filter first and closes the panel second.
- Tab cycles inside the panel, which is a visible keyboard focus ring now.

### Fixed: the top bar blinked while the pointer rested on it

- The show animation used to start a "hide after 1 s" when it finished while a 100 ms poll re-showed
  the bar, so a parked pointer made it pulse; measured with the diagnostic log: one show and zero
  hides over 6 s, then one hide after the pointer left. The rule lives in `Helpers/TopBarVisibility`.

### Accessibility

- Reduced motion is honoured (`SystemParameters.ClientAreaAnimation` gates the caption fade, the
  content fade and the window show transition; the busy spinner is information and stays).
- Icon-only caption buttons carry an `AutomationName` from their tooltip; panel buttons get one from
  their label because a custom template hides the content text from assistive tech.

### Housekeeping

- The four hand-drawn panels (update prompt, download panel, data & cache, OCR) share one button
  template in `Helpers/PanelStyles` instead of four copies with three different hover treatments.
- Unit tests 90/90.

## QuickLook-Next 5.3.0

### Readability on any wallpaper

- The tray menu keeps its 30% WCA acrylic; the surfaces that are *read* (plugin manager, update
  prompt, download panel, data & cache, OCR) use a 45% panel tint plus a content plate, and the
  secondary text colour was raised because it used to assume a solid background.
- When Windows' transparency effects are off these panels fall back to a solid surface instead of
  becoming a 30-45% tint with no blur.

### Preview caption bar

- A theme-aware gradient scrim behind the bar (the image viewer switches the glass off, so white
  icons used to float on the picture), single-glyph toggles whose active state is the accent colour,
  a hairline between file actions and window actions, and a title with a secondary prefix.

### Plugin manager and tray menu

- A glyph per plugin family instead of 25 identical puzzle pieces; the per-row "Built-in" badge is
  gone (the header counts them); plugins without a version no longer print `0.0.0.0`; the list sits
  on a content plate.
- The tray menu icon column is a fixed 16 px grid, and the version at the top is no longer dimmed.
- Unit tests 87/87.

## QuickLook-Next 5.2.0

### Low memory mode (tray menu → Options)

- The two startup warm-ups (the off-screen preview window and the per-family preparation) are now a
  switch. Measured idle memory: 64 MB with both off, 112-118 MB with the window only, 169-179 MB by
  default; the warm-ups buy ~200 ms on the first preview and 100-200 ms per family.
- `WarmUpPreviewFamilies` and `WarmUpFamilyCount` remain available for manual tuning, documented in
  `OPTIONS.md` with the measurements.

### Fixed: the preview did not re-measure its content after the window changed screens

- The plugin's own "fit to the screen" question is asked again whenever the window lands on another
  monitor or the resolution/scaling changes (`ContextObject.RefitToHostDesktop`), then clamped to
  that screen; a size the user dragged wins, and fixed-size plugins are unaffected.
- WebView2: a Chromium control keeps the scaling it was created with, so a scaling change now
  discards parked controls and the visible panel rebuilds (upstream #1956).

### Fixed: circled numbers are no longer reported as wrong digits

- The engine cannot read ①-⑤ or ❶-❺ at all (measured), but it used to report a filled ① as `0`; a
  lone `0`/`O`/`o` in a disc-shaped box is now dropped as an unreadable list marker. `/test-ocr`
  reports a `filtered=` count.
- Unit tests 87/87.

## QuickLook-Next 5.1.0

### OCR: mixed-language images no longer lose whole lines

- Lines are merged per engine: candidates are grouped by vertical position and each group keeps the
  text that scored best, so a bilingual page keeps both languages (measured: 2 lines/67 characters
  before, 3 lines/89 characters after).

### OCR: small images are enlarged before recognition

- A picture whose longest side is under 1000 px is enlarged 2× first (a 14 px Chinese line went from
  `138 佣佣 1 1 1 1` to the correct `13800001111`); larger pictures are untouched and recognition
  stays at ~1.1 s.

### Fixed

- A preview request from Explorer could be dropped silently when it hit the gap between two pipe
  connections; it retries four times (~3 s) before falling back to a message box.
- The preview warm-up no longer writes its diagnostic file on every start (same always-true guard as
  the update prompt below).

### Improved

- The diagnostic log rotates at 1 MB (one previous file is kept) and the data & cache panel can
  clear it.
- Unit tests 78/78.

## QuickLook-Next 5.0.11

### Fixed: Chinese images came out as gibberish — OCR picks the right engine now

- Every installed engine is tried and the answer whose characters belong to that language's script
  wins (own script +10, foreign -1, digits and punctuation neutral), so a Chinese page is no longer
  handed to the English engine (measured: 33 characters of nonsense before, 257 correct Han
  characters after).
- Chinese is no longer joined with a space per glyph, so copying gives "不要去回应负能量" instead
  of "不 要 去 回 应".

### Fixed: the update prompt closed itself after a few seconds

- The self-answering test hook decided "am I being tested?" from "does a test directory exist?",
  and that value is always true, so the shipping prompt was closed by its own timer. The hook is
  driven by its dedicated switch now, and the startup diagnostic reports whether it is armed.

### Improved

- "Extract text" moved from the More menu onto the toolbar (image previews only).
- Unit tests 69/69.

## QuickLook-Next 5.0.10

### Fixed: the preview window spanned several monitors (upstream #827)

- The size a plugin asked for was measured against the monitor of the foreground window while the
  viewer placed the window on the monitor it was on, so a size measured on one screen was scaled
  into pixels on another and overflowed.
- The host now hands the plugin the target screen before `Prepare`, clamps every plugin-derived
  size to the screen the window is about to be placed on (a size the user dragged is left alone),
  and re-fits the window on a DPI change. The arithmetic lives in `PreviewWindowSizing`.
- Unit tests 60/60; the single-screen result is unchanged (`2308×1540 @(382,190)` for a 6000×4000
  image), and `/test-preview-diag` records every placement.

## QuickLook-Next 5.0.9

### Fixed: an unplayable video crashed the whole app

- `MediaFailed` is raised on the player's own thread and touched the visual tree directly, so a
  damaged file, a 0-byte file or a container without a decoder took the process down; all UI work
  now runs on the window's dispatcher and the user gets "This video could not be played" (upstream
  #1768).

### Video robustness sweep

- A 22-sample matrix (containers, codecs, 4K, long, portrait, rotated, VFR, no audio, audio-only,
  non-ASCII paths, truncated, 0-byte) run end to end: 22/22 render, no crashes, the two deliberately
  broken files fail gracefully and log. Tools ship with the repo (`Scripts/make-video-matrix.ps1`,
  `Scripts/run-video-matrix.ps1`) and the record is in `docs/research-video-matrix.md`.

## QuickLook-Next 5.0.8

### Text recognition for images (OCR, upstream #1608)

- "Extract text (OCR)" in the preview's More menu uses the OCR engine Windows already ships
  (`Windows.Media.Ocr`) and shows the result in a selectable, one-click-copy panel.
- Lines are preserved (the engine's `Text` joins everything with spaces), decoding is bounded
  (10000 px per side, 16 MP total, EXIF orientation respected), and the feature lives in the app
  because the image plugin does not carry the WinRT projection.
- `/test-ocr` with `QL_TEST_OCR_FILE` writes the result to `<smokeDir>\ocr.txt`.
- Unit tests 48/48.

## QuickLook-Next 5.0.7

### Data & cache: see the footprint, clear the rebuildable caches (upstream #1933)

- The tray menu's "Data & cache…" shows the WebView2 caches, the leftover update files, settings,
  logs and the WebView2 profile side by side with a total.
- "Clear cache" deletes only the whitelist (shader/web caches, update leftovers); sign-in data,
  settings, statistics and logs survive, files in use are reported instead of failing.
- Unit tests 44/44.

## QuickLook-Next 5.0.6

### Huge-image protection, part two: the coordinate space follows the decoded size

- Zoom, the badge and the "100%" reset are computed in the space the image was actually decoded in,
  not the original pixel size (peak private bytes for a 64 MP image: 1705 → 1156 MB).

## QuickLook-Next 5.0.5

### Reverted: the preview window no longer remembers its size across runs

- Remembering it forced one aspect ratio onto every later file and produced letterboxing or empty
  space; the size is back to "remembered for this session only".

### Huge-image protection (upstream #1054)

- Decoding is capped at 40 MP by default (configurable per plugin as `MaxDecodePixels`), which takes
  the private-bytes peak for a 64 MP image from 1.2-1.7 GB down to about 1.2 GB.

## QuickLook-Next 5.0.4

### Preview window remembered its size (withdrawn)

- This release remembered the preview window size across runs (including video previews). It was
  withdrawn because it made every later preview inherit that size and aspect ratio.

## QuickLook-Next 5.0.3

### Menu surfaces settled at 30% opacity

- The accent tint went to 30% with no extra brush coverage. Measured surface luminance over a bright
  wallpaper: 10% → 113, 20% → 104, 30% → 96, 40% → 87, 60% → 70, 90% → ~52; 30% keeps the wallpaper
  readable while the menu still reads as a surface.

## QuickLook-Next 5.0.2

### Menu surfaces back to WCA acrylic

- The Windows 11 host backdrop of 5.0.0/5.0.1 read as a fully transparent pane on screen, so the
  menu-like surfaces (tray menu, plugin manager, update prompts) went back to
  `ACCENT_ENABLE_ACRYLICBLURBEHIND` with the app's own tint.

## QuickLook-Next 5.0.1

### Update dialog follows the UI language, sizes are formatted

- The new `Update_*` strings were only added to zh-CN/zh-TW, so English UIs fell back to the Chinese
  failsafe; the whole dialog is translated now.
- Download sizes are formatted ("12.3 MB") instead of printing the raw double
  ("60.12675467123377 MB"), and small values stay in KB so the first moments of a download still
  show movement.

## QuickLook-Next 5.0.0

### Menu surfaces moved to the Windows 11 host backdrop

- The tray menu, plugin manager and update dialogs switched to `ACCENT_ENABLE_HOSTBACKDROP` with a
  lighter tint (later reverted in 5.0.2/5.0.3), and the acrylic material was reworked around it.


## 4.6.0

- Add `.resources` (.NET binary resources) support to TextViewer
- Add Greenfish Icon Editor Pro document support (`.gfie`, `.gfi`) to image viewer
- Add Excalidraw support to image viewer [#1955](https://github.com/QL-Win/QuickLook/issues/1955)
- Add binary viewer plugin [#290](https://github.com/QL-Win/QuickLook/issues/290)
- Add database viewer plugin for [LiteDB](https://github.com/litedb-org/LiteDB) v5, SQLite and encrypted SQLite support
- Add prefetch viewer plugin for `.pf` file support
- Add FilePilot preview integration by @Andrey Semjonov [#1949](https://github.com/QL-Win/QuickLook/issues/1949)
- Add AsciiDoc support to markdown viewer (`.adoc`, `.asciidoc`, `.asc`, `.ad`)
- Add Jupyter Notebook support to markdown viewer (`.ipynb`)
- Add reStructuredText support to markdown viewer (`.rst`, `.restructuredtext`)
- Add configurable keyboard shortcuts to toggle TOC visibility for markdown viewer [#1934](https://github.com/QL-Win/QuickLook/issues/1934)
- Add option to disable window show transitions `<ShowWindowTransition>False</ShowWindowTransition>`
- Add `.winmd` to PE viewer supported extensions
- Add HTTP (`.http` and `.rest`) syntax highlighting
- Add `.pyi` extension to Python syntax highlighting
- Add RViz `.rviz` in YAML highlighting extensions
- Add COM wrappers and out-of-proc preview host [#1929](https://github.com/QL-Win/QuickLook/issues/1929)
- Add `.vdproj` highlighting definitions support
- Add versioned `.so` files support in ELF viewer
- Prepare building for ARM64 [#1872](https://github.com/QL-Win/QuickLook/issues/1872) but NOT READY
- Improve acrylic tint opacity and color values [#1912](https://github.com/QL-Win/QuickLook/issues/1912)
- Add magic number checks to support image files without an extension [#1868](https://github.com/QL-Win/QuickLook/pull/1868)
- Add option to close preview when losing focus [#484](https://github.com/QL-Win/QuickLook/issues/484) (Experimental)
- Add auto-terminate QuickLook-Next.exe before install/upgrade/uninstall
- Add `.csv`, `.tsv` and `.psv` rainbow highlighters support
- Add `.jsonc` extension to JSON syntax highlighters support
- Add `.hxx` extension to C++ syntax definitions
- Add `.sc` extension to Scala syntax definitions
- Add `.csh`, `.fish`, `.nu` extensions to ShellScript syntax
- Add `.es6` and `.pac` extensions to JS syntax
- Add `.jav` extension to Java syntax
- Add Clip Studio Paint file .clip support [#1937](https://github.com/QL-Win/QuickLook/issues/1937)
- Add F5 shortcut key for Reload [#1922](https://github.com/QL-Win/QuickLook/issues/1922)
- Add `FocusWindowOnOpen` option for window focus on open [#1695](https://github.com/QL-Win/QuickLook/issues/1695)
- Improve markdown to support frontmatter (YAML Metadata) [#1920](https://github.com/QL-Win/QuickLook/issues/1920)
- Improve tray initialization of ContextMenu
- Add `.msp` installer support for app viewer
- Add `.m3u` and `.m3u8` highlighting definitions support
- Add ShellScript syntax extensions for `.bashrc`, `.bash_profile`, `.bash_login`, `.profile`, `.bash_logout`, `.zshrc`, `.zprofile`, `.zlogin`, `.zlogout`, `.dashrc`, `.kshrc`, `.mkshrc`, `.ashrc` and `.shrc`
- Add IDMan (Internet Download Manager) support instead of [QuickLook.Plugin.IDManViewer](https://github.com/emako/QuickLook.Plugin.IDManViewer)
- Support search panel in CSV viewer [#1824](https://github.com/QL-Win/QuickLook/issues/1824)
- Add ShellScriptDetector and register in FormatDetector
- Add Graphviz (`.gv` and `.dot`) support for image viewer
- Add draw.io (`.drawio` and `.dio`) support for image viewer
- Add Clip Studio Paint (`.clip`) support for image viewer
- Support localization for CSV viewer search panel
- Add extension filter helper and integrate checks
- Improve extension parsing and add balcklist for .insv [#1802](https://github.com/QL-Win/QuickLook/issues/1802)
- Add icon font preview mode to FontViewer
- Add `.jsonld` to JSON syntax extensions
- Use `.invalid` domain to speed up CHM page loading
- Support DeskBox 3rd-party program
- Improve update notification message [#1961](https://github.com/QL-Win/QuickLook/issues/1961)
- Upgrade MSVC PlatformToolset to v145
- Update dependencies and add System.IO.Compression
- Add recycle bin images and use embedded icons on Win10+
- Disambiguate `.pl` files with Prolog/Perl detectors
- Add Perl syntax highlighting support
- Add Objective-C++ syntax highlighting (Dark/Light)
- Add `.phtml` and `.ctp` to PHP syntax extensions
- Include `.ndjson` in JSONL highlighting extensions
- Add `.dsql` extension to SQL syntax
- Add NuGet (`.nupkg` and `.snupkg`) support for AppViewer
- Add setting to auto-unblock Protected View [#1832](https://github.com/QL-Win/QuickLook/issues/1832)
- Add built-in plugin support for `.chm`
- Add PlantUML preview support for image viewer
- Add `.pyz` to supported archive extensions
- Add [ISON](https://github.com/ISON-format/ison) syntax definitions support for `.ison`
- Add `.DS_Store` and `Thumbs.db` support for archive viewer
- Add options of the font and font size used in the text viewer [#1930](https://github.com/QL-Win/QuickLook/issues/1930)
- Add Inno Setup syntax definitions support for `.iss` and `.isl`
- Add built-in dump plugin for minidumps (`.dmp`, `.dump`, `.mdmp`, `.hdmp` and `.minidump`)
- Support HW/SW (hardware acceleration) decoding toggle to video viewer [#1928](https://github.com/QL-Win/QuickLook/issues/1928)
- Support localization for video viewer
- Add `.psd1` and `.psm1` to PowerShell syntax extensions
- Add Roff (`.ms`, `.man`, `.roff`, `.tmac`, `.me` and `.troff`) syntax highlighting support
- Add `.gitconfig` to INI syntax extensions
- Add Graphviz (`.dot` and `.gv`) syntax definitions
- Improve JSON syntax highlighting color
- Fix oftentimes doesn't trigger [#1903](https://github.com/QL-Win/QuickLook/issues/1903) [#1483](https://github.com/QL-Win/QuickLook/issues/1483)
- Fix CSV auto-scroll bug on large files with virtualization enabled
- Fix fullscreen behavior for window dragging and window corners for Windows 11
- Fix the busy decorator foreground color error in dark mode
- Fix rendering lag caused by excessively long lines by truncating and sanitizing them in text viewer
- Fix twice space after Alt+Tab to explorer window [#1939](https://github.com/QL-Win/QuickLook/issues/1939)
- Fix FontViewer preview width sizing

## 4.5.0

- Update LAVFilters to `0.81.0` [#1362](https://github.com/QL-Win/QuickLook/issues/1362) [#1855](https://github.com/QL-Win/QuickLook/issues/1855) [#1863](https://github.com/QL-Win/QuickLook/issues/1863)
  > A possible side effect is that users with older GPUs or without the latest VC++ Redistributable installed may experience video playback failures.
  >
  > Nevertheless, QuickLookNext has chosen to continue with an up-to-date update strategy.
  >
  > If you encounter any issues, you can refer to [#1362](https://github.com/QL-Win/QuickLook/issues/1362) and consider downgrading your LAVFilters version.

- Improve LRC handling by merging duplicate timestamps [#1858](https://github.com/QL-Win/QuickLook/issues/1858)
- Improve the translation of Simplified Chinese by [@stxttkx](https://github.com/stxttkx)
- Add support for the `WindowBackdrop` option (Auto/None/Mica/Acrylic/Tabbed/Acrylic10/Acrylic11)
- Add PKCS7 extensions to supported file types (`.p7s` and `.pkcs7`)
- Add support Fortran95 (`.f90`, `.f95`, `.f03`), GDScript (`.gd`), Diff (`.patch`, `.rej`), Razor (`.cshtml`, `.razor`), ActionScript (`.as`, `.mx`), Assembly (`.asm`), Ada (`.ada`, `.ads`, `.adb`), AutoHotkey (`.ahk`), Rhai (`.rhai`), C++ ( `.cu`, `.cuh`, `.hip`), Python (`.pyx`), PlantUML (`.puml`, `.plantuml`, `.pu`, `.uml`, `.iuml`, `.wsd`), Zig (`.zig`), Moji (`.moji`), GraphQL (`.graphql`, `.gql`, `.gqls`), Mermaid (`.mmd`, `.mermaid`), KQL (`.kql`), PromQL (`.promql`), JSON Lines (`.jsonl`), ANTLR, Boo, Ceylon, ChucK, Clojure, Cocoa, CoffeeScript, Cool, and others syntax highlighting, including the dark mode theme
- Add support Chromium `.pak` viewer and file extraction
- Add `.axml` extension to XML syntax highlighting
- Add `.cursorignore` extension to GitIgnore syntax
- Add support for Python `.whl` and `.egg` archives
- Add support `.psv` parsing in CsvViewer
- Add Mermaid (`.mermaid`) support and `.mmd` detection [#1893](https://github.com/QL-Win/QuickLook/issues/1893)
- Add plugin icon registration and include `QLPlugin.ico` designed by [@Shomnipotence](https://github.com/Shomnipotence)
- Add DICOM image support to ImageViewer plugin [#1866](https://github.com/QL-Win/QuickLook/issues/1866) `This is not a long-lasting built-in plugin`
- Add Romanian translation by [@Laszlo19](https://github.com/Laszlo19)
- Add F11 full screen toggle support [#253](https://github.com/QL-Win/QuickLook/issues/253)
- Fix markdown not supporting absolute resource paths
- Fix option `<UseTransparency>False</UseTransparency>` not taking effect in Windows 10 [#1542](https://github.com/QL-Win/QuickLook/issues/1542)
- Fix loop toggle resuming paused video playback [#1852](https://github.com/QL-Win/QuickLook/issues/1852)
- Fix unhandled Exception with XLSX and CSV files in OfficeViewer resize with `RPC_E_CANTCALLOUT_ININPUTSYNCCALL` [#1854](https://github.com/QL-Win/QuickLook/issues/1854)
- Fix command line relative path resolution [#1857](https://github.com/QL-Win/QuickLook/issues/1857)
- Fix taskbar icon intermittently missing after Explorer restart [#1864](https://github.com/QL-Win/QuickLook/issues/1864)
- Fix `{Desktop composition is disabled}` exceptions in `GetMonitorColorProfileFromWindow` [#4](https://github.com/QL-Win/QuickLook.Common/pull/4)
- Fix `assimp.dll` not found for win-x64 [#1741](https://github.com/QL-Win/QuickLook/issues/1741)

## 4.4.0

- Add support for `.cnf` files to INI syntax highlighting
- Add support for `.ddeb` Debian debug symbol packages
- Add a **Reload** option to the **More** context menu [#1839](https://github.com/QL-Win/QuickLook/issues/1839)
- Add support for embedded lyrics in music file [#1847](https://github.com/QL-Win/QuickLook/issues/1847)
- Add a certificate viewer plugin to support extensions `.p12`, `.pfx`, `.cer`, `.crt`, `.pem`, `.mobileprovision` and `.certSigningRequest` 
- Add support for Compound File Binary formats (`.cfb` and `.eif` is now supported)
- Add a restart button after plugin installation [#1823](https://github.com/QL-Win/QuickLook/issues/1823)
- Improve XML version attribute detection in `XMLDetector` (e.g. `<?xml version='1.0'?>` is now supported)
- Improve YAML highlighting and support `.clang-format`
- Fix a crash that could occur when shutting down or restarting Windows [#1782](https://github.com/QL-Win/QuickLook/issues/1782)
- Fix JSON detection with UTF-8 BOM present
- Fix tags not displayed due to empty cover art [#1845](https://github.com/QL-Win/QuickLook/issues/1845)

## 4.3.0

- Add Svelte syntax highlighting support
- Add ShowInTaskbar setting to display window in taskbar [#1789](https://github.com/QL-Win/QuickLook/issues/1789)
- Add option to disable automatic update check at startup [#1801](https://github.com/QL-Win/QuickLook/issues/1801)
- Update PowerShell syntax colors in dark theme
- Improve TextViewerPanel UI and usability
- Fix DOpus crash when QuickLookNext runs with different privilege level [#1781](https://github.com/QL-Win/QuickLook/issues/1781)
- Fix volume control exceeding limits during mouse wheel scroll [#1813](https://github.com/QL-Win/QuickLook/issues/1813)
- Fix error in RTF file originating from version 4.2.1 [#1826](https://github.com/QL-Win/QuickLook/issues/1826)

## 4.2.2

- Fix version display issue [#1776](https://github.com/QL-Win/QuickLook/issues/1776)

## 4.2.1

- Fix theme error in MediaInfoViewer plugin [#1775](https://github.com/QL-Win/QuickLook/issues/1775)
- Fix theme error in any theme-changable plugin [#1507](https://github.com/QL-Win/QuickLook/issues/1507)

## 4.2.0

- Add built-in MediaInfoViewer plugin and support it in more menu
- Add 'Copy as path' option to more menu
- Add cross-plugin 'Reopen as' menu for SVG and HTML [#1690](https://github.com/QL-Win/QuickLook/issues/1690)
- Support Point Cloud Data (.pcd) for 3D spatial (Only PCD files with the PointXYZ format are supported, while Color and Intensity formats are not.)
- Support Mermaid diagram rendering in MarkdownViewer [#1730](https://github.com/QL-Win/QuickLook/issues/1730)
- Support .pdn in ThumbnailViewer [#1708](https://github.com/QL-Win/QuickLook/issues/1708)
- Improve CLI performance [#1706](https://github.com/QL-Win/QuickLook/issues/1706) [#1731](https://github.com/QL-Win/QuickLook/issues/1731)
- Set default background to transparent for SVG panel
- Improve UI/UX of font loading
- Add diff file syntax highlighting
- Add Swedish translation [#1755](https://github.com/QL-Win/QuickLook/issues/1755)
- Add .slnx extension to XML syntax highlighting
- Add support for Telegram Sticker (.tgs) files [#1762](https://github.com/QL-Win/QuickLook/issues/1762)
- Add .snupkg and .asar support to archive viewer
- Add .krc file support to TextViewer
- Add UseNativeProvider option [#1726](https://github.com/QL-Win/QuickLook/issues/1726)
- Fix image .jxr error reading from UseColorProfile
- Fix issue where font file stays locked [#77](https://github.com/QL-Win/QuickLook/issues/77)
- Fix font file unicode name is not supported
- Fix extracting cover art will not cause the title to be lost [#1759](https://github.com/QL-Win/QuickLook/issues/1759)
- Fix HelixViewer default height being too large
- Fix long path handling issue in HtmlViewer [#1643](https://github.com/QL-Win/QuickLook/issues/1643)
- Update Batch syntax highlighting colors
- Refactor tray icon to use TrayIconHost
- Refactor to make exe-installer no forked relaunching
- Remove unimportant UnobservedTaskException [#1691](https://github.com/QL-Win/QuickLook/issues/1691)
- Remove configuration `ModernMessageBox`

## 4.1.1

- Add built-in ThumbnailViewer plugin [#1662](https://github.com/QL-Win/QuickLook/issues/1662)
- Add built-in HelixViewer for 3d models [#1662](https://github.com/QL-Win/QuickLook/issues/1662)
- Add FBX model support using AssimpNet [#1479](https://github.com/QL-Win/QuickLook/issues/1479)
- Add `SVGA` and `Lottie Files` animation preview support
- Add MathJax inline math support to Markdown [#1640](https://github.com/QL-Win/QuickLook/issues/1640)
- Add `SubRip Subtitle (.srt) files`, `Protobuf`, `NSIS`, `.gitmodules`, `.dotsettings`, `.gitignore`, `.gitattributes`, `Markdown`, `reStructuredText`, `simple QML syntax`, `.env`, `Configuration (.conf;.config;.cfg)` highlighting [#1002](https://github.com/QL-Win/QuickLook/issues/1002)
- Add dark mode highlighting for `PowerShell`, `Registry`, `C`, `C++`, `Java`, `Rust`, `SQL`, `Ruby`, `R`, `PHP`, `Pascal`, `Objective-C`, `Lisp`, `Kotlin`, `Erlang`, `Dart`, `Swift`, `VisualSolution`, `CMake`
- Add `MakefileDetector`, `CMakeListsDetector for CMakeLists.txt`, `DockerfileDetector`, `HostsDetector for hosts` for text viewer
- Improve QuickLookNext initialization speed
- Optimize JSONDetector with Span
- Set RichTextBox background to transparent
- Revert Add Sandbox detection from 4.1.0 which will call crash

## 4.1.0

- Add built-in AppViewer plugin for `.msi`, `.appx`, `.msix`, `.wgt`, `.wgtu`, `.apk`, `.ipa`, `.hap`, `.deb`, `.dmg`, `.appimage`, `.rpm`, `.aab`
- Add built-in ELF viewer plugin for ELF-type files
- Add reload feature by JSuttHoops but you should enable `AutoReload` option firstly
- New option ProcessRenderMode
- Use format detector feature for TextViewer, only `JSON` / `XML` available now
- Add support more highlighting for `HLSL`, `XML`, `TXT`, `Properties`, `Lyric`, `Log`, `Python`, `JavaScript`, `Vue`, `CSS`, `Go`, `YAML`, `F#`, `INI`, `TypeScript`, `VB`, `SubStation Alpha` and `Lua`
- No markdown resource extraction [#1661](https://github.com/QL-Win/QuickLook/issues/1661) [#1670](https://github.com/QL-Win/QuickLook/issues/1670)
- Support X11 and more JPEG2000 image formats
- Support JXR image but SDR only [#1680](https://github.com/QL-Win/QuickLook/issues/1680)
- Enable window dragging in video viewer panel [#425](https://github.com/QL-Win/QuickLook/issues/425)
- Add SVG support using WebView2 in ImageViewer
- Support RTL for .txt file [#1612](https://github.com/QL-Win/QuickLook/issues/1612)
- Add `Alt+Z` shortcut to toggle word wrap [#1487](https://github.com/QL-Win/QuickLook/issues/1487)
- Improve startup speed [#1521](https://github.com/QL-Win/QuickLook/issues/1521)
- Improve PDF magic detection
- Improve GroupBox UI/UX
- Attempt to fix the crash [#1648](https://github.com/QL-Win/QuickLook/issues/1648) `This is an experimental fix, the idea is to remove the tree to prevent the DUCE command`
- Update font pangram for FontViewer
- Update de translations by King3R
- Manually resolve the assembly fails [#1618](https://github.com/QL-Win/QuickLook/issues/1618)
- Merge OfficeViewer-Native plugin [#1662](https://github.com/QL-Win/QuickLook/issues/1662)
- New option CheckPreviewHandler for OfficeViewer-Native
- Add Sandbox detection
- Revert the DataGrid style of CSV [#1664](https://github.com/QL-Win/QuickLook/issues/1664)
- Remove the WoW64HookHelper from release [#1634](https://github.com/QL-Win/QuickLook/issues/1634)
- Fix share button was not visible in win11
- Fix generic theme resources [#1652](https://github.com/QL-Win/QuickLook/issues/1652)
- Fix old version volume exception [#1653](https://github.com/QL-Win/QuickLook/issues/1653)
- Fix CaptionTextButtonStyle not static anymore
- Fix unsupported ColorContexts in Windows [#1671](https://github.com/QL-Win/QuickLook/issues/1671)
- ~~Fix long path issue [#1643](https://github.com/QL-Win/QuickLook/issues/1643)~~

## 4.0.2

- Support .pcx image [#1638](https://github.com/QL-Win/QuickLook/issues/1638)
- Improve PE parsing with extended buffer size
- Fix flickering [#1628](https://github.com/QL-Win/QuickLook/issues/1628)
- Fix DpiAwareness for PerMonitor [#1626](https://github.com/QL-Win/QuickLook/issues/1626)

- Hide PEViewer Title just like InfoPanel
- Avoid audio cover null exception in xaml

## 4.0.1

- Support more Markdown file extensions [#1562](https://github.com/QL-Win/QuickLook/issues/1562) [#1601](https://github.com/QL-Win/QuickLook/issues/1601)
- Support CLI options [#1620](https://github.com/QL-Win/QuickLook/issues/1620)
- Update pt-BR translations in Translations.config
- Delay initialization of MarkdownViewer
- Make .exe installer use MSI path by default [#1596](https://github.com/QL-Win/QuickLook/issues/1596)
- Fix style issues in the Search Panel [#1592](https://github.com/QL-Win/QuickLook/issues/1592)
- Fix volume control not working [#1578](https://github.com/QL-Win/QuickLook/issues/1578)
- Fix exception when checking for updates [#1577](https://github.com/QL-Win/QuickLook/issues/1577)

## 4.0.0

- Add built-in PE viewer plugin
- Add built-in font viewer plugin
- Update translations
- Update dependent packages
- Add support for Multi Commander
- Add support for both Everything v1.4 and v1.5(a)
- Add "Open Data Folder" and dark mode support to tray menu
- Add "Restart QuickLookNext" option to tray menu [#1448](https://github.com/QL-Win/QuickLook/issues/1448)
- Implement modern message box UI
- Replace icons with Segoe Fluent Icons
- Detect and auto-fix Windows blocking issues [#1495](https://github.com/QL-Win/QuickLook/issues/1495)
- Adjust tray menu position
- Use MicaSetup to create EXE installer
- Fix plugin installer description length limit
- Prevent crash when WMI fails [#1379](https://github.com/QL-Win/QuickLook/issues/1379)
- Show toast when "Prevent Closing" cannot be cancelled [#1368](https://github.com/QL-Win/QuickLook/issues/1368)
- Add support for multi-layer GIMP .xcf files [#1224](https://github.com/QL-Win/QuickLook/issues/1224) for ImageViewer
- Fix .xcf file extension check [#1229](https://github.com/QL-Win/QuickLook/issues/1229) for ImageViewer
- Fix HEIC preview rendering [#1470](https://github.com/QL-Win/QuickLook/issues/1470) for ImageViewer
- Add support for .qoi, .icns, .dds, .svgz, .psb, .cur, and .ani formats for ImageViewer
- Improve animated WebP support (x64 only) [#1024](https://github.com/QL-Win/QuickLook/issues/1024) [#1324](https://github.com/QL-Win/QuickLook/issues/1324) for ImageViewer
- Improve GIF decoding performance [#993](https://github.com/QL-Win/QuickLook/issues/993) for ImageViewer
- Add copy button to image viewer [#1399](https://github.com/QL-Win/QuickLook/issues/1399) for ImageViewer
- Fix SVG rendering error [#1430](https://github.com/QL-Win/QuickLook/issues/1430) for ImageViewer
- Add double-encoding detection [#471](https://github.com/QL-Win/QuickLook/issues/471) [#600](https://github.com/QL-Win/QuickLook/issues/600) for TextViewer
- Improve dark mode rendering for TextViewer
- Catch exceptions from XSHD loader for TextViewer
- Add syntax highlighting for shell scripts [#668](https://github.com/QL-Win/QuickLook/issues/668) for TextViewer
- Add dark mode support for C# syntax highlighting for TextViewer
- Improve support for comic archive formats [#1276](https://github.com/QL-Win/QuickLook/issues/1276) for ArchiveViewer
- Redesign file list with Fluent UI for ArchiveViewer
- Change default background color to blue for CsvViewer
- Fix issue with non-UTF8 CSV encoding for CsvViewer
- Improve rendering and stability for MarkdownViewer
- Add support for password-protected PDFs [#155](https://github.com/QL-Win/QuickLook/issues/155) for PDFViewer
- Enable auto-resizing of the viewer window for PDFViewer
- Fix audio cover parsing error for multiple embedded images for VideoViewer
- Add lyric (.lrc) support for audio files [#1506](https://github.com/QL-Win/QuickLook/issues/1506) for VideoViewer
- Add support for .mid audio format [#931](https://github.com/QL-Win/QuickLook/issues/931) for VideoViewer
- Fix time label overflow in long videos for VideoViewer

## 3.x and earlier

One line per release, newest first. These notes come from the same period as the Chinese archive
([CHANGELOG.zh-CN.md](CHANGELOG.zh-CN.md)), which holds the full text.

### 3.43.0

- Download progress in the updater (percentage, size, cancel), and the package gained a
  `Readme.txt` first-run note covering updates, logs and the optional switches.

### 3.42.1

- Performance and memory baselines recorded for the tray process.

### 3.42.0

- Preview warm-up: the formats this user actually opens are prepared in the background right after
  startup, so a first preview feels like the second one (text 289 → 96 ms, image 170 → 100 ms).

### 3.41.0

- Fixed auto-update getting stuck at "downloaded but will not update".
- First preview after login made much faster (the window and the tray icon are built before the
  keyboard hook starts), and the update prompt got the app's own material instead of a stock window.

### 3.40.0

- WebView2 profiles stopped piling up: a stale lock is repaired first, the profile is rebuilt in
  place only if that fails, and the maintenance sweep removes abandoned folders.

### 3.39.0

- The warm WebView2 controller pool was extended to every web-based preview, and the preview window
  gained a caption that behaves like the rest of the app.

### 3.38.0

- Database previews show the window first and fill the grid afterwards.

### 3.37.0

- Font preview switched to native rendering.

### 3.36.0

- WebView2 initialisation failures recover instead of leaving a blank panel.

### 3.35.0

- The update prompt asks "update now / skip this version" instead of downloading on the click, and
  the data location moved to a single folder.

### 3.34.0

- WebView2 controllers are pooled and reused (~300-400 ms saved per web preview).

### 3.33.0

- Following the Explorer selection became event driven instead of a 500 ms poll.

### 3.32.1

- Packaging fix: a copy of `QuickLook.Common.dll` stays in the package root so the 3.31.0 updater
  accepts the new `lib\` layout; behaviour identical to 3.32.0.

### 3.32.0

- Delivery size cut (157.9 MB unpacked, 61.4 MB zipped) with plugin dependency governance, the memory
  diagnostics hook, and fixes found during that verification.

### 3.31.0

- Regression tests and CI, a more reliable plugin match, a cleaned-up plugin contract, a faster
  settings store and update handling.

### 3.30.0

- Plain-text preview loads lazily, cutting idle memory by about 40 MB.

### 3.29.0

- Day-to-day use (mostly PDFs) uses less memory; assorted fixes.

### 3.28.0

- Plugin residency is driven by the recorded usage statistics.

### 3.27.0

- Robustness fixes.

### 3.26.0

- Office previews no longer flash white.

### 3.25.0

- Reverted 3.24.0's "keep the spinner while Office/Markdown parses" (it made opening feel slower);
  parsing stays in the background and the content appears without a spinner.

### 3.24.0

- Smoother previews: Markdown/Mermaid parse in the background and the spinner layer no longer blocks
  input.

### 3.23.0

- Previews stop stuttering (performance work).

### 3.22.0

- Performance and memory work, plus general polish.

### 3.21.0 / 3.20.0

- Visual polish and engineering hygiene (three plus two layout/theme items).

### 3.19.0 / 3.18.0

- Tray menu submenus fixed (they no longer swallow quick clicks).

### 3.17.0 / 3.16.0

- Office preview layout and behaviour reworked.

### 3.15.0 / 3.14.0 / 3.12.0

- The self-rendered Office line, stage by stage: PowerPoint, then Word, then Excel.

### 3.13.0

- Excel preview fixes.

### 3.11.0 / 3.10.0 / 3.9.0 / 3.8.0 / 3.7.0

- A run of visual and performance releases: lighter UI assets, faster startup, and the Fluent-style
  surfaces.

### 3.6.0

- Faster, lighter Markdown preview.

### 3.5.0 / 3.4.0 / 3.3.0

- Memory and package-size reductions, faster cold start, more polish.

### 3.2.1

- Fixes settings and tray menu labels that showed raw keys (translation lookup moved to the program
  root, including the `portable.lock` check).

### 3.2.0 / 3.1.0

- Performance work and the packaging layout, plus restoring the full set of built-in plugins.

### 3.0.5

- Removed a demo Firebase API key from the embedded Excalidraw template (a GitHub secret-scanning
  warning; static rendering never used it).

### 3.0.4

- "Check for updates" downloads the release zip and restarts the app instead of just opening the
  release page (falling back to the page when the folder is not writable).

### 3.0.3

- Submenu clicks right after opening are no longer swallowed by the parent menu.

### 3.0.2

- The update check queries this fork's releases instead of upstream QuickLook's.

### 3.0.1

- The preview window is raised on every open/switch again (the off-screen warm-up made
  `IsVisible` true, which skipped `BringToFront`).

### 3.0.0

- Restored old-plugin compatibility: the plugin contract (`QuickLook.Common`, the
  `QuickLook.Plugin.*` prefix, metadata files and registry associations) went back to the old names,
  so plugins load without recompiling, while the app itself keeps the QuickLook-Next naming.

### 2.0.0

- The rename was completed: `QuickLook-Next.exe`, `QuickLookNext.*` namespaces, `QuickLookNext.App.*`
  pipes/mutexes and the `QuickLookNext.Plugin.*` plugin prefix, isolated from upstream. Settings
  moved to the `QuickLookNext` domain, so themes, language and the user plugin folder start fresh.

### 1.x (1.2.8 - 1.5.0)

- The fork's first line, based on QuickLook Lite 1.2.29: plugin compatibility, the off-screen preview
  warm-up, the tray menu, translation work and the first packaging scripts. 25 releases in this
  range; the detailed notes are in [CHANGELOG.zh-CN.md](CHANGELOG.zh-CN.md).
