# Changelog

All notable changes to TDPdf are documented here. TDPdf is a fork of [SteveTheKiller/KillerPDF](https://github.com/SteveTheKiller/KillerPDF); entries from before the rename describe upstream history continued under the previous name.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.15.0.0] - 2026-07-02

Fork-sync release porting built-in OCR from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) v1.6.0, adapted to TDPdf's single-file build and settings.

### Added

- **OCR (Tesseract), built into the single EXE** (upstream v1.6.0). TDPdf can now read the text off scanned/image PDFs:
  - **OCR Page to Clipboard** — recognize the current page's text and copy it (Tools ▸ OCR, the page right-click menu, or **Ctrl+Shift+O**).
  - **OCR Region to Clipboard** — drag a rectangle and OCR just that area.
  - **Make Searchable PDF** — OCR every page and save a new PDF with an invisible text layer aligned over the scan, so the result is selectable and searchable.
  - **Extract All Text** — OCR the whole document to a `.txt` or `.md` file.
  - **Language selection with on-demand download** — a multi-select language menu; each language's data is downloaded from the Tesseract project on first use (nothing is bundled, keeping the EXE small), with a **high-quality model** toggle (`tessdata_best`) for more accuracy at a larger download.
  - Long OCR operations show progress and can be cancelled with **Esc**.
- The native Tesseract engine is embedded in `TDPdf.exe` as a resource and self-extracted to a per-version cache under `%LOCALAPPDATA%\TDPdf\` on first use — the same single-file self-extraction pattern already used for the app's other native code, so no external install and **no Costura/Fody**. Language data lives in a stable `%LOCALAPPDATA%\TDPdf\tessdata` folder so downloaded packs survive app updates.

### Notes

- OCR requires a one-time internet download of the chosen language's data on first use; TDPdf shows a themed message if the download fails (for example, offline) and never crashes. OCR itself runs entirely locally.

## [1.14.0.0] - 2026-07-02

Fork-sync release porting the Transform tool from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) v1.6.0, adapted to TDPdf's page-operation and undo model.

### Added

- **Transform tool** (upstream v1.6.0). A new **Transform…** command on the page right-click menu and the Tools menu opens a themed dialog with a live preview for reshaping a page: **fine-angle rotation** (a −45°…+45° slider plus 90° quarter-turns and a numeric field), **scale** (10–400% with a live output-size readout), **flip** horizontal/vertical, and **straighten** — drag a line along anything that should be level and the page rotates to level it. A fixed-page-size vs. expand-canvas mode controls whether the result is clipped/padded to the original page box or grown to the rotated bounding box. The page's annotations are baked in first so they follow the transform, and the change is a single undo (Ctrl+Z), matching Rotate/Crop.

### Notes

- Applying a transform **rasterizes** the affected page to an image, so its text is no longer selectable and its annotations become part of the raster (they follow the transform). This matches how the Crop tool already works and how upstream implements Transform. TDPdf warns once per session before the first transform and notes it in the status bar afterward. As with the existing Crop and Rotate page operations, applying a transform commits the page change and clears the in-app (not-yet-saved) annotation overlay.

## [1.13.0.0] - 2026-07-02

Fork-sync release porting the annotation-editing improvements from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) v1.6.0, adapted to TDPdf's annotation model and settings bars.

### Added

- **Full RGB color picker with a screen eyedropper** (upstream v1.6.0). Every annotation color row (Text, Draw, Highlight, and Shape stroke/fill) now ends with a **custom-color** swatch that opens a themed picker: a saturation/value square and hue strip, live-synced RGB and hex inputs, a preview swatch, and an **eyedropper** that samples any pixel on screen. Recently picked colors are remembered across sessions (new `CustomColors` setting) and shown as a Recent row. The fixed swatches are unchanged.
- **Resizable, word-wrapping text boxes with an optional whiteout fill** (upstream v1.6.0). Placed text boxes now wrap at a fixed width, can be **resized** with a corner handle (the wrap width/height follow), and can carry an opaque **whiteout background** (toggle + pickable fill color in the Text bar) to cover what's underneath. **Double-clicking a placed text box re-opens it for editing** (content, size, color, width, and fill), distinct from the existing edit-PDF-text tool. The saved PDF reproduces the on-screen wrapping (same font metrics) and the whiteout fill. Existing single-line text annotations render and save exactly as before.
- **Restyle a selected annotation in place** (upstream v1.6.0). Selecting an annotation with the Select tool now reopens its style bar bound to that annotation, so changing size, color, stroke width, or fill restyles the selected item live instead of only affecting new annotations.

### Notes

- **Shift-click multi-select and cross-page marquee selection were intentionally not ported in this release.** In TDPdf the Select-tool drag-marquee drives text drag-select-to-copy, and the move/resize/delete/selection subsystem is built around a single selected annotation; a faithful multi-selection would risk regressing text selection and single-selection editing. It was deferred rather than shipped in a half-working state, and remains a candidate for a future, dedicated change. Single-annotation select/move/resize/restyle and all text-selection behavior are unaffected.

## [1.12.0.0] - 2026-07-02

Fork-sync release overhauling the print dialog with the layout, quality, and persistence options from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) v1.6.0 (Issue #83), adapted to TDPdf's existing themed print-preview window.

### Added

- **True 300 DPI print output** (upstream v1.6.0, Issue #83). Printed pages are now re-rasterized at a true 300 DPI at print time, so output is sharp instead of carrying the lighter on-screen preview resolution. The preview itself stays at ~200 DPI, and the 300 DPI render happens on demand one page at a time and only for the pages actually being printed, so it does not blow up memory the way rendering every page up front would.
- **Color / black-and-white and two-sided (duplex)** print options, applied at the driver level through the print ticket. The two-sided option is disabled automatically when the selected printer reports no duplex support.
- **Scale** (Fit to page or a custom percentage from 25–400%), **position** (nine-way — center, edges, corners), and **margins** (None / Narrow / Normal / Wide), replacing the previous always-centered, always-fit placement.
- **Pages per sheet (N-up)** — print 1, 2, 4, 6, or 9 pages tiled onto each sheet in reading order. The preview navigates by composed sheet and shows exactly what will print (the same sheet-composition code drives both preview and print).
- **Numeric steppers** for the copy count and the custom scale percentage — up/down buttons with arrow-key and mouse-wheel stepping, clamped to valid ranges.
- **Remembered print settings** — the last printer, orientation, color, and two-sided choices persist across sessions (falling back to the OS default printer if the remembered one is gone).

### Changed

- Copies continue to be handled by a single driver-level copy count (no manual copy loop), avoiding the duplicate-copy behavior some drivers exhibit.

## [1.11.0.0] - 2026-07-02

Fork-sync release porting the recent-files and folder/archive-drop features from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) v1.6.0, adapted to TDPdf's multi-tab `DocumentContext` architecture.

### Added

- **Recent files** (upstream v1.6.0). TDPdf now remembers the last 10 PDFs you opened. The start screen lists them (document icon, file name, and dimmed full path) so you can reopen one with a click, and **right-clicking the Open toolbar button** drops down the same list plus a **Clear List** item. The list persists across sessions in a new `RecentFiles` user setting, automatically drops entries whose file no longer exists, and deliberately excludes working copies that have no real on-disk home (newly merged/imported documents, decrypted temp copies of password-protected files, and rasterizer-recovered documents).
- **Drop a folder or .zip archive onto the window** (upstream v1.6.0). Dropping a folder (searched recursively), a `.zip` archive (extracted to a temp folder), or several files at once now gathers all the PDFs and images inside and, when there is more than one, asks whether to **merge them into a single PDF** or **open each in its own tab**. The merge runs on a background thread so the window stays responsive. Dropping a single PDF still opens it directly as before. A 50-file cap (with a confirmation prompt) guards against dropping an enormous folder.
- **Open images as a PDF** (upstream v1.6.0). Images found in a dropped folder/archive — or a dropped image file — are converted to PDF pages (one page per image, sized to the image; multi-frame TIFF/GIF expand to one page per frame) and opened as an unsaved document. Because these merged/imported documents and image imports have no saved location yet, **Ctrl+S routes them to Save As** so they are never silently written into the temp folder.

## [1.10.0.0] - 2026-07-02

Fork-sync release porting two self-contained features from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) v1.6.0, each adapted to TDPdf's custom window chrome and multi-tab architecture.

### Added

- **Document Info dialog (F12)** (upstream v1.6.0). View and edit the active PDF's metadata — Title, Author, Subject, Keywords, and Creator — in a themed dialog reachable from **File ▸ Document Info…** or **F12**. A read-only summary line shows the Producer, page count, PDF version, creation date, and file size. Saving writes the values into the document's Info dictionary and marks the tab dirty, so they are baked in on the next save. Editing metadata on a document with no PDF open is guarded.
- **Full-screen mode (F11)** (upstream v1.6.0). Press **F11** to hide all chrome — title bar, menu, toolbar, tab strip, sidebar, and status bar — so only the document pane fills the monitor; **F11** or **Esc** exits. Full-screen deliberately covers the taskbar (window goes borderless-normal + topmost over the full monitor rectangle rather than maximized, so the `WM_GETMINMAXINFO` taskbar-preserving maximize behavior is left intact for normal maximize), and the exact pre-full-screen window placement and chrome state (including a collapsed sidebar or hidden tab strip) are restored on exit.

## [1.9.2.0] - 2026-07-02

Fork-sync release porting the curated set of reliability and polish fixes from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) v1.6.1, each adapted to TDPdf's multi-tab `DocumentContext` architecture.

### Added

- **Continuous view stays sharp when zooming in and on high-DPI displays** (upstream v1.6.1, Issue #85). Continuous scroll previously rendered every page once at a fixed fit-width budget and then only scaled the bitmaps with the shared zoom transform, so zooming in — or viewing on a high-DPI monitor — left the pages soft. A debounced, cancellable pass now re-renders **only** the pages near the viewport at a DPI- and zoom-aware resolution and swaps them into place; pages that scroll away are restored to their base bitmap so the higher-resolution bitmaps are released and never accumulate beyond the visible window.

### Changed

- **Faster mouse-wheel scrolling in every view mode and the page sidebar** (upstream v1.6.1). Each wheel notch now scrolls roughly three times the previous distance (~120 px vs the default ~40 px), including Shift+wheel horizontal scrolling and the tilt-wheel; Grid and Continuous always scroll and are never hijacked into page navigation, while Single and Two-Page keep their scroll-boundary → page-navigation behavior.

### Fixed

- **A failed save no longer loses your edits** (upstream v1.6.1, Issue #106). Saving occasionally failed with "Cannot retrieve stream length." (a stream with a broken or indirect `/Length`) or "File streams are not yet implemented"; the save simply errored out. Save, Save As, and Save Flattened now detect these recoverable PdfSharpCore errors, repair the document once through the existing PDFium round-trip, and retry the save automatically — re-baking the in-memory annotations and edits so nothing is lost. Only if the repaired retry also fails is the themed error shown. The recoverable-error classifier (previously used only when reopening a saved file) was extended to cover both messages.
- **Save As on a merged or imported document no longer risks crashing before the dialog opens** (upstream v1.6.1, Issue #112). Seeding the Save As filename from a document with no original path is now fully guarded, so a null or malformed seed just opens the dialog with its defaults. The dialog is seeded from the document's display name rather than the internal working-copy path.
- **Esc now cancels the password prompt and the signature save-name dialog** (upstream v1.6.1, Issue #111), matching the Enter/Esc behavior already present in TDPdf's other dialogs.

## [1.9.1.0] - 2026-06-22

### Added

- **Typed signatures.** The Signature tool's popup now offers **Type Signature** alongside Create (draw) and Import Image. Type your name, pick from the handwriting fonts installed on the system (Segoe Script, Segoe Print, Gabriola, Ink Free, Lucida Handwriting, …) and an ink color (black or blue), and a live preview shows the result. On save it is rasterized to a transparent PNG and stored as a normal saved signature, so it persists in `signatures.json` and places, resizes, and bakes into the PDF through the exact same path as an imported-image signature.
- **Export Tables to CSV** (File menu). Pulls the tabular text out of a PDF into a CSV file that Excel opens directly — the lightweight equivalent of Adobe Reader's "export as spreadsheet." Every page is written to one `.csv`, each page as its own `Page N` block; pages with no extractable text are skipped. Detection is heuristic ("stream"-style): words are grouped into rows by vertical position and into columns by clustering their left edges, reusing the same PdfPig word/bounding-box extraction already used by search and drag-select-copy. It works best on clean, left-aligned grid tables and may merge or split columns on irregular, right-aligned, or merged-cell layouts; scanned/image-only PDFs (no selectable text) produce nothing to export.

## [1.9.0.0] - 2026-06-16

Fork-sync release porting the curated set of upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) v1.4.2–v1.5.1 features and fixes that TDPdf had not yet adopted, each adapted to TDPdf's multi-tab `DocumentContext` architecture.

### Added

- **Interactive PDF form filling** (upstream v1.4.2). Interactive AcroForm fields — text inputs, checkboxes, radio buttons, and dropdowns — now render as live themed controls overlaid on the page using the same PDF-point→canvas coordinate conversion as link overlays. Fill them in directly; values are written back into the PDF on save (`/V`, plus `/AS` for buttons) with regenerated `/AP /N` appearance streams so the values are visible in other viewers (falling back to `/NeedAppearances` where an appearance cannot be regenerated). Per-document field state lives on `DocumentContext` so it survives tab switches, and AcroForm parsing is fully guarded so a malformed form can never crash open or save. List boxes, push buttons, and digital-signature fields are not fillable (parsed and skipped), matching upstream.
- **Built-in print preview** (upstream v1.5.0). Printing now opens a themed TDPdf preview dialog — page-by-page preview with printer selection, orientation, copy count, and page-range parsing (for example `1-3,5`) — instead of the OS print dialog (which reported "This app doesn't support print preview"). Pending annotations are burned into a temp printable copy before previewing and the editable document is restored afterward, preserving the prior print-with-annotations behavior.
- **Continuous scroll view mode** (upstream v1.5.0). A view mode that stacks every page in one vertical strip with progressive background rendering (large documents fill in without freezing the UI); the page-number indicator and sidebar thumbnail track the scroll position, and selecting a page or zoom scrolls the strip. Opens fit-to-width. Continuous mode is view-and-navigate only — annotation editing remains in Single, Two-Page, and Grid modes.
- **Two-page view mode** (upstream v1.5.0). Displays the current page and the next page side-by-side; the primary (selected) page remains fully editable (annotations, links, forms), the secondary page is view/click-to-navigate. Opens fit-to-page.
- **View-mode selector** in the Settings dialog (Single / Continuous scroll / Two pages / Grid). The selection persists across sessions; the existing toolbar toggle remains a quick Grid↔Single switch.
- **Right-click actions on PDF links** (upstream v1.4.3). Right-clicking a link overlay now offers "Remove Link from PDF" (removes the native link annotation, not just the overlay) and, depending on the link, "Copy Email Address" (for `mailto:` links) or "Copy URL".

### Changed

- **Save Flattened PDF now rasterizes across CPU cores** (upstream v1.5.1, Issue #68). PNG encoding runs in parallel while the PDFium render step stays serialized (pdfium.dll is not thread-safe); pages are sized per-page at 150 DPI to their own box and assembled in order. Large documents flatten faster.
- **Minimum zoom lowered from 25% to 5%** (upstream v1.5.1) so single-page view can zoom out further and grid/continuous views can pack more pages; 5% and 10% presets added to the zoom dropdown.

### Fixed

- **Link annotations no longer render their borders as colored rectangles / strikethrough-like lines in saved PDFs** (upstream v1.4.3). On save, `/AP`, `/C`, and `/BS` are stripped from every link annotation and an invisible `/Border [0 0 0]` is set.
- **The page view now fits-to-page after a rotation** (upstream v1.4.3) so the full rotated page is visible without manual rezoom.
- **Rotating pages in an encrypted (owner-restricted RC4) PDF no longer fails with "Unexpected token 'xref'"** (upstream v1.4.3). PdfSharpCore can silently emit a broken cross-reference entry when re-saving an encrypted file after a modification; the save→reload path now detects the failed reopen, pipes the saved file through PDFium to rebuild a valid xref (preserving the new page rotation and stripping encryption), and retries the open.

## [1.8.3.0] - 2026-06-11

### Added

- **Startup splash.** Launching TDPdf now shows an immediate splash (brand crest, "TDPdf", and an animated progress bar) so a double-click gives instant feedback while the main window — which has a heavy first render — is being built. The splash runs on its own dedicated UI thread, so it stays painted and animated even while the main thread is blocked constructing the window, and it disappears the moment the real window has rendered. It only appears for genuine interactive launches; the `/install`, `/uninstall`, telemetry-provisioning, and single-instance "open this PDF in the already-running window" paths never show it. A hard max-lifetime fallback guarantees a crash or hang during window construction can never strand the splash on screen.

### Notes

- **"Windows cannot access the specified device, path, or file. You may not have the appropriate permissions to access the item." when opening a PDF attachment from the _new_ Outlook is an Outlook-side problem, not something TDPdf can change from its own code.** The new Outlook for Windows ("Olk") stages opened attachments under `%LOCALAPPDATA%\Microsoft\Olk\Attachments\…` and then asks the shell to launch the registered handler. This error is raised by the shell _before `TDPdf.exe` ever starts_, when that staging folder is missing/corrupt or Outlook's local state is corrupt; it affects every PDF handler (Adobe, Edge, etc.), not just TDPdf. It is distinct from the two attachment issues already addressed: the `1.8.1.0` Attachment-Manager `FTA_OpenIsSafe` change (the "Open File – Security Warning" prompt) and the Defender ASR block noted under `1.8.2.0`. Remediation is environment-side: ensure `%LOCALAPPDATA%\Microsoft\Olk\Attachments` exists (create the `Attachments` folder if missing), reset Outlook's attachment cache with `olk.exe --clearLocalState`, or Repair/Reset "Outlook (new)" in **Settings → Apps**. Saving the attachment first and opening it from Explorer, the File menu, or drag-and-drop is unaffected.

## [1.8.2.0] - 2026-06-09

### Fixed

- **Grid (multi-page) view no longer crashes the app intermittently with an `ArgumentOutOfRangeException` during layout.** When secondary pages were added to or removed from the page-grid panel from an async Dispatcher continuation while WPF was already mid-layout, the framework `WrapPanel` could index its internal visual collection out of range (`index ('2') must be less than '1'`) inside `MeasureOverride`/`ArrangeOverride`. Depending on timing this surfaced as a recoverable UI-thread fault or escaped to the AppDomain handler and terminated the process. The page-grid panel is now a hardened `SafeWrapPanel` that absorbs this transient framework race and re-queues a clean layout pass instead of letting it crash the app.

### Notes

- **"Windows Defender blocked this action" when opening a PDF attachment directly from Outlook is a Microsoft Defender Attack Surface Reduction (ASR) policy, not something TDPdf can change from its own code.** The block comes from the ASR rule *Block Office communication application from creating child processes* (`26190899-1602-49e8-8b27-eb1d0a1ce869`), which prevents Outlook from launching any child process (including `TDPdf.exe`) when a user double-clicks an attachment. Saving the attachment first and opening it from Explorer, the File menu, or drag-and-drop is unaffected. Remediation is an org-side ASR exclusion for `TDPdf.exe` (configured in Microsoft Defender / Intune); no application can self-exempt from ASR. The earlier `1.8.1.0` Attachment-Manager change does not affect this Defender prompt.

## [1.8.1.0] - 2026-06-08

### Fixed

- **Opening a PDF attachment from Outlook no longer triggers a "Windows Security" / "Open File - Security Warning" prompt.** When Outlook opens an attachment it saves the file to a temp folder tagged with a Mark-of-the-Web zone identifier; the shell then warned before launching the associated handler because the `TDPdf.pdf` ProgID's open verb wasn't marked safe. The file-handler registration now writes `EditFlags = FTA_OpenIsSafe (0x00010000)` on the `TDPdf.pdf` ProgID, telling the Windows Attachment Manager that opening a `.pdf` with TDPdf is safe. This suppresses the prompt for zoned files (Outlook attachments, downloaded files, etc.). The fix applies on the next install/upgrade since it is written during file-association registration.

## [1.8.0.0] - 2026-06-07

Ports document outline / bookmark navigation from upstream [KillerPDF v1.4.2](https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.4.2), adapted to TDPdf's multi-tab sidebar.

### Added

- **Document outline / bookmark navigation.** The sidebar now has **PAGES** and **OUTLINES** tabs. When a PDF contains bookmarks, the OUTLINES tab becomes available and shows the document's bookmark tree (indented by nesting depth); clicking an entry jumps to its target page. Bookmark destinations are resolved through the same `/Dest` and `/GoTo`-action logic already used for in-document link annotations (including named destinations and name trees). The outline is parsed per tab and cached on each `DocumentContext`, with cycle and depth guards so a malformed outline tree can never break opening a document. PDFs without bookmarks keep the OUTLINES tab disabled and stay on PAGES.

## [1.3.1.0] - 2026-06-06

Ports the text-extraction ordering fix from upstream [KillerPDF v1.4.2](https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.4.2).

### Fixed

- **Copied text was out of order on PDFs that store glyphs in non-reading order** (upstream Issue #66). Both drag-select copy and Select All now route text through a shared `WordsToText` helper that sorts words top-to-bottom then left-to-right, groups them into lines using a dynamic threshold (~40% of average word height, minimum 4 PDF units) so words on slightly different baselines still land on the correct line, and re-sorts each line left-to-right. Select All previously used PdfPig's raw `page.Text`, which preserved the underlying glyph order.

## [1.3.0.0] - 2026-06-06

Resilience and observability release: the app now heals corrupt user settings, recovers from PDFs that the strict parser rejects, captures crashes to telemetry automatically, and turns previously-fatal recoverable exceptions into graceful errors.

### Added

- **Automatic recovery for "not a valid PDF" files.** Some scanner output (and other lightly-malformed PDFs) is rejected by PdfSharpCore's strict parser even though it renders fine. When the initial open fails, TDPdf now falls back to rasterizing every page through PDFium (Docnet) at 150 DPI into a fresh, editable document instead of giving up. Recovered documents open with a status note — "(recovered - pages rasterized, text not selectable)" — so it's clear the text layer was lost. Emits a `File.OpenRecovered` event; a hard failure emits `File.OpenFailed`.
- **Self-healing user settings.** A corrupt `user.config` previously threw `ConfigurationErrorsException` at startup — before telemetry or the crash handlers were even wired up — which both crashed the app and left no trace in App Insights. Startup now proactively validates settings, deletes the corrupt per-user config, reloads defaults, and continues; a `Settings.Recovered` event is emitted so the recovery is visible.
- **Operation timing + failure telemetry across file operations.** Open, Save, Save As, Save Flattened, and Print are now wrapped in timed operation scopes (`Op.*` events with a `DurationMs` metric and `Success` flag) and emit dedicated failure events (`File.OpenFailed`, `File.SaveFailed`, `File.PrintFailed`) with sanitized context instead of failing silently.
- **Telemetry primitives:** `TrackMetric`, `TrackTrace`, `TrackOperation`, and a disposable `StartOperation` scope (stopwatch-based, with `.With(key,value)` enrichment and `.Fail(ex)`), all no-ops when telemetry is disabled.

### Changed

- **Crashes auto-report to App Insights.** Unhandled exceptions already routed through the crash reporter now reliably forward to telemetry as sanitized `Crash` events, and the global handlers (`DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) flush telemetry before exit and are hardened so the handlers themselves can never throw a secondary exception.
- **More exceptions recover instead of crashing.** The recoverable-exception classifier now also treats `ConfigurationErrorsException`, `COMException`, `NotSupportedException`, `FormatException`, `OverflowException`, `TimeoutException`, `OperationCanceledException`, `KeyNotFoundException`, `IndexOutOfRangeException`, and `NullReferenceException` as recoverable (shown as a graceful error dialog) rather than fatal. Genuinely unrecoverable conditions (`OutOfMemoryException`, `StackOverflowException`, `AccessViolationException`, and native PDFium stacks) remain fatal.

## [1.2.0.0] - 2026-05-30

### Added

- **Open multiple PDFs as tabs in a single window.** Opening another PDF — via File ▸ Open (now multi-select), drag-and-drop, or double-clicking a `.pdf` in Explorer while TDPdf is already running — now adds it as a tab instead of replacing the current document or spawning a separate window. A tab strip appears once a second document is open; each tab shows the file name, an unsaved-changes dot, and a close button, and `Ctrl+W` closes the active tab. Each tab keeps its own pages, annotations, undo/redo history, search results, and current page. The cross-process behavior (Explorer double-click forwarding to the existing window) is governed by a new **"Open PDFs as tabs in a single window"** setting (Settings dialog; on by default) — turn it off to restore one-window-per-file. Internally, every per-document field was moved into a `DocumentContext` so the existing single-document code paths continue to operate on whichever tab is active.

### Changed

- **PDF file-type association now shows a generic PDF icon instead of the company logo.** The TDPdf company logo remains the application icon (taskbar, window, Add/Remove Programs), but `.pdf` files associated with TDPdf now display a stock PDF document icon in Explorer and the file-type association UI. The icon (`Resources\pdf-file.ico`) ships embedded in the single-file EXE and is extracted to the install directory on install; the `TDPdf.pdf\DefaultIcon` registry value points at it, falling back to the EXE icon if the extracted file is missing.

## [1.1.0.1] - 2026-05-19

Ports upstream KillerPDF v1.4.1 user-facing improvements into TDPdf, plus zoom-drift and HiDPI memory fixes uncovered during the port.

### Added

- **`Ctrl+S` / `Ctrl+Shift+S` keyboard shortcuts.** `Ctrl+S` saves in place to the currently open file (no dialog), `Ctrl+Shift+S` opens Save As. The previous behavior — every save going through a Save As dialog — was a usability papercut for anyone iterating on a single file.
- **Arrow-key page navigation.** Up/Down/PageUp/PageDown move between pages in the sidebar without stealing focus from text edits or annotation tools.
- **Page jump box in the sidebar header.** Type a page number and press Enter to jump; matches the upstream UX and is much faster than scrolling the thumbnail list on a long document.
- **`Ctrl+?` keyboard-shortcut overlay.** Themed dialog listing every shortcut TDPdf binds, so the new key bindings above are discoverable.
- **Multi-page grid view toggle.** Toolbar button switches between single-page and grid view. Grid view tiles up to 25 additional pages from the current position so wide monitors don't waste space on long PDFs.
- **Scroll-wheel page navigation at scroll edges.** Reaching the top of the current page and continuing to scroll up moves to the previous page; reaching the bottom and continuing scrolls into the next page. Matches the upstream `KillerPDF` reading flow.
- **Crop bar Remove Crop / Remove All buttons and corner-resize handles.** The crop bar now lets you delete a single crop or every crop on the page, and the four corners of an active crop rectangle have draggable resize handles instead of being width/height-only.

### Fixed

- **Signature and annotation drift across zoom changes.** Annotations rendered against the page raster at zoom A could end up a few pixels off when the page was re-rendered at zoom B because the DPI rounding path differed slightly between the two render entry points. `Services/PdfDocumentService.cs` and the zoom plumbing in `MainWindow.xaml.cs` now share a single DPI-to-pixel formula so signatures, ink, highlights, and text edits stay pinned to the same PDF coordinate at every zoom level.

### Performance

- **HiDPI memory: release secondary page bitmaps on grid clear; cap render count.** `ClearSecondaryPages` now nulls `Image.Source` on every removed page before dropping the `Border`, so the underlying `WriteableBitmap` (multi-MB per page on HiDPI) is eligible for GC immediately instead of staying pinned in WPF's visual tree. `RenderAdditionalPages` also caps the secondary render window at 25 pages so a 500-page PDF in grid view doesn't try to allocate half a gigabyte of bitmaps up front.
- **Async secondary-page rendering with cancellation.** `RenderAdditionalPages` previously called Docnet (PDFium) synchronously on the UI thread, freezing the window while every secondary page was decoded; rapid zoom or page-change events could also stack overlapping renders whose stale pages would land on the panel after the user had moved on. A new `_secondaryRenderCts` cancels any in-flight render at the top of each call, the PDFium decode loop runs on `Task.Run`, and cancellation is re-checked between WPF rebuilds so late results are dropped cleanly.

### Attribution

- These features and fixes track [`SteveTheKiller/KillerPDF` v1.4.1](https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.4.1). See `NOTICE` for the GPLv3 §5(a) upstream attribution.

## [1.0.0.6] - 2026-05-19

### Added

- **Build-time-embedded telemetry key for managed deployments.** Release builds now bake an encrypted App Insights connection string into `TDPdf.exe` itself, so a managed/Intune deployment of TDPdf auto-provisions `%ProgramData%\TDPdf\telemetry.dat` on first launch (or at `/install` time when run as SYSTEM) — no separate provisioning app or `/set-telemetry` ceremony required. The connection string is read at release time from `$env:TDPDF_APPINSIGHTS_CONN`, encrypted with a fresh AES-256-CBC key per release, and the AES key is XOR-split across two compiled-in halves. When the env var is unset (dev / CI / source-bundle builds), no key is embedded and TDPdf is a no-op exactly as before. The GPLv3 source bundle ships the placeholder, never the real key.
- **`/clear-telemetry` is now sticky.** Running `TDPdf.exe /clear-telemetry` on a device deletes `telemetry.dat` AND writes a `telemetry.disabled` sentinel; auto-provisioning on the next launch is suppressed by the sentinel, so the device stays disabled across re-installs of the same build. Running `/set-telemetry` clears the sentinel and re-enables.
- **`build/embed-telemetry-key.ps1`** — release-time generator that produces the gitignored `Diagnostics/EmbeddedTelemetry.Generated.cs`. Reads the connection string only from the environment (never argv). `release.ps1` invokes it before `dotnet publish` and unconditionally strips the generated file afterwards so the working tree never contains the secret.

### Security

- **Threat model framing (unchanged):** the embedded key is a speed bump against casual extraction by a non-admin user on the device, not strong cryptography. Both XOR halves and the AES IV are compiled into `TDPdf.exe`, so a determined reverse engineer with the binary can still recover the connection string. Pair with a **dedicated** App Insights resource, a **daily ingestion cap**, and **key rotation** on suspected exposure. Documented in `docs/intune-distribution.md`.
- **No secret in git, no secret in the source bundle.** The placeholder `Diagnostics/EmbeddedTelemetry.cs` is the only version tracked; the generated file is gitignored; `bundle-source.ps1` uses `git ls-files`, so the generated file is never copied into `TDPdf-<version>-src.zip` even when present in the working tree.

### Changed

- **Version** bumped to `1.0.0.6` (assembly, file, product). Detection script `build/intune/Detect-TDPdf.ps1`, landing page, and in-app version label updated to match.

## [1.0.0.5] - 2026-05-19

### Added

- **Opt-in Application Insights telemetry** — TDPdf can now emit anonymous usage and crash telemetry to Azure Application Insights, but **only** when an administrator has explicitly provisioned a connection-string file on the device. With no provisioning, telemetry is fully no-op: the SDK is initialized with an empty configuration and no network calls are made. Events are limited to a small set (`App.Startup`, `Install.Start`/`Success`/`Crash`, `Tool.Selected`, `File.Open`/`New`/`Save`/`SaveFlattened`/`Merge`/`Split`/`Print`, `Crash`); no file paths, file names, document content, user names, or device identifiers are sent. Each session is anonymous (no persistent `User.Id` or `Device.Id`).
- **`TDPdf.exe /set-telemetry`** — provisioning CLI. Reads the App Insights connection string from **stdin** (never from `argv`) so it can't leak via process listings or installer logs, then writes a DPAPI-`LocalMachine`-encrypted blob to `%ProgramData%\TDPdf\telemetry.dat`. Intended usage from an elevated Intune/MDM context: `type conn.txt | TDPdf.exe /set-telemetry`.
- **`TDPdf.exe /clear-telemetry`** — removes the provisioning file and instantly disables all telemetry on the device.
- **`Diagnostics/Sanitizer.cs`** — shared PII scrubber used by both the local crash log and outbound telemetry. Redacts `password=` / `passphrase=` / App Insights connection-string fragments, Windows UNC/drive/relative paths, POSIX paths, and portable-PDB `in /file:line N` frames. Provides a stable 12-hex-char `GroupingKey(Exception)` (SHA-256 prefix of `type|firstScrubbedFrame`) so crashes can be bucketed without exposing content.

### Security

- **Hardened-at-rest provisioning file** — `telemetry.dat` is encrypted with DPAPI `LocalMachine` scope plus a fixed 32-byte entropy parameter, written atomically (`.tmp` → `File.Replace`), and then ACL'd explicitly: inheritance disabled, `SYSTEM` and `BuiltinAdministrators` get `FullControl`, `AuthenticatedUsers` get `Read`/`ReadAndExecute` only. The parent `%ProgramData%\TDPdf` directory is hardened the same way to prevent pre-create squatting by a non-admin user.
- **No `TrackException`** — the telemetry wrapper deliberately does not expose `TelemetryClient.TrackException`, which would serialize raw `Exception.Message` and `StackTrace` (often containing user file paths). All crashes flow through `Telemetry.TrackCrash(ex, source, recoverable)`, which builds a sanitized property bag (`ExceptionType`, scrubbed `Message`/`StackTrace`, `GroupingKey`) and emits it as a regular `TrackEvent("Crash", …)`.
- **No on-disk telemetry buffer** — the SDK is configured with `InMemoryChannel` (not `ServerTelemetryChannel`), so unsent events live only in process memory. There is no per-user buffer directory whose ACLs could leak content. Acceptable trade-off: events may be lost on crash or network failure.
- **No auto-collectors** — the wrapper builds its own `TelemetryConfiguration.CreateDefault()` instance (not `TelemetryConfiguration.Active`) and does not enable dependency tracking, live metrics, performance counters, or heartbeat. Only events explicitly emitted by TDPdf code are sent.
- **`/set-telemetry` does not log its input** — that CLI branch deliberately bypasses `InstallLog.WriteHeader`, which echoes `e.Args` verbatim, to ensure that even an accidentally-passed positional secret never reaches the installer log.

### Changed

- **`Diagnostics/CrashReporter.cs`** — the inline path-scrubbing regex was removed; the local crash log and the outbound `TrackCrash` event now both route through `Sanitizer.Scrub`, guaranteeing identical redaction in both surfaces. On any unhandled exception, `Report()` now writes the local log first (best-effort), then emits a sanitized telemetry crash event (also best-effort); failures in either path never propagate.
- **Version** bumped to `1.0.0.5` (assembly, file, and product). Detection script `build/intune/Detect-TDPdf.ps1`, landing page, and in-app version label updated to match.

## [1.0.0.4] - 2026-05-18

### Fixed

- **Menu bar didn't open and showed literal underscores** (`_File`, `_Edit`, `_View`, `_Tools`, `_Help`). The custom dark-themed `MenuItem` ControlTemplate had two latent bugs from #48: it omitted `RecognizesAccessKey="True"` on the header `ContentPresenter`, so access-key underscores rendered as literal characters; and it had no `PART_Popup`, so clicking a top-level menu never opened the dropdown. Replaced with role-aware templates (`TopLevelHeader`, `TopLevelItem`, `SubmenuHeader`, `SubmenuItem`) that include a proper `Popup`, render access keys correctly, and keep the existing dark palette.

### Changed

- **Intune detection rule** — promoted a PowerShell detection script (`build/intune/Detect-TDPdf.ps1`) to recommended, demoted the manual registry rule to a fallback. Some Intune tenants reject the manual rule at save time with "invalid detection rule, unable to parse detection rule" when **Registry → Value comparison → Data type: Version** is used; the script form sidesteps tenant-side validation entirely. Manual-rule guidance now uses **String comparison** against the literal release version (e.g. `1.0.0.4`) and spells out the exact field formats (no `HKLM\` prefix, no leading backslash). A "value exists" rule on `Installed` is documented as the simplest fallback when no minimum-version gate is needed.

### Added

- `build/intune/Detect-TDPdf.ps1` — checks `HKLM\Software\TDPdf` for `Installed=1` and `Version >= $MinVersion`, writes one line to stdout on success, exits 0 otherwise. Bump `$MinVersion` per release.

## [1.0.0.3] - 2026-05-18

### Fixed

- **Intune "installed but not detected" failures** — the `/install` and `/uninstall` headless paths could exit 0 even when the install never actually placed the EXE. Three changes close the loop:
  - The install short-circuit now runs **before** `ThemeManager.Initialize` and the crash-dialog plumbing, so a SYSTEM/session-0 install never touches pack-URI ResourceDictionary loading, `SystemEvents`, or any code that wants a UI.
  - `DoInstall` no longer shows a `MessageBox` from its catch (an invisible message box under SYSTEM was the silent-success vector). `OnStartup` now wraps `/install` and `/uninstall` in explicit `try/catch` that maps any exception to `Shutdown(1)`, so Intune sees a real failure code instead of a swallowed exception.
  - `DoInstall` verifies the EXE actually landed on disk (exists, non-zero length, file version readable) and writes the `HKLM\Software\TDPdf` `Installed`/`Version` registry marker **last**, after the file copy, Add/Remove Programs entry, and file-handler registration all succeed. A partial install can no longer detect as complete.
- **Interactive Install button** still surfaces a dark-themed error dialog on failure — `InstallAndRelaunch` now wraps `DoInstall` itself, since that callsite is guaranteed to have a UI thread.

### Added

- **Install / uninstall log** at `%ProgramData%\TDPdf\install.log` (SYSTEM-context machine install) or `%LocalAppData%\TDPdf\install.log` (interactive user install). Records process identity, scope, source / destination paths, post-copy `FileVersionInfo`, every registry step, and the final outcome. Rotates at ~1 MB. Strictly best-effort — a logging failure never blocks install. Designed for Intune triage when a deployment "succeeds" but the detection rule fails.

### Changed

- Recommended Intune detection rule is now **Registry-based** on `HKLM\Software\TDPdf` value `Version`, "Version comparison" `Greater than or equal to` the release version. This is more reliable than file-version sniffing through `%ProgramFiles%` and dodges the "Associated with a 32-bit app on 64-bit clients" footgun. The file-version rule is documented as a fallback.
- Recommended Intune **install command** is now `TDPdf.exe /install /silent` (was `TDPdf.exe /install`). With `/silent` set, the headless install path will never even attempt to surface a UI dialog under SYSTEM.
- `release.ps1` summary block prints the new registry-based detection rule and silent install command.

## [1.0.0.2] - 2026-05-18

### Fixed

- **Intune system-context install** — when invoked by Intune with install behavior set to **System**, the installer now correctly installs machine-wide to `%ProgramFiles%\TDPdf\` and writes its registry entries under `HKLM` instead of dropping them in LocalSystem's user profile. This fixes the `0x80070005` access-denied deployment failure and the missing Start Menu shortcut. Per-user installs (interactive double-click) continue to work as before.
- **Saved signatures lost on machine-wide installs** — `signatures.json` is now stored under `%LocalAppData%\TDPdf\` so it stays writable when the EXE lives under `%ProgramFiles%`. A legacy file next to the EXE is migrated automatically on first launch.

### Changed

- Start Menu shortcut is now placed in the **All Users** Start Menu (`%ProgramData%\Microsoft\Windows\Start Menu\Programs\TDPdf`) for system-context installs, so every user of the machine sees the entry.
- `IsInstalled()` and `Uninstall()` discover the existing install (HKLM vs HKCU) from registry markers instead of inferring from the current process context, so the elevated Add/Remove Programs uninstall correctly tears down a machine-wide install even though it runs as the user (not SYSTEM).
- Add/Remove Programs `DisplayVersion` is now the full 4-part assembly version (e.g. `1.0.0.2`) instead of being truncated to 3 parts.
- `release.ps1` summary and `docs/intune-distribution.md` updated to reflect the system-context Intune configuration (install behavior **System**, paths under `%ProgramFiles%\TDPdf`).

## [1.0.0.1] - 2026-05-18

### Changed

- New TDPdf crest logo across the app icon, favicon, and landing-page header.
- Reworked release tooling for internal Intune distribution: `release.ps1` now supports PFX signing with a secure password prompt and packages the signed EXE into a `.intunewin` via `IntuneWinAppUtil.exe`. The app gained `/install [/silent]` and `/uninstall [/silent]` CLI flags so Intune can drive install and uninstall headlessly; `QuietUninstallString` is registered alongside the interactive `UninstallString`.

### Documentation

- Replaced `docs/code-signing.md` with `docs/intune-distribution.md` covering the end-to-end Intune Win32 packaging, signing, deployment, and update workflow.

## [1.0.0-tdpdf] - 2026-05-18

First release under the **TDPdf** identity, maintained by **The Doodle Project, LLC**. Combines the rename of the upstream `KillerPDF` codebase with the body of fork work done since (upstream sync through KillerPDF v1.4.0, viewer + annotation polish, snapshot-based undo/redo, native Windows shell, and more).

### Added

- **`NOTICE` file** at the repo root with the GPLv3 §5(a) modification notice and full attribution to the upstream project. README prominently links to upstream and `NOTICE`.
- **Real menu bar** (File / Edit / View / Tools / Help) with full Alt-mnemonic support and `InputGestureText` shortcut hints next to each item.
- **About TDPdf** dialog (Help → About, or F1) showing version, license, and upstream fork attribution.
- **Standard keyboard shortcuts** wired through `Window.InputBindings`: Ctrl+N (new), Ctrl+W (close file), Ctrl+Z (undo), Ctrl+Y / Ctrl+Shift+Z (redo), Ctrl+Shift+S (save as), F1 (about). Existing Ctrl+O / Ctrl+S / Ctrl+P / Ctrl+F continue to work.
- **TdpDialog** keyboard polish: Enter activates the default button, Esc cancels (or dismisses OK-only dialogs). The default button is auto-focused on open. `MessageBoxImage` is now rendered as a Segoe MDL2 Assets glyph (Information, Warning, Error, Question) in the left column of the message body.
- **Edit → Redo** (`Ctrl+Y` / `Ctrl+Shift+Z`) — paired with the snapshot-based undo overhaul below.
- **Viewer + annotation polish, addressing [Reddit/KillerPDF community feedback](https://www.reddit.com/r/KillerPDF/):**
  - **Pan / hand tool** — drag the page in any direction. Middle-mouse drag also pans regardless of the active tool.
  - **Shift + scroll wheel** scrolls horizontally; **horizontal tilt-wheel** (left/right) on multi-axis mice now scrolls horizontally via a `WM_MOUSEHWHEEL` hook.
  - **Eraser tool** — click any annotation (highlight, ink, shape, text, signature, image) to delete it.
  - **Shape tool** — drag-to-create rectangles, ellipses, and lines with a settings bar for stroke color, fill color (toggleable), and stroke width. Shapes are persisted into the PDF on save.
  - **Edit existing annotations with the Select tool** — drag highlights, shapes, ink, and text to move them. Drag the new bottom-right green resize handle to scale highlights, shapes, and ink (ink stroke width scales proportionally).
  - **Zoom to Width / Zoom to Page now re-fit on window resize** — if either mode is active, resizing the window re-applies the fit. Picking an explicit zoom level or using Zoom In/Out/Reset disables the auto-fit.
  - **Signature auto-selects after placement** — the Select tool activates automatically with the new signature highlighted, so you can immediately drag the corner handle to resize or press Delete.
  - **Insert Blank Page now opens a page-size dialog** — choose "Same as current page", Letter, Legal, Tabloid, A3, A4, or A5, plus Portrait or Landscape, instead of always inserting A4.
- **Upstream sync (KillerPDF v1.4.0):**
  - Rotate page (upstream Issue #52). Right-click any page in the sidebar to rotate it 90° clockwise or counter-clockwise. Works on multi-page selections.
  - Insert Image tool (upstream Issue #50). Click the toolbar button, then click anywhere on the page to place a PNG, JPG, BMP, GIF, or TIFF as a resizable annotation. Drag the green corner handle to resize; burned into the PDF on save.
  - PDF link annotation support (upstream Issue #47). Clicking hyperlinks and internal cross-references in a PDF now navigates to the target page or opens the URL in the default browser. Works on both the primary page and all secondary pages in multi-page grid view.
  - New Blank Document (Ctrl+N, toolbar button). Creates a single blank A4 page as a new working document. Prompts to discard unsaved changes if a dirty file is open.
  - Typewriter tool font size picker. When the Text tool is active, a settings bar appears showing size presets (8–72pt) and a color palette. Size and color are stored per-annotation and applied when flattening to PDF.
  - Insert Blank Page. Right-clicking any page in the sidebar now shows a context menu with page-level operations: insert a blank A4 page, move up/down, extract, or delete.
  - Signature resize. Placed signatures now show a green drag handle in the bottom-right corner. Dragging it scales the signature proportionally; releasing commits the new size.
  - Multi-page grid view. When viewing a page, subsequent pages render as a tiled grid to the right and below, allowing context across multiple pages at once.
  - Fit to Width on open. Files now auto-zoom to fill the viewer width on open instead of opening at 100% and clipping wide pages.

### Changed

- **Renamed product, package, and namespace from `KillerPDF` to `TDPdf`.** Versioning resets to 1.0.0 under the new identity.
- Assembly, EXE name, install directory, ProgID, registry keys, Add/Remove Programs entry, and uninstall batch all migrate to `TDPdf` / `TDPdf.pdf`. Existing KillerPDF installations are unaffected by the new build; they continue to work in place.
- Publisher metadata in the Add/Remove Programs entry now reads `The Doodle Project`.
- All in-app dialog titles, automation strings, and wordmark updated to `TDPdf`. The header wordmark renders as `TD` + accent-green `Pdf`.
- Application icon replaced with a placeholder `Resources/tdpdf-icon.ico` (multi-resolution dark/green TD glyph).
- Footer hyperlink and crash-report header updated to The Doodle Project.
- **Native Windows frame is now the default** for new installs (`Settings.UseNativeWindowFrame` default flipped from `False` to `True`). Existing users keep whatever they had configured. The native frame matches Windows 11 chrome, DWM-managed window snapping, and Aero Snap. Custom dark chrome remains available in Settings.
- `OnPreviewKeyDown` no longer swallows global Alt+letter or duplicated Ctrl shortcuts; menu accelerators and `InputBindings` own those paths. Context-sensitive keys (Ctrl+C/Ctrl+A inside text selection, Esc to dismiss search, Delete for annotation removal) still flow through the preview hook.
- `ResizeMode` is now applied after `InitializeComponent` so the custom-chrome resize grip and the native-frame standard resize border each look correct.
- Theme refresh: accent green updated to `#1ea54c`, backgrounds shifted to `#333333`/`#3a3a3a`, sidebar darkened to `#222222`, toolbar and title bar at `#222222`. Film grain overlay added to the main content area. Footer text lightened for readability.
- Sidebar scroll is now handled by an outer ScrollViewer wrapping the page list, allowing the list to size to its content rather than stretching to fill the panel height.

### Fixed

- **Snapshot-based undo** ([#50](https://github.com/doodlemania2/TDPdf/issues/50)) — `Ctrl+Z` now reverses move, resize, erase, image-edit replace/delete/reset, and inline text edits on annotations, not just additions. Each mutating gesture pushes a deep-cloned page snapshot before mutation; no-op clicks are dropped from the stack. A symmetric redo stack (capped at 100 entries each) is populated on undo. Document-level operations (crop / insert page / delete page / reorder / rotate) remain a hard history barrier — page snapshots are cleared when a `Document` entry is undone so they can't be restored onto an out-of-date page layout.
- Scroll wheel in the main viewer no longer triggers page navigation. Previously, at low zoom levels where the page fit entirely in the viewport, every scroll tick caused a full page re-render.
- Page selection no longer flashes centered before jerking left. The layout width is now managed exclusively in the Dispatcher callback, eliminating the double layout pass that caused the visual artifact.
- "Back to TOC" and other internal links on secondary pages now navigate to the correct target instead of advancing to the next sequential page.
- Clicking an internal link now scrolls the viewer back to the top of the target page so links pointing to page tops (e.g. TOC back-links) land correctly.
- Internal PDF links now survive a merge. When merging PDFs, named destinations from the source document's catalog are resolved and rewritten as explicit page-object references in the merged document, so TOC and cross-reference links continue to work after merging.
- Multi-page grid content is now centered in the viewport instead of left-aligned. Panel width is snapped to a whole number of page-width slots so HorizontalAlignment=Center has room to work.
- Sidebar page list no longer shows empty space after the last page. The list now ends at the final page entry with no trailing dead zone.

### Removed

- Upstream author personal branding ("Steve the Killer", `thekiller.net` and `killertools.net` links) stripped from the UI, landing page, footer, and installer dialogs as required by GPLv3 §7(e) for modified versions.
- Upstream-specific landing-page assets (badges, og-image, screenshots) and Umami analytics script removed.

## [1.3.2] - 2026-05-11

### Fixed
- Windows Program Compatibility Assistant popup on first launch. Added an app manifest declaring Windows 10/11 compatibility, which suppresses PCA when the app writes to uninstall registry keys.
- "Set as default PDF viewer" prompt now only appears if the app is not already the default handler. Previously showed on every install/update regardless.
- "Set as default PDF viewer" prompt now uses the dark KillerDialog instead of a native Windows message box.

## [1.3.1] - 2026-05-11

### Fixed
- Print no longer fails with "No application is associated with the specified file for this action" on systems where Edge is the default PDF handler. Printing now uses WPF-native rendering and PrintDialog instead of the shell print verb.
- Zoom dropdown selected value no longer shows in blue - selection highlight now uses the accent green.

## [1.3.0] - 2026-05-08

### Added
- Image signatures. Import a PNG, JPG, or BMP as a reusable signature instead of drawing one. Stored alongside drawn signatures and flattens into the PDF on save.
- Close File (Ctrl+W). Close the current document without quitting the app. Prompts if there are unsaved changes.
- Unsaved-changes protection. The title bar marks dirty files with `*` and prompts before closing or opening a new file with unsaved edits.
- Full-document Find. Ctrl+F search now scans the entire PDF and cycles through all matches, not just the current page.
- Zoom preset dropdown with quick presets (50%, 75%, 100%, 125%, 150%, 200%). Scroll-wheel zoom syncs the box, including non-preset levels.

### Fixed
- Scrolling past the bottom of a page now advances to the next page; scrolling past the top goes back.
- Re-dropping a PDF onto the window after a file is already open now works correctly.
- Owner-password-protected PDFs now open correctly (previously only user-password was handled).
- Dragging the title bar while maximized now correctly restores and moves the window.
- Delete confirmation now reads "Delete 1 page?" or "Delete 2 pages?" instead of "Delete N page(s)?".
- Signature delete button showed a rectangle glyph instead of an X.

### Changed
- All dialog boxes are now fully dark-themed via a custom dialog window. No more native Windows popups.
- Create Signature dialog now uses a dark custom chrome title bar with a red X close button.
- Button hover states and page thumbnail hover in the sidebar are now green instead of the default Windows blue.
- Toolbar icons overhauled: Open Folder, Close File, Move Up, Move Down, Extract Pages, and Merge PDFs all use cleaner glyphs.

## [1.2.1] - 2026-05-04

### Changed
- Code signed with Certum certificate. Windows now shows a verified publisher instead of unknown.
- Cleaned up footer.

## [1.2.0] - 2026-04-24

### Added
- Self-installing EXE. Running the downloaded binary now shows an Install / Run dialog. Install copies the EXE into `%LOCALAPPDATA%\Programs\<AppName>\` (no UAC required), creates Start Menu and optional Desktop shortcuts, registers as a PDF file handler, and adds an uninstall entry to Add/Remove Programs. Uninstall self-deletes via a deferred batch file. Running a newer version from outside the install path shows an Update prompt instead.
- Command-line file argument support so file associations work: passing a path opens the file directly.
- Password-protected PDF support. Opening an encrypted PDF now prompts for the password instead of showing a generic error. The decrypted copy is held in a temp file for the session so all rendering and editing works normally.
- Save Flattened PDF (photo icon in toolbar). Rasterizes every page at 150 DPI via PDFium and writes them as embedded images into a new PDF, producing a fully uneditable document. Pending annotations are burned in before rasterization.

## [1.1.1 (KillerPDF)] - 2026-04-18

### Fixed
- Maximize no longer covers the Windows taskbar. Added a `WM_GETMINMAXINFO` hook so the frameless window clamps to the monitor's work area (multi-monitor aware).
- Two `CS8602` nullability warnings in the font-name cleanup path.

## [1.1.0 (KillerPDF)] - 2026-04-16

### Changed
- Retargeted from .NET 8 to .NET Framework 4.8 so end users no longer need to install a separate .NET runtime.
- Forced 64-bit build via `PlatformTarget=x64`.
- Added PolySharp polyfills for modern C# language features on net48.
- Replaced `Math.Clamp` calls with `Math.Min`/`Math.Max` equivalents.

### Added
- Post-publish MSBuild target that automatically bundles a GPL3-compliant source zip alongside the published EXE.
- CHANGELOG.md.

## [1.0.1]

_Historical entries to be backfilled._

[Unreleased]: https://github.com/doodlemania2/TDPdf/compare/v1.15.0.0...HEAD
[1.15.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.14.0.0...v1.15.0.0
[1.14.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.13.0.0...v1.14.0.0
[1.13.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.12.0.0...v1.13.0.0
[1.12.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.11.0.0...v1.12.0.0
[1.11.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.10.0.0...v1.11.0.0
[1.10.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.9.2.0...v1.10.0.0
[1.9.2.0]: https://github.com/doodlemania2/TDPdf/compare/v1.9.1.0...v1.9.2.0
[1.9.1.0]: https://github.com/doodlemania2/TDPdf/compare/v1.9.0.0...v1.9.1.0
[1.8.3.0]: https://github.com/doodlemania2/TDPdf/compare/v1.8.2.0...v1.8.3.0
[1.8.2.0]: https://github.com/doodlemania2/TDPdf/compare/v1.8.1.0...v1.8.2.0
[1.8.1.0]: https://github.com/doodlemania2/TDPdf/compare/v1.8.0.0...v1.8.1.0
[1.9.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.8.3.0...v1.9.0.0
[1.8.3.0]: https://github.com/doodlemania2/TDPdf/compare/v1.8.0.0...v1.8.3.0
[1.8.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.3.1.0...v1.8.0.0
[1.3.1.0]: https://github.com/doodlemania2/TDPdf/compare/v1.3.0.0...v1.3.1.0
[1.3.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.2.0.0...v1.3.0.0
[1.2.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.1.0.1...v1.2.0.0
[1.1.0.1]: https://github.com/doodlemania2/TDPdf/compare/v1.1.0.0...v1.1.0.1
[1.0.0.6]: https://github.com/doodlemania2/TDPdf/compare/v1.0.0.5...v1.0.0.6
[1.0.0.5]: https://github.com/doodlemania2/TDPdf/compare/v1.0.0.4...v1.0.0.5
[1.0.0.4]: https://github.com/doodlemania2/TDPdf/compare/v1.0.0.3...v1.0.0.4
[1.0.0.3]: https://github.com/doodlemania2/TDPdf/compare/v1.0.0.2...v1.0.0.3
[1.0.0.2]: https://github.com/doodlemania2/TDPdf/compare/v1.0.0.1...v1.0.0.2
[1.0.0.1]: https://github.com/doodlemania2/TDPdf/compare/v1.0.0-tdpdf...v1.0.0.1
[1.0.0-tdpdf]: https://github.com/doodlemania2/TDPdf/releases/tag/v1.0.0-tdpdf
[1.3.2]: https://github.com/SteveTheKiller/KillerPDF/compare/v1.3.1...v1.3.2
[1.3.1]: https://github.com/SteveTheKiller/KillerPDF/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/SteveTheKiller/KillerPDF/compare/v1.2.1...v1.3.0
[1.2.1]: https://github.com/SteveTheKiller/KillerPDF/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/SteveTheKiller/KillerPDF/compare/v1.1.1...v1.2.0
[1.1.1 (KillerPDF)]: https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.1.1
[1.1.0 (KillerPDF)]: https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.1.0
[1.0.1]: https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.0.1
