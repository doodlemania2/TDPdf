# Changelog

All notable changes to TDPdf are documented here. TDPdf is a fork of [SteveTheKiller/KillerPDF](https://github.com/SteveTheKiller/KillerPDF); entries from before the rename describe upstream history continued under the previous name.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.26.0.0] - 2026-08-27

**The zoom storm is over, text boxes can be styled, and page reordering works the way the list always implied it did.**

### Fixed

- **The repeated re-rendering behind [#132](https://github.com/doodlemania2/TDPdf/issues/132) is fixed, and the cause was a comment.** A handler sat on WPF's `DpiChanged` event under a note asserting it never fires — that our own `WM_DPICHANGED` hook preempts it, leaving harmless dead code. It fires. The `Zoom.Churn` diagnostic added in 1.25.0.0 named it on the first report back and then again from a second, unrelated machine: **13 full zoom passes a second, held for two seconds**, each one cancelling and restarting a page render, re-fitting the view and writing `user.config` to disk. The handler stays — it demonstrably is a live DPI path on some machines — but now acts only when the DPI has actually changed, which is what makes "idempotent" true rather than hoped for.

### Added

- **Bold, italic and underline in text boxes** — Ctrl+B, Ctrl+I, Ctrl+U while editing. They were listed in the shortcut overlay and did nothing, which is exactly what upstream found in their own build. The whole path is new: text annotations carried a size and a colour and nothing else. Everything that measures text — the on-screen box, the annotation measurement, the wrap calculation and the PDF burn-in — now shares one typeface, because bold and italic change glyph advances and a mismatch means the text moves when you save. Underline is drawn explicitly: `XFontStyle.Underline` exists but PdfSharpCore's `DrawString` ignores it, so an underlined note would have looked right and saved without the line. Existing annotations are untouched — all three default to off. (Ported from upstream KillerPDF v1.7.5.)
- **Drag a selection of pages as one block.** The Pages sidebar has allowed multi-select all along, and dragging then moved exactly one page. It now moves the whole selection, in order, with each page's rotation, and leaves the block selected where it lands. (Ported from upstream KillerPDF #233.)
- **An insertion line while dragging pages**, showing precisely which gap the pages will drop into instead of asking you to guess from the cursor. The line and the reorder read the same target calculation, so the indicator cannot point somewhere the pages do not go.

### Changed

- **Dropping a block of pages back where it started is now a no-op.** It used to rewrite and reload the document anyway, which discards unsaved annotations — the documented cost of any structural edit — in exchange for no change at all.

### Fork-sync

The remaining portable items from upstream's `develop/1.8.0` were assessed and two are deliberately still out, with reasons recorded in [#135](https://github.com/doodlemania2/TDPdf/issues/135): **letter spacing** cannot be previewed honestly, because WPF has no letter-spacing primitive and a burn-in-only implementation would move the text at the moment you save it — the exact failure the shared-typeface work above exists to prevent; and the **measurement tool** needs a toolbar glyph that cannot be verified from a non-Windows build machine, and a blank button is worse than a missing feature. Two other upstream items turned out not to apply here at all: TDPdf already switches to a drag cursor while panning, and it has no page-duplication command to fix the selection of.

## [1.25.0.0] - 2026-08-27

**TDPdf now notices when a new release exists, and on a managed device asks Intune to come and get it.** Yesterday a machine was found still running 1.23.5.0, six releases behind, whose user placed 32 text boxes in 28 minutes and got nothing from any of them — every one of those releases had fixed the defect they were hitting, and none had reached them. Nothing on that machine was broken. It simply had not been told.

### Added

- **An update check, twice a day.** TDPdf asks GitHub whether a newer release exists and, on an enrolled device, runs the enrollment client's own sync task — the same thing the Company Portal's **Sync** button does — so Intune fetches the update rather than waiting up to eight hours for its next check-in. **It never downloads and never installs:** on a managed device the update path is Intune's and stays Intune's, so the fleet keeps one delivery mechanism, one audit trail and one set of assignments. The request carries no identifier and not even the installed version — the comparison happens on the device. Disclosed in `PRIVACY.md`, and an administrator can switch it off fleet-wide with `Enabled = 0` under `SOFTWARE\Policies\TDPdf\Update`.
- **The status line says when an update has landed underneath you.** An update can now install while TDPdf is open, which means someone can be working in a build that has quietly been replaced. That is an update that landed rather than an update that arrived, so TDPdf says so.

### Fixed

- **Ctrl+S — and Open, Save As, New, Close, Print, Find and About — now work while you are typing in a text box.** All nine were suppressed whenever an annotation text box had focus, so typing an annotation and reaching for Ctrl+S did nothing at all, in silence. Nobody could have noticed before now: until 1.24.1.0 the editor was destroyed a few hundred milliseconds after it appeared, and nobody was ever typing in one long enough to reach for a shortcut. Undo keeps its guard, and is the only one that should ever have had it — Ctrl+Z inside a text box means "undo my typing", and letting the document handler win would silently revert an annotation, a crop or a page rotation while you believed you were correcting a word. (Ported from upstream KillerPDF #237.)
- **Installing no longer fails because TDPdf is running.** Windows refuses to overwrite an executing file but permits renaming one, so the installer now displaces the running image and drops the new build into place. This had crashed the installer five times across four consecutive releases, always the same `IOException`, on a first-run path where the user sees the app die rather than install. It also tolerates several updates landing without a restart in between.
- **PDFs whose file name or password contains characters outside the system code page now open.** Direct PDFium loading marshalled both through the machine's ANSI code page where PDFium expects UTF-8, so anything the local code page could not represent was corrupted before PDFium saw it and the document silently failed to load. (Ported from upstream KillerPDF.)
- **`Zoom.Churn` can now name the caller responsible.** The diagnostic added in 1.24.1.0 reported `.ctor` on its first production sighting, which is what both the DPI-change handler and an untouched startup zoom produce — opposite findings that the event could not tell apart. Both are named explicitly now. See [#132](https://github.com/doodlemania2/TDPdf/issues/132).

### Fork-sync

Reviewed against upstream KillerPDF `develop/1.8.0` and **deliberately not adopted**. That branch is unreleased, and its defining change is replacing PdfSharpCore with a bespoke document engine on .NET 10 — nearly every entry in its *Changed* section reads "now goes through The KillerPDF.Engine". That is the library swap this project's architecture notes rule out, and adopting it is a fork-defining decision rather than a sync. The two fixes above are what the review was worth: both are upstream's, both were live bugs here, and neither touches their engine. The features that remain genuinely portable — letter spacing, bold/italic/underline while editing, the measurement tool, multi-select thumbnail drag — are tracked as issues.

## [1.24.1.0] - 2026-08-27

**Text boxes work.** Every live text editor in the fleet was being destroyed before anyone could type into it, and had been for five releases. The cause was never focus.

### Fixed

- **A zoom no longer tears down the text box you just placed.** `ApplyZoom` reached the app's commit chokepoint, and it repeats — roughly 23 times a second, sustained. 54 of 54 editor destructions across the fleet arrived from there, and the 400 ms grace window added in 1.23.7.0 only moved every death to 409-456 ms; the next tick took the editor anyway. **The commit was never needed.** The page tile and both annotation canvases are sized from one fixed render box, and the zoom is an ancestor layout transform, so a live box's coordinates — and the annotation it becomes — are identical at every zoom level. Upstream KillerPDF has never settled the editor on an automatic path either. Removed, along with `_applyingFitZoom`, 1.23.7.0's attempt to exempt only the automatic re-fits (the telemetry shows it reading false on all 54 anyway).

  For the record, since four releases were spent on the wrong theory: `Annotation.TextEditorFocusLost` and `Annotation.TextEditorFocusRestored` have fired **zero** times in fleet history, and `Annotation.PlaceCompleted` reports `Attached=true, Focused=true` every time. The editor was being destroyed *while focused*. The single successful text annotation ever recorded came from a user who got a keystroke in 29 ms and won the race.

- **The zoom dropdown no longer reads its own updates back as your input.** The combo box's selection is two-way bound to the view model, which rewrites it on every zoom change from every source — so each zoom the app computed echoed back into the "user picked a zoom" handler, which cancelled whatever fit mode you had chosen and applied a zoom nobody asked for. A selection that asks for the zoom already in force is now recognised as the echo it is.

- **Pinch-to-zoom now stops the view tracking the window,** as Ctrl+wheel, the zoom buttons and Ctrl+0/1 already did. It never did so on its own — it only reached that code by accident, through the zoom box reading its own update back as a user pick, and then only when the pinch happened to land on a preset. Like every other explicit zoom, a pinch now also retires a remembered Fit Width / Fit Page.

- **A computed fit is no longer snapped to the nearest preset.** The view model matched a preset within 0.5% but wrote one back if it differed by more than 0.01%, so any Fit Width or Fit Page landing near 100% was quietly forced to exactly 100% — changing the fit you asked for, and re-entering the zoom pipeline to do it. One tolerance now.

### Added

- **`Zoom.Churn` telemetry.** The repeated zoom passes were only ever visible because a text editor happened to be alive while they ran, and that signal disappears with the fix above. The viewport now reports directly when it re-applies its zoom 8 or more times a second **and keeps doing so for at least two seconds** — the sustain requirement is what separates the defect from a sidebar animation or a window-edge drag, both of which clear that rate by design. It names the method that originated the zooms, which is a frame above the handler they all funnel through; naming the handler would have reported the same value for every cause and taught the next investigation nothing. Rate-limited to one report every 15 minutes; carries method and enum names only, and cannot reference a document. Disclosed in `PRIVACY.md`, along with `Annotation.TextEditorCommitDeferred` — that one shipped in 1.23.7.0 and was left out of the privacy policy at the time. The repetition itself is a separate defect and is tracked in [#132](https://github.com/doodlemania2/TDPdf/issues/132).

## [1.24.0.0] - 2026-08-26

**Application Insights is removed. The OTLP collector is now the only telemetry destination, and no destination is compiled into the binary at all.**

Fourteen days of dual export settled the question. Azure received a lossy ~17% subset — every install and heartbeat, none of the interaction events — while the collector carried the full stream, including the evidence that identified the 1.23.x text-editor defect. Keeping a second destination that saw less and cost more had no case left.

### Removed

- **The `Microsoft.ApplicationInsights` dependency**, the `TelemetryClient` fan-out, and every Azure-specific type in `Diagnostics/`.
- **The build-time-embedded key.** `build/embed-telemetry-key.ps1`, `Diagnostics/EmbeddedTelemetry*.cs`, the conditional `<Compile Remove>` in the project file, the `TDPDF_APPINSIGHTS_CONN` secret, and the embed/cleanup steps in `release.yml` and `release.ps1` are all gone. This obfuscated a secret inside a binary shipped to end-user laptops — a speed bump rather than a control — and could not be rotated without a full release, which is why it never was rotated. **No destination is compiled into the binary now; rotation is always a policy push.**
- **`TDPdf.exe /set-telemetry` and the DPAPI store** at `%ProgramData%\TDPdf\telemetry.dat`. There is no longer a secret for an operator to pipe in by hand. `/clear-telemetry` stays and is unchanged.
- **The `ConnectionString` policy value.** The Intune remediation now deletes it rather than writing it — a live Azure ingestion secret sitting unread in the registry on every managed device is worth one line to clean up.

### Added

- **`host.name` on every record.** The classic Azure SDK populated the machine name by default; the OpenTelemetry default resource does not, so it is now set explicitly. Without it this release would have silently lost the ability to say *which* device is failing — the capability that let issue #124 name a specific machine. Disclosed under "Device name" in `PRIVACY.md`.
- **`crash.replayed` and `crash.time_utc`** on spooled crash records. The `Replayed` marker previously rode only the Application Insights event, so removing that destination would have dropped it. It matters because the resource attributes — `session.id` above all — describe the session doing the *reporting*, not the one that died; without the marker, "crashes per session" blames an old crash on a healthy launch.

### Changed

- **Upgrading deletes any legacy `telemetry.dat`,** on launch and again on the elevated `/install` path. It holds a DPAPI-encrypted connection string that nothing reads any more, and a dead secret should not outlive its purpose on 30 laptops.
- `PRIVACY.md`, `NOTICE`, `README.md` and `docs/intune-distribution.md` rewritten for a single OTLP destination. The Intune guide loses ~230 lines of Azure setup, embedded-key and file-provisioning procedure that no longer describe anything real.

### Notes

- **Nothing is deleted on the Azure side by this release.** The `ai-tdpdf-prod` resource keeps its data and will simply stop receiving; retiring it is a separate, deliberate action.

## [1.23.7.0] - 2026-08-26

**Fixes Insert Text Box discarding the editor a frame after it appears — the actual cause, after four releases of treating it as a focus bug.**

### Fixed

- **An automatic re-fit no longer discards the text box you just placed.** Production traces show the editor being created, attached and focused correctly, then committed as empty 15–140 ms later without a keystroke and without losing focus. Focus was never broken; a layout-driven caller was reaching the shared "settle any in-progress edit" chokepoint one frame after placement. `FitToWidth` / `FitToPage` run from a `SizeChanged` continuation whenever anything perturbs the viewport — including adding the text box itself — and every re-fit committed the live editor. Automatic re-fits now leave it alone, using the `_applyingFitZoom` marker that already existed for exactly this distinction but which nothing read.
- **A brand-new, untouched text box can no longer be torn down before you can type in it.** An editor that is empty, has never received input and is not a re-edit is now protected from an incidental commit for a short grace period, whatever the caller. A re-edit still commits immediately, because emptying that box means "delete this annotation".

### Changed

- **`Annotation.TextEditorClosed` now records `Via`, the method that requested the commit**, alongside the existing outcome. A new `Annotation.TextEditorCommitDeferred` event records commits refused by the grace window. Both carry a compile-time method name only. Diagnosing this failure previously required guessing which of twenty-one callers ran; it is now a single field in telemetry.

### Notes

- The build warning baseline drops from 6 to 4: wiring `_applyingFitZoom` to its intended purpose retires the `CS0414` "assigned but never used" warning it had been raising in both project passes.

## [1.23.6.0] - 2026-08-26

**A curated fixes-only sync with upstream KillerPDF v1.7.4/v1.7.5, plus the remaining Insert Text Box activation repair.**

### Fixed

- **Every dynamic text editor now gets an unconditional activation attempt.** TDPdf attached its focus callback only to WPF's `Loaded` event. A dynamically-added TextBox can already be loaded before that handler is attached, so the callback never ran and the editor silently received no keyboard input. The editor lifecycle is now wired before the TextBox enters the visual tree, and an idempotent dispatcher fallback runs whether `Loaded` fires before or after registration. This follows the upstream KillerPDF fix for the same “silently took no typing” failure.
- **Lost-focus handling is attached independently of `Loaded`.** Even if WPF's loaded event has already passed, the editor now has complete commit/cancel lifecycle handling.
- **Large or invalid print ranges cannot freeze TDPdf or silently widen to the whole document** (upstream KillerPDF v1.7.4, PRs #220/#222). Range endpoints are clamped before iteration, so `1-2147483647` cannot overflow into an endless loop; a range matching no pages now produces an empty preview with Print disabled instead of printing every page.
- **Printing stays responsive through composition and spooling** (upstream KillerPDF v1.7.4, PR #228). The existing 300-DPI raster pass already ran in the background; sheet composition and the XPS spool operation now run on a dedicated STA print thread as well, while the progress overlay remains responsive.
- **An in-flight print job cannot be triggered twice.** Print stays disabled while rasterization and spooling are active even if keyboard input behind the overlay refreshes the preview (upstream KillerPDF v1.7.4).
- **Rotating pages preserves unsaved annotations and keeps them reachable** (upstream KillerPDF v1.7.4/v1.7.5, #169). Highlights, line markup, ink, shapes, placed text, signatures, images, existing-content edits, and crop geometry are remapped through the turn. Upright text and placed items follow the sheet by their center and are clamped within the rotated page bounds.
- **Transform includes a text box that is still being edited** (upstream KillerPDF v1.7.5). The live editor is committed before the preview and full-resolution transformed page are built.

### Notes

- Already present and therefore not re-ported: page-image export, Shift+mouse-wheel horizontal scrolling, toolbar hiding, the grid page badge, comma-decimal form appearances, duplicate form-field output repair, damaged-drop repair, and startup file-forwarding safety.
- Intentionally skipped: localization, themes, split-pane behavior, upstream packaging/install changes, branding/landing assets, and cosmetic-only toolbar/badge changes.

## [1.23.5.0] - 2026-08-25

**Fixes closing a changed document appearing to hang while background page rendering continued.**

### Fixed

- **Close Without Saving now stops every render pipeline before releasing the document.** Closing previously canceled only the primary-page render while grid, continuous-view, re-sharpen, and window-maintenance PDFium workers could continue rasterizing 17–25 pages. The close path then forced a synchronous full garbage collection while those workers were still allocating bitmaps, making the accepted close appear frozen and sustaining substantial CPU use.
- **Released bitmap memory is collected without blocking the UI.** The synchronous `GC.Collect()` on every tab close is replaced by one coalesced, optimized, non-blocking collection at application-idle priority.
- **Application exit no longer waits indefinitely for OpenTelemetry exporters to dispose.** Exporter shutdown now has a two-second bound after the existing bounded flush.
- **Switching tabs cancels every render pipeline tied to the previous document.** Stale PDFium work can no longer keep using CPU or marshal old page bitmaps back to the shared UI after a tab switch.
- **The unsaved-changes choice is explicit.** Dirty file and application prompts now say **Close Without Saving** and **Cancel** instead of the ambiguous **Yes** and **No**, where choosing No intentionally left the file open and looked like the close command had failed.

## [1.23.4.0] - 2026-08-25

**A second focused follow-up for Insert Text Box.** A Windows smoke test confirmed that Signature and Draw worked in 1.23.3.0 while the text editor still did not accept input.

### Fixed

- **Clicking the active text box now explicitly restores keyboard focus.** The 1.23.3.0 fallback correctly recognized clicks within the editor's bounds but only swallowed the underlying annotation-canvas event, leaving an unfocused editor unusable. The fallback now focuses the editor and places the caret at the clicked character.
- **Initial text-editor focus waits until queued mouse input has completed.** The deferred focus callback now runs at dispatcher context-idle priority instead of competing with the mouse event that created the editor. Direct clicks on the editor also explicitly reaffirm focus.

### Changed

- Privacy-safe telemetry now distinguishes an editor that merely loaded from one that actually received its first text change, and records whether an in-bounds focus recovery succeeded. It still records no typed text, page number, filename, or document content.

## [1.23.3.0] - 2026-08-25

**A focused follow-up for Insert Text Box.** Version 1.23.2.0 stopped the layout-race crash, but production telemetry proved that one user still could not get a usable text editor.

### Fixed

- **The live text editor can no longer disappear during a normal page refresh.** It previously lived inside `AnnotationCanvas`, whose persistent annotations and PDF overlays are routinely rebuilt with `Children.Clear()`. The TextBox could reach WPF's `Loaded` event and then be detached by a render refresh before the user saw it—the exact pattern recorded in production, where one machine created thirteen loaded editors in seconds but could not type into any of them. Live placed-text and existing-PDF-text editors now use a dedicated topmost `SafeCanvas` that annotation rendering never clears.
- **Text editing takes keyboard focus after the creating mouse event has finished.** All text-editor paths now share one deferred focus routine, rather than requesting focus synchronously from `Loaded` while WPF is still completing input and layout. Clicking inside the active editor is also protected by its actual bounds, placement near a page edge stays visible, and stale lost-focus events cannot commit a newer editor.

### Changed

- Privacy-safe text-editor telemetry now records whether the editor remained attached, received focus, and ended committed, canceled, deleted, or empty. It still records no typed text, page number, filename, signature data, or document details.

## [1.23.2.0] - 2026-08-24

**A second layout-race hotfix for 1.23.x.** Upgrade from 1.23.0.0 or 1.23.1.0 as soon as it reaches you.

### Fixed

- **Insert Signature no longer closes the application, and Insert Text Box no longer appears to do nothing.** Production telemetry showed both failures recurring on 1.23.1.0. The WPF runtime can omit its internal `VisualCollection` frame from an optimized stack trace; the safety check added in 1.22.1.0 and repaired in 1.23.1.0 still required that frame, so it could reject the exact `Canvas.MeasureOverride` / `WrapPanel.MeasureOverride` race it exists to recover from. The guards now inspect real stack frames and accept the panel's own layout method with or without the omitted collection frame. They remain deliberately narrow: an exception raised by a child control is not swallowed.

### Changed

- Text-box and signature placement now emit start/completion telemetry breadcrumbs containing only the annotation type. They contain no text, signature data, page number, filename, or document details, and make a future interrupted placement distinguishable from a user merely selecting a tool.

## [1.23.1.0] - 2026-08-21

**A regression introduced in 1.23.0.0.** Upgrade from 1.23.0.0 as soon as it reaches you.

### Fixed

- **The page-grid crash guard stopped guarding, and the crash it was written for came back.** `SafeWrapPanel` has protected the page grid since 1.8.1.0 against a WPF race where the panel's child collection is indexed while it is being changed. 1.23.0.0 rewrote the shared predicate that decides whether a given exception is that race, and narrowed it in a way that no longer recognises it — so the guard passed the crash straight through and the application closed. One user hit it fourteen times before it was caught.

  The cause is worth recording. The predicate asked the exception where it was thrown, via `TargetSite`, and only consulted the stack trace when that was unavailable. On .NET 8 and later the runtime raises this particular error through a *throw helper*, so `TargetSite` reports `System.ArgumentOutOfRangeException` — technically non-null, and nothing to do with the collection — which made the check answer "not that race" and skip the stack-trace test that matches it exactly. The two signals are now independent: either can confirm the race, and neither can veto the other.

  Nothing else about 1.23.0.0 is affected, and no telemetry, consent or privacy behaviour changed.

## [1.23.0.0] - 2026-08-20

Telemetry becomes something you can see, control and switch off — and starts reporting to the parish's own self-hosted collector alongside Application Insights. Groundwork for open-sourcing this repository.

### Added

- **A telemetry setting, on by default, in Settings → Privacy.** Reporting now needs *two* things: your consent, and a configured destination. Either alone sends nothing. That split is what makes the default honest rather than sneaky — a build compiled from a public checkout has no destination configured anywhere, so it reports to nobody whichever way the setting is set. The dialog says which of the two states your machine is actually in, rather than letting you believe you have switched off something that was never on. Turning it off takes effect immediately, flushing first so reports already recorded in good faith are not dropped by the act of opting out.
- **[`PRIVACY.md`](PRIVACY.md)** — what is collected, what never is, where it is stored on your machine, and how to turn it off. Enumerated from the code rather than written as boilerplate, including the two fields a careful reader would ask about: reports carry the **machine name**, and a **session identifier** that is regenerated every launch and never written to disk.
- **Export to the self-hosted OTLP collector**, running alongside Application Insights. Neither destination knows about the other and each is resolved independently, so retiring either one later is a configuration change rather than a rewrite. Completed operations are exported as spans, so latency percentiles are computed from real data instead of pre-aggregated numbers.
- **Telemetry survives being offline.** Two mechanisms, because "telemetry is down" hides two different failures. Reports whose upload fails are queued on disk and sent when connectivity returns — this matters far more on laptops, where offline and captive-portal wifi are the normal state rather than an incident. And crash reports are written to disk *as they are raised*, then replayed at the next launch: a crash that kills the process takes its own in-memory report with it, which is exactly why some of the crashes fixed in 1.22.1.0 left no trace at all.
- **New signals** — a per-launch session identifier, a 15-minute heartbeat, and a clean-shutdown marker. These exist so crash counts can be *normalised*: the events TDPdf already sent could say a crash happened, but not how often out of how many attempts, so there was no way to tell one user having a terrible afternoon from a regression hitting everyone.

### Changed

- **The telemetry destination is now delivered as managed policy** rather than compiled into the executable. Rotating the key becomes a policy push instead of building, signing and shipping a new binary to every device — which is the reason the previous key was never rotated in practice. The embedded key remains as a deprecated fallback so machines that have not received the policy keep reporting, and will be removed once it has rolled out.
- **The README's privacy section, which had become false.** It said released binaries ship with no telemetry endpoint embedded; that stopped being true when the key was embedded for 1.22.1.0. It now states plainly that a build you compile yourself reports to nobody, while a maintainer-released managed binary does report unless you turn it off.

### Removed

- `Telemetry.TrackMetric` and `Telemetry.TrackTrace`, which had no callers. Nothing was being lost — unlike the superficially similar defect found in a sibling project, where values *were* being recorded and silently discarded — but they advertised a capability the application does not have.

### Notes

- **Nothing is removed from the Azure side.** Application Insights keeps every alert rule and query it has; this adds a second destination rather than replacing the first.
- **The OTLP path is inert until an administrator configures it.** With no policy key present the exporter never starts, and behaviour is identical to 1.22.1.0.

## [1.22.1.0] - 2026-08-20

A fixes-only release. The headline is a crash that made **Insert Signature and Insert Text Box look broken**: placing either one could take the whole application down, so the annotation never appeared and the user was left thinking the tool did nothing. Alongside it, the applicable fixes from upstream KillerPDF's in-progress **v1.7.4**. (v1.7.2 and v1.7.3 landed in 1.21.0.0.)

### Fixed

- **Placing a signature or a text box no longer crashes the app** (#115). Production telemetry caught it directly: `InvalidOperationException: The enumerator is not valid because the collection changed`, thrown out of `Canvas.MeasureOverride` during the WPF layout pass, every occurrence within milliseconds of placing an annotation. The annotation overlay's children were being modified while WPF was walking them. This is the **same race `SafeWrapPanel` was written for in 1.8.1.0** — children added or removed while a measure pass over the panel is already in flight — surfacing on a different panel: `Canvas` enumerates its children where `WrapPanel` indexes them, so it throws a different exception type that the existing guard did not catch. Three things were fixed rather than one:
  - `AnnotationCanvas` is now a `SafeCanvas`, and both panels share one predicate covering both shapes of the race. It keys on the *throw site* being the visual collection, not on the exception type — an `InvalidOperationException` raised by a child's own measure is a real bug and must keep crashing — and not on the message, which is localized.
  - Two real paths into that mutation are closed. `RenderPage` queued the link, form-field and additional-page overlays at `DispatcherPriority.Loaded`, which outranks `Render`, so the continuation could be dispatched inside the very layout pass it was meant to follow; it now runs at `Background`. And the preview panel's `ScrollChanged` handler ran for *every* nested scroll viewer beneath it, because the event bubbles — including the live form-field text boxes and combo boxes parented into the annotation canvas — after which the Continuous branch assigned the selected page and its handler stripped selection chrome off the canvas mid-layout. **Side effect worth knowing: scrolling inside a form field could previously move the current page.** It no longer does.
  - The crash reporter was **converting this recoverable exception into process death**. `InvalidOperationException` is classified recoverable, so the app should have carried on; instead the modal crash dialog was opened from the throwing stack, which pushed a dispatcher frame, re-entered layout, threw again, and tripped the dialog's own reentrancy guard into answering "don't continue". A recoverable crash is now marked handled and unwound *first*, and the prompt follows once the stack is clear.
- **Form field appearance streams are written in the invariant culture** (upstream v1.7.4). Every number in a generated appearance stream was formatted with the OS culture, so on a comma-decimal locale — `de-DE` and most of Europe — a field wrote `12,34`, which is not a valid PDF number token, and the entire appearance stream failed to execute in a strict viewer. Affects saved text fields, comb fields and checkboxes alike.
- **Filled form fields are no longer drawn twice in printed, flattened and exported output** (upstream v1.7.4). 1.21.0.0 fixed the offset-ghost effect on screen; the output path had the same fault for the same reason. Widget appearances were painted once by the static `FPDF_ANNOT` pass, straight from the stored `/AP`, and again by `FFLDraw` — visibly doubled wherever the two layouts disagreed. Widgets are now hidden for the static pass in both modes and their original flags restored before `FFLDraw` runs, so a field the document genuinely marks hidden still stays hidden. If the output path has no form environment to draw with, widgets stay visible and the static pass remains the one thing showing them.
- **A PDF forwarded from a second launch is no longer lost when it arrives during startup** (upstream v1.7.4, #202). WPF assigns `Application.Current.MainWindow` from the `Window` constructor, so a path arriving over the single-instance pipe could reach the open path while the window's own constructor was still running, against controls it had not resolved yet. The path is now held and replayed from `Loaded`.
- **A damaged PDF dropped on the Pages sidebar now offers a repair instead of vanishing** (upstream v1.7.4, #203). It was caught, written to the debug log and silently discarded, so the file simply disappeared from the drop with no message. Deliberately narrower than upstream: only the lossless PDFium re-save is offered, not the raster fallback. A rasterized *open* tells the user the document was recovered from pixels; a rasterized *append* would bury a run of flattened, unsearchable pages in the middle of a live document with nothing to mark them apart.

### Notes

- **Not ported from upstream v1.7.4, verified rather than assumed:** the title-bar drag-to-restore fix (#206) — TDPdf's `TitleBar_MouseLeftButtonDown` has always delegated to `WM_NCLBUTTONDOWN(HTCAPTION)` rather than `DragMove()`, and our logo bar carries only a mouse-wheel handler, so the maximized bail-out upstream repaired does not exist here. The Open-dialog crash without Quick Access (#210) targets upstream's custom file dialog, declined in v1.20. Also skipped: the packaging rework and portable launcher (TDPdf has its own single-file publish and Intune pipeline), the machine-wide uninstall elevation (our installer is per-user), Hungarian translations and the `en-US` string churn (TDPdf is English-only), and the landing-page and theme-logo assets.
- **Needs Windows verification.** None of this can be exercised on the (macOS) build machine. Worth a smoke test, in rough priority order: place a signature and a text box on a form-bearing PDF in every view mode, especially Continuous, and confirm the app survives and the annotation appears; scroll inside a form field and confirm the current page no longer jumps; print, flatten and export a filled form and confirm the values appear exactly once; drop a deliberately truncated PDF on the Pages sidebar and take the repair offer; and double-click a PDF while a first instance is still starting up.

## [1.22.0.0] - 2026-08-18

A release-infrastructure release. **There are no functional changes to the application** — no fix, no feature, no behaviour difference from 1.21.0.0. Everything here is in how TDPdf is built, signed and delivered. It is versioned rather than folded into the next feature release so that the first build produced end-to-end by the automated pipeline is identifiable, and so the Intune detection script and the shipped binary move together the way the new checklist requires.

If you are reading this to decide whether to upgrade: you do not need to. 1.21.0.0 and 1.22.0.0 are the same editor.

### Changed

- **Releases are now built, signed and published automatically from a pushed tag**, rather than by hand. The CI runner is Linux, so signing uses `osslsigncode` rather than Windows-only `signtool.exe`, and the Intune app is updated through Microsoft Graph rather than by uploading a `.intunewin` produced by the Windows-only `IntuneWinAppUtil.exe` — that file is only a transport container for the portal, and Graph accepts the encrypted payload directly. Each stage is gated on its credentials being present, so a missing or rotated secret degrades the run to an unsigned build-and-publish instead of failing it.
- **The published binary is signed**, and the release now carries the GPLv3 corresponding-source zip and a `SHA256SUMS.txt` alongside the executable.
- **The Intune deployment updates the existing app in place** — a new content version, the displayed version, and the detection script are all set in one operation, so the payload and the script that detects it can no longer disagree. Existing assignments are untouched, because assignments belong to the app rather than to a content version.

### Fixed

- **The telemetry key was never actually embedded by CI.** The generator built its output path as a literal `Diagnostics\...` string, so on the Linux build machine it wrote a file with a backslash in its *name*. The project never saw that file, so every CI build silently shipped the no-op placeholder instead of failing.
- **Tagged releases shipped no GPLv3 corresponding-source zip.** The bundling step invoked `powershell`, which does not exist on Linux, and the script itself relied on `%TEMP%`, which is unset there. The build step ignores its own exit code, so this failed silently on every release. The public repository satisfied the licence throughout; the intended artifact simply was not being produced.
- Two faults in the new Intune upload path, both of which failed in ways that pointed nowhere near their cause: the content file must be named `IntunePackage.intunewin` or the service rejects it with a `BadRequest` that names no field, and the trailing partial upload block was being sent as text rather than bytes — accepted by storage, and surfacing only much later as a bare `commitFileFailed` when Intune could not validate the payload.

### Notes

- **Detection now requires 1.22.0.0**, so managed machines will reinstall even though the application is unchanged. That is the cost of versioning this release and is understood.
- Upstream KillerPDF is unchanged since the v1.7.3 sync in 1.21.0.0; there is no fork-sync content here.

## [1.21.0.0] - 2026-08-17

Fork-sync release porting the features and bug fixes from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) **v1.7.2** and **v1.7.3**, adapted to TDPdf's diverged multi-tab architecture. The headline items — a print dialog that can finally choose its paper, column-aware text selection, book layout, Transform LEVELS — sit alongside a group of memory and save-path fixes that matter more here than they read upstream. Porting surfaced three bugs in upstream's own versions of these changes, all fixed here rather than inherited.

### Added

- **Paper size and paper source selectors in the print dialog, and collapsible settings sections** (upstream v1.7.2, #186). The dialog had no way to choose paper at all — the ticket carried only copies, orientation, color and duplex, so the stock was whatever the driver defaulted to, and a Letter-default printer quietly printed A4 documents wrong. Both lists come from the driver's own capabilities and rebuild when the printer changes; "Match document" and "Printer default" keep today's automatic behavior. A chosen size drives the preview and the job through the same coupling, being set on the ticket before the printable area is read in both paths, so the composed sheet cannot disagree with what the job prints on. The 248-line settings column is now collapsible **PRINTER** / **LAYOUT** / **OUTPUT** sections using the same pattern the Transform dialog already had, with LAYOUT folded — which is what keeps a 268px column workable while gaining two controls. Neither selection is persisted: they are driver-scoped, and a remembered size is meaningless against a different printer.
- **Two-Page book layout** (upstream v1.7.2, #193). The cover displays alone, so facing pages pair like a physical book — 1 | 2‑3 | 4‑5 — instead of always (0,1), (2,3). Bare **B** toggles it, it is on the View menu and the F1 overlay, and it persists. Four pairing sites are book-aware, including the two that otherwise park the lone cover in the left half of a centred two-slot panel.
- **Comb text fields** (upstream v1.7.2, #158). A `/Tx` field with the comb flag and a `/MaxLen` now caps typing at the cell count, lays the overlay out one character per cell in a monospace face sized to the cell, and writes an appearance stream placing each character at its cell centre — the way Acrobat prints them. Previously these were ordinary uncapped text boxes whose saved value ignored the cells entirely.
- **Transform LEVELS** (upstream v1.7.2, #174). Black point, white point and midtone gamma, for rescuing pale, hard-to-read scans. Unlike perspective correction, levels are page-independent, so they carry no single-page restriction and apply across a whole selection.
- **Drop PDFs or images onto the Pages sidebar to append them** (upstream v1.7.2, #172). The sidebar previously accepted only its own page-reorder payload and showed the no-drop cursor for files. Appending rather than inserting at the drop point is deliberate: existing page indices stay stable, so annotations and rotations need no remapping. A dropped PDF gets the same named-destination link rewriting a merge does, because the merge path was extracted and shared rather than written a second time.
- **Click the status line — or press Shift+F4 — to see the open document's file size**, after which the previous line comes back (upstream v1.7.2). It reports against the original path, never the `%TEMP%` working copy a password-protected document is edited through.

### Changed

- **Text selection follows columns** (upstream v1.7.2, #185). Line grouping was by vertical band alone, so on a two-column page a left-column line and its right-column neighbour merged into one line: dragging down one column swept the other, and copied text came out row-interleaved. Bands are now split at gutter-sized gaps, clustered into columns by horizontal overlap, and emitted whole columns left to right, with titles and footers closing the group so a title / two columns / footer page keeps a sane order. The same split is applied to the text-edit line buckets, so double-clicking picks up one run per column instead of a run whose text was both columns space-joined and whose box straddled the gutter.
- **The current page shows in a corner badge** (upstream v1.7.2, #197), sliding up on scroll and page change and back down when the view settles, replacing the page tooltips that trailed the cursor. Suppressed for single-page documents, and never hit-testable, so it cannot swallow a page click.
- **The Outlines sidebar opens with top-level bookmarks visible and deeper levels folded** (upstream v1.7.2), instead of expanding every node at every depth, and expand/collapse choices now survive a tab switch rather than only a bookmark edit.
- **The last manual zoom is restored on reopen** (upstream v1.7.2, #201). Every open ran a fit and threw the user's zoom away. Manual zoom and fit are now two halves of one preference — the last explicit zoom decision — so exactly one is live at a time. This deliberately does **not** resurrect the v1.20 failure where a raw zoom saved at a different window or monitor size opened a document enormous or microscopic: a fit is still replayed *as a fit* against the current window, and only a zoom a person actually chose is replayed as a number.
- **Documents get keyboard focus on open** (upstream v1.7.2, #196), so the scroll viewer's own keyboard scrolling — Space, and arrow-scrolling inside a page zoomed past the viewport — works without clicking first. Narrower than it sounds: arrow and Page Up/Down paging already worked from anywhere, because TDPdf handles those at the window level.

### Fixed

- **Saved highlights no longer wash out the text underneath** (upstream v1.7.2, #200). They burned as an opaque-ish alpha rectangle painted over the words; they now use the Multiply blend mode, so the colour darkens the paper and the text stays crisp. Only fills get Multiply — TDPdf's markup annotations carry a style, and a thin dark strikethrough or underline band multiplied over text would disappear. On-screen highlights still paint as the old alpha rectangle, so screen and saved output differ slightly in appearance; upstream has the same seam.
- **A page opened with a non-zero `/Rotate` no longer has its `MediaBox` swapped on save** (upstream v1.7.2, #184), which permanently clipped the content. Fixed in the vendored PdfSharpCore, whose landscape media-box flip fired on pages read from a file rather than only on pages the caller creates in landscape. The old restore condition did not even match the flip condition — a landscape page with odd `/Rotate` had its box "restored" without ever having been flipped.
- **The viewer no longer bakes form-field appearances underneath the live field overlays**, which painted the same text twice, slightly offset — the ghost or drop-shadow effect on filled forms. Print, flatten, export, thumbnails and raster recovery still include them, because there the pixels *are* the output. Applied only to the primary tile: unlike upstream we have no per-page form overlays, so hiding widgets on the secondary or continuous tiles would have erased the filled values instead of un-ghosting them.
- **Editing text no longer loses the font when the PDF names it in PostScript form** (upstream v1.7.2, #187). `ArialMT`, `TimesNewRomanPSMT` and `Helvetica` name no installed Windows family, so the in-place editor silently substituted a fallback face and the save path fell through to Segoe UI — the detected bold/italic was right but the family never applied, which reads as "all my formatting was lost".
- **Peak memory on large documents is substantially lower** (upstream v1.7.2, #189 / PR #194). Two independent causes. The continuous re-sharpen pass rendered at *twice* device resolution: `targetW × zoom × dpiScale` already is the page's on-screen size in device pixels, so the extra factor was a linear supersample costing four times the pixels and four times the bytes for detail no display can resolve. And the page bitmap cache was capped only by page count, so 48 cached Letter pages could hold roughly 630 MB in a single tab — it is now budgeted in bytes as well, with a floor of nearby pages so the moving window around the viewport still serves instantly.
- **The view re-renders when the window moves to a monitor with a different scale factor.** Every raster budget is measured in device pixels, so a monitor move changes how many pixels a page needs without changing its size in logical units; at a manual zoom nothing else fired and the page sat upscaled from the old monitor's render. The 2× supersample above used to mask this by accident.
- **Exported images carry the chosen DPI in their metadata** (upstream v1.7.2, #188) instead of always reporting 96, in both the GUI export and the CLI, so a 300 DPI export places and prints at its true physical size.
- **Clicking in the right-hand column of a two-column PDF puts the caret where you clicked.** *(Bug in upstream's own #185, fixed here rather than inherited.)* Their caret hit-test takes the first line containing the clicked Y, but once columns split a band into several lines the left column always comes first in reading order — so every click in the right column landed at the end of the left column's line.
- **Ordinary single-column pages are not reordered by the column logic.** *(Bug in upstream's #185.)* Upstream clusters unconditionally, so a letter with a right-aligned date, a centred title and a short salutation — three narrow lines that do not overlap horizontally — clusters as three "columns" and reads back as salutation / title / date. Clustering here requires two bands with a real gutter, and a second guard keeps table rows row-major, since a row splits at its cell gaps exactly like a gutter and an invoice read column-major gives every name, then every quantity, then every price. A rejected group is handed back as the bands it came in as, so a misjudgement can only ever fall back to the previous order.
- **Windows font families ending in "MT" still resolve.** *(Bug in upstream's #187.)* Their name normalizer commits to the foundry-trimmed name even when the lookup misses, turning `BellMT` into `Bell` and `Gill Sans MT` into `"Gill Sans "` — trailing space and all — which resolve to nothing and fall through to the default font. TDPdf's resolver already matched these by ignoring spacing, so trimming made previously-working fonts fail.

### Notes

- **Not ported from upstream v1.7.2 / v1.7.3:** the seven new themes (98SE, Ectoplasm, Decay, Mourning, Sepulchre, Delirium, Malaise); Polish localization and all other localization — TDPdf is English-only; per-pane night mode and the split-pane grid-resize fix, which need the split-pane viewer we do not have; machine-wide `killerpdf://` protocol registration (#183), branding-bound scaffolding for a browser extension we do not ship; and the family file-dialog work — the restored film grain, the centred picker radio dot (#198), and **v1.7.3's live preview pane in the image-selection dialogs**, which all live in the custom picker replacing every stock Windows dialog. That picker was declined in v1.20 and remains declined; our image flows still use the stock `OpenFileDialog`, so there is nothing to hang a preview on without adopting the whole dialog. Also skipped: v1.7.3's accent-following tab ring and the theme-flyout jump fix (#199), which target upstream's accent system, and the KillerUI/theme-ownership refactor.
- **Already present, verified rather than assumed:** horizontal touchpad and tilt-wheel scrolling (#196) — TDPdf has had the `WM_MOUSEHWHEEL` hook since v1.19. Upstream's cross-thread crash in the render cache budget **cannot occur here**: their cache is written from background render threads, ours is touched only on the UI thread and already stores its pixel dimensions, so the byte budget needs neither a lock nor upstream's parallel size dictionary.
- **Deliberately not ported:** upstream's book layout also snaps the *rendered* primary page to the start of the spread. They can, because their refactor keeps the current page and the rendered primary page as separate concepts; here the sidebar selection **is** the primary page, and 86 call sites depend on that. Snapping the render would send annotations, form fields and search highlights to the wrong page, and snapping the selection would break search, which sets the selection precisely so the hit gets highlighted. So paging is fully book-aware and the cover renders alone, but jumping directly to an arbitrary page still shows that page plus the next — which is what Two-Page has meant in TDPdf since #120.
- **Known pre-existing issue, unrelated to this release but found during it:** TDPdf's `WndProc` marks `WM_DPICHANGED` handled so it can apply Windows' suggested rect against the custom chrome, and public window hooks run before WPF's internal one — so WPF never processes the message and its own DPI state never updates. The raster budgets above are now correct regardless, because they read the scale straight from the message, but the window *chrome* may still render at the starting monitor's scale after a move. Worth a separate issue.
- **Needs Windows verification.** This release changes the render budgets, the save geometry, the print ticket, text selection and the form-field render path, none of which can be exercised on the (macOS) build machine. Worth a smoke test: printing to a non-default paper size and tray; a two-column PDF selected, copied and double-click-edited, plus a single-column page and a table to confirm they are untouched; a filled form on screen (the ghost) and then printed and flattened (values must still be there); a comb field typed and saved; annotating and saving a page opened at `/Rotate 90`; a highlight saved and reopened elsewhere; Two-Page with and without book layout; dragging the window between monitors at different scale factors at a manual zoom; and a long document scrolled hard in Continuous to exercise the smaller cache budget.

## [1.20.0.0] - 2026-08-06

Fork-sync release porting the features and bug fixes from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) **v1.7.0** and **v1.7.1**, adapted to TDPdf's diverged multi-tab architecture. Upstream's v1.7.0 was largely an internal refactor — about 8,700 lines moved into a new viewer control to enable split-pane — which does not apply here; what came across is the substantial set of correctness fixes riding alongside it, plus perspective correction, night mode that spares pictures, and non-Latin text that survives a save. Several of the fixes turned out to matter more in TDPdf than upstream, and porting surfaced four TDPdf-specific bugs of our own.

### Added

- **Trapezoidal perspective correction in Transform** (upstream v1.7.1, #175). Turn on perspective correction, drag four corner handles onto a page photographed at an angle, and Apply straightens that quadrilateral into a true rectangle — composed with rotation, deskew, scaling and flipping in the same operation, at the full transform resolution. Drag capture is taken over the overlay subtree, so a handle keeps tracking when the pointer crosses a child control or leaves the window. The Transform settings column is now collapsible sections (Rotate open, the rest folded) so a fifth section fits. **Restricted to a single-page selection**: rotate, scale and flip are page-independent, but a corner outline is traced against *one* page's photographed edges, and applying that same quad to the rest of a selection would warp them by an outline that was never theirs — irreversibly, since the page is rasterized. With more than one page selected the section says so rather than silently doing nothing.
- **Night mode keeps pictures their real colors** (upstream v1.7.0, #135 follow-up). Photos and figures hold their true colors while the page around them goes dark. Image rectangles come from PdfPig as fractions of the unrotated page, so one cached set serves every render resolution, and the carve-out re-applies the inversion operator over those regions — for an already-inverted opaque pixel that is exactly the original composited over white. Right-click the moon (or press **Shift+N**) for **Invert images too**, which is what makes a scanned document — one page-sized image — usable in night mode. Ctrl+I still toggles night mode; upstream's remap of it to a bare `N` was not adopted, as our tool hotkeys already claim single letters.
- **Ctrl+Shift+W closes every tab except the current one** (upstream v1.7.0), alongside a new tab right-click menu (Close Tab / Close Other Tabs). Each unsaved tab still gets its own save prompt, and declining keeps that tab open instead of aborting the sweep.
- **Fit Width / Fit Page are remembered** across launches (upstream v1.7.1) when chosen explicitly, instead of every open re-imposing a hardcoded per-view-mode default — so users on smaller screens no longer switch away from Fit Page every time.
- **A Settings checkbox for "Confirm before opening links."** The behavior already existed, but the prompt's "Don't ask again" was a one-way trapdoor: once ticked, no UI anywhere could turn confirmation back on. TDPdf's default is unchanged — we ask.

### Changed

- **The annotations a PDF already carries are now rendered** (upstream v1.7.0/v1.7.1, #141/#179). Sticky notes, highlights, stamps, ink made in another app and filled form values were in the file and simply never painted — PDFium does not draw annotation appearance streams unless asked, and Docnet's parameterless `GetImage()` passes no flags. This went well beyond the screen: flatten and image export build a new document from the pixels, so the source's markup was silently dropped from the output, and printing omitted it. Docnet's annotation-aware overload is unusable — it builds a form-fill environment per call and destroys it while the page is still open, corrupting PDFium state so the *next* native call takes an access violation (upstream shipped that, crashed, and reverted). Instead, every direct `pdfium.dll` call is consolidated into `Services/PdfiumInterop.cs` and a new renderer owns the whole document/form/page lifetime, tearing it down in the order this PDFium build expects. Threaded through the viewer, continuous view, Grid and Two-Page tiles, print preview and spool, flatten, image export, raster recovery, Transform and the CLI, each falling back to the old path so a PDFium failure degrades to previous output rather than a blank page. Deliberately **not** used for OCR (a reviewer's sticky note is not page content) or for sidebar thumbnails (they render eagerly over every page on the open path, where a per-page document open would cost open time proportional to page count for markup barely legible at 256px).
- **100% now means 100% in every view mode** (upstream v1.7.0, #154). Outside Continuous, the box the zoom transform scales is the render bitmap rather than the page's natural width, so asking for 100% actually landed at **~137% on A4 and ~145% on US Letter** — and the readout, the dropdown presets and the min/max clamp were all wrong with it. Upstream converted only absolute zoom requests; TDPdf's zoom model now means *true* zoom end to end, with one display-factor seam converting to layout scale. Ctrl+0, Ctrl+1, every preset, the wheel steps, the percentage readout and the 5–400% clamp agree in Single, Two-Page, Grid and Continuous alike. Two consequences worth knowing: peak raster at maximum zoom roughly halves, because render DPI now follows the true layout scale; and a very small page can no longer fit-to-width past 400%, which is exactly how Continuous already behaved.
- **Punctuation shortcuts work on keyboard layouts that need Shift** (upstream v1.7.0, #153). Shortcuts were matched by key *position*, a US-layout assumption: on a German keyboard "?" lives on Shift+ß and "=" on Shift+0, so Ctrl+? and Ctrl+= pressed keys the app was not listening for. Matching now asks the active layout which character a key types, and the menu, tooltip and overlay spellings follow suit. Positional checks are kept as fallbacks, so US behavior is bit-for-bit unchanged. Ctrl+0 stays positional deliberately — on AZERTY the digit needs Shift, which would collide zoom-reset with app-scale-reset — and **Ctrl+NumPad0** is added as the reset for those layouts.
- **Multi-line highlights follow right-to-left reading direction** (upstream v1.7.1, #170). Detected per line, so mixed-language pages work without a document-wide setting; line extents are now the selected slice's physical extremes rather than assuming the first character is leftmost. Highlight, strikethrough and underline all inherit it.
- **Page-number tooltips reach every page in every view mode** (upstream v1.7.0, #151). Previously only secondary tiles carried one, so Single and Continuous had none, Two-Page showed it only on the right page, and Grid started at page 2.
- **The shortcuts list uses the window width** (upstream v1.7.1, #177) instead of a fixed 640px card, with wrapping descriptions and level columns.
- **The app-size readout no longer parks itself on the status bar** (upstream v1.7.0). Each notch restarts a five-second timer and the previous status returns when it expires — skipped if something real was written in the meantime.

### Fixed

- **Annotations were burned into the wrong frame on a rotated page** (upstream v1.7.0/v1.7.1, #169). The save path never read page rotation at all, so anything placed on a quarter-turned page was written a quarter turn out of place and scaled on swapped axes. Because TDPdf keeps rotation on the page itself rather than in upstream's temporary map — and PdfSharpCore already swaps a rotated page's reported width and height, which `XGraphics` then builds its flip from — upstream's matrices could not be copied and were re-derived as the exact inverse of TDPdf's existing PDF→canvas mapping. The rendered box origin is folded in at the same time, so an inset or offset `/CropBox` no longer displaces annotations either.
- **A `/CropBox` extending outside its `/MediaBox` is scrubbed on save** (upstream v1.7.1, #169) — a malformed combination strict validators reject. Removal is lossless: the page falls back to its full media box. Done only when the media box itself is usable, so a damaged media box can never be a reason to delete a good crop.
- **Filled form fields generated incomplete appearance streams** (upstream v1.7.1, #180). No `/Length` was written at all, because the stream was attached through a reflection helper that omitted it. **In TDPdf this was worse than upstream's "damaged file" warning: a cold open of a form we had saved could fall through to raster recovery and flatten the user's filled form into flat images.** Multiline fields were laid out as a single run, and text was encoded such that curly quotes, dashes, bullets and ellipses became `?`. All three fixed; TDPdf uses an explicit WinAnsi table rather than a code-page provider, so it cannot throw at save time.
- **A signature dropped on a form field was hidden behind it** (upstream v1.7.0, #156). TDPdf draws form overlays and annotations onto one shared canvas and added the overlays last, with near-opaque backgrounds. Overlays now sit below the annotation layer and stay clickable, because the annotation visuals do not intercept the mouse.
- **Clicking a page could crash with "'∞' is not a valid value for property 'Height'"** (upstream v1.7.1, #181). Our guards were NaN-only or `< 2`, and `∞ < 2` is false, so infinity sailed through to WPF. Every sized annotation and form widget is now checked. The live source is fixed too: a zero `CanvasWidth` in `signatures.json` produced an infinite scale that was **persisted onto the annotation**, crashing every later render.
- **Non-Latin text saved as empty boxes** (upstream v1.7.0, #168). Typing Japanese looked right on screen — the text box borrows glyphs from any installed font — then saved as a row of blank squares, silently. The stock font resolver enumerates only `*.ttf`, and nearly every CJK face on Windows ships as a TrueType collection (Yu Gothic, MS Gothic, Meiryo, YaHei, JhengHei), so those fonts were never even seen; nothing checked glyph coverage either, and the burn hardcoded a Latin face. TDPdf now indexes `ttf`/`ttc`/`otf`, extracts a standalone face out of a collection, and picks a family by actual cmap coverage with a per-script fallback chain — always keeping your own font when it covers the text. When nothing installed can draw a character you are told as the text is *placed*, listing the characters, instead of discovering it after saving and reopening. The resolver cannot throw into the save path.
- **Editing bold or italic text turned it regular** (upstream v1.7.1, #182). PDF text carries its face inside the font name; the detector stripped those tokens to find the family, and TDPdf's version stripped them *mid-string* — `Helvetica-BoldOblique` became `HelveticaOblique`, which resolves nowhere, so the editor silently fell back to the default font. Family and face are now separated properly and both survive into the saved annotation.
- **Edited text could collapse to a fixed size** (upstream v1.7.0, #163/#165). The size was read from the content stream's `Tf` value, which is only the visual size when the text matrix does not scale — a generator emitting `/F1 1 Tf` reported 1. The point size is used now, and a bogus value no longer clobbers the line-height estimate.
- **Owner-restricted PDFs with a malformed linearization table would not open at all** (upstream v1.7.1). The read-only retry threw from inside its own catch block, so the exception escaped the open path entirely and the raster-recovery net never ran. The retry is now contained and falls through to the existing PDFium cleanup. Files that open read-only today still do, with the same status text; only a file that genuinely needed the repaired copy reports "(owner restrictions removed)".
- **The eyedropper's OK could be silently discarded** (upstream v1.7.0). Its nested capture window corrupts the outer dialog's result, so a real OK came back as a cancel and the tool kept drawing the previous color. The dialog now reports through its own flag; Escape and Cancel were exposed to the same fault and are guarded too. The button gains a lit armed state and no longer shows a crosshair on mere hover.
- **Cropping a rotated page cropped the wrong region.** *(TDPdf-specific, found while porting #169.)* The canvas→PDF crop mapping ignored `/Rotate` entirely.
- **Fit Page did nothing in Continuous view.** *(TDPdf-specific, found while porting.)* The primary page image is collapsed in Continuous, so the fit measured zero and silently no-opped; it now fits against the strip's own geometry.
- **Night mode looked different depending on which renderer served the page.** *(TDPdf-specific, found while porting.)* Our inversion was a plain channel flip that left alpha untouched, which works only on opaque pages. It now composites over white and forces alpha opaque, as upstream does, so every render path agrees.
- **Ctrl+? never worked, even on a US keyboard.** *(TDPdf-specific, found while porting #153.)* Its modifier test demanded exact equality with Ctrl, while physically typing "?" is Ctrl+Shift+`/`.
- Removed the unreferenced `Services/PrintService.cs`. Its page-range helper re-parsed the range with no odd/even filter — the exact shape of the bug already fixed in the live print path — and was a trap for the next contributor.

### Notes

- **Not ported from upstream v1.7.0:** split-pane mode and the ~8,700-line `PdfViewer` control extraction it is built on (TDPdf's multi-tab `DocumentContext` is a different architecture); the shared "Killer Tools family" file dialogs replacing every stock Windows one; the family rounded-card theming, new app icon and themed system menu; the dissolution of the Settings panel into rail flyouts; the toolbar appearance menu (it requires visible toolbar labels TDPdf has never had); the sidebar left/right swap; and all localization — TDPdf is English-only. Upstream's `killerpdf://` protocol handler was skipped as branding-bound scaffolding for a browser extension we do not ship. Upstream's machine-wide PDF registration fix (#176) is **not applicable**: TDPdf's installer is already scope-aware and writes the handler to the correct hive, because it has to support Intune deployment.
- **Already present, verified rather than assumed:** upstream's rotated-page transform squash fix (#167) and the odd/even print filter fix (#159) were both already correct here — TDPdf fixed them independently in v1.19.0.0 — as were the null-safe link `/Subtype` read and the PDFium call locking.
- **Needs Windows verification.** This release changes the render pipeline, the save geometry, zoom in every view mode and the font resolver, none of which can be exercised on the (macOS) build machine. Worth a smoke test: scrolling and zooming a long annotated document for a while (the new direct-PDFium render path is the highest-crash-risk change — a lifetime mistake there surfaces as an access violation on a *later* call); annotating and saving a rotated page; cropping a rotated page; a filled form saved and then cold-opened; Japanese text placed and saved; Ctrl+0 measuring true size in all four view modes; the zoom and app-size chords on a non-US layout; perspective correction on a real photographed page; and night mode with and without "Invert images too" on both a text PDF and a scan.

## [1.19.0.0] - 2026-07-27

Fork-sync release porting the features and bug fixes from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) **v1.6.5** and **v1.6.6**, adapted to TDPdf's diverged multi-tab architecture. The headline items — flowing text selection with line-hugging markup, freeform polygon shapes, an image-export dialog, invert-for-reading, app-wide UI scaling, and a Remove Password command — sit alongside a group of geometry and save-path fixes, two of which are TDPdf-specific bugs found while porting.

### Added

- **Flowing text selection, and Strikethrough / Underline tools** (upstream v1.6.5, #127). Dragging with the Select tool now tracks the actual run of characters in reading order, browser-style, across lines and paragraphs, with real per-line selection rectangles instead of one box. **Highlight** follows the text the same way — drag over words and the markup hugs each line — and two new tools, **Strikethrough** and **Underline**, join it. One gesture produces one grouped annotation that selects, moves, resizes, deletes and undoes as a single unit. **Ctrl+A** now shows real per-line selection rather than a full-page rectangle. A plain click still selects annotations, a drag starting on empty page still box-selects, and **Shift+drag** forces the classic marquee.
- **Freeform polygon shapes** (upstream v1.6.5). The Shape tool gains a fourth kind alongside Rectangle, Ellipse and Line: click to place each vertex, double-click or click the highlighted first point to close, **Backspace** removes the last point, **Esc** cancels. Polygons share the shape bar's color, size, opacity and Fill toggle, and move, resize, select, undo, print and export like any other shape.
- **Export pages as images** (upstream v1.6.5, #132). A new **File ▸ Export Pages as Images…** entry renders pages to PNG or JPEG at a chosen DPI (24–1200, default 150) with an optional page range. Pending annotations are burned in and in-app rotation is honored — the dialog runs the same pipeline as the CLI's `--to-image`, now factored into a shared `PageImageExporter` so the two cannot drift. Long exports report progress and cancel with **Esc**.
- **Odd / even page printing** (upstream v1.6.5, #134). An **All pages / Odd pages only / Even pages only** selector in the print dialog, applied on top of the typed page range, with the preview following along — manual duplex for printers without a duplexer. The spool path now shares the preview's page-selection helper, so what prints always matches what the preview showed.
- **Invert document colors** (upstream v1.6.5, #135). A moon toggle at the bottom of the sidebar rail (**Ctrl+I**) renders the document with inverted colors for dark-mode reading, lit in the accent while active and remembered across launches. Display only: saving, printing, exporting, OCR and the sidebar thumbnails all keep the document's true colors, because the inversion is applied at the display layer rather than in the shared page rasterizer.
- **App-wide UI scale** (upstream v1.6.5). Scale the app chrome — toolbar, sidebar and tab strip — from 70% to 250% with **Ctrl+Shift+=** / **Ctrl+Shift+-** / **Ctrl+Shift+0**, or by scrolling the mouse wheel over the title-bar logo or the new footer size chip. Remembered across launches. It uses a layout scale so UI text stays sharp, and the document pane is deliberately untouched — app size and page zoom stay separate controls.
- **Remove Password** (upstream v1.6.6, #149). A **File ▸ Remove Password…** entry saves a protected document back over the original with its password protection dropped, enabled whenever the file needed a password (or carried owner restrictions) to open. TDPdf already strips encryption at open time because the editing pipeline cannot modify encrypted files in place, so every save has always written an unprotected PDF; this makes that a visible, deliberate action, and ordinary saves of a previously protected file now say so in the status bar instead of dropping the password silently.
- **"Don't remember recently opened files"** (upstream v1.6.5, #146). A new Privacy section in Settings; turning it on also empties the existing list, so nothing about your documents lingers on a shared machine. Session restore keeps its own separate control (`ReopenSession`) and is unaffected.
- **Single-key tool shortcuts** (upstream v1.6.6, adapted). TDPdf previously had no tool hotkeys at all — tools were toolbar-only. Following upstream's principles rather than its table (our tool set differs), `V` selects the Select tool and the digits mirror the toolbar left to right, with mnemonic letters alongside: `V` Select, `P` Pan, `1`/`T` Text, `2`/`X` Edit Text, `3` Edit Image, `4`/`I` Insert Image, `5`/`H` Highlight, `6`/`K` Strikethrough, `7`/`U` Underline, `8`/`S` Shape, `9`/`D` Draw, `0`/`E` Erase, `G` Signature, `C` Crop. Number-row and numpad digits both work. The shortcuts overlay (both list and keyboard views), the Tools menu, the context menus and every toolbar tooltip now carry these hints.
- **Menu items carry their icon in the gutter** (upstream v1.6.5 / v1.6.6). The submenu template gained a real 20px icon-and-check column, so every item in the menu bar and in the page, annotation, sidebar-thumbnail, bookmark, link and recent-files context menus now shows the same glyph as its toolbar equivalent, and page rotation gets a properly mirrored CW / CCW pair. Menu items that carry state (Invert Colors, the OCR language list, "Use High Quality Models") now show a real check mark — previously the template reserved no gutter, so `IsCheckable` items rendered no state at all and Invert had to flip its header text instead.
- **`--transparent` flag on `--to-image`** (upstream v1.6.6, #148) keeps the raw alpha channel for PNG output when transparency is actually wanted.

### Changed

- **The page sidebar animates and follows the workspace** (upstream v1.6.5 / v1.6.6). Collapsing and expanding is now a smooth quarter-second slide with the thumbnails holding their size (clipped, not squished) instead of a snap. The sidebar also starts collapsed when no PDF is open, opens itself when one is, and re-collapses when the last document closes — and the empty page-number box and "/ –" are hidden until a document is open. A manual collapse survives opening a second document; only an empty-to-occupied transition moves it.
- **The "Password Required" prompt is a themed dialog** (upstream v1.6.6) — wordmark title bar, dark card, themed password field, Open / Cancel — instead of the stock Windows dialog with native chrome and light Aero controls it used to be. The dialog shell is now shared with `TdpDialog` so the two cannot drift.
- **Esc steps down instead of doing nothing** (upstream v1.6.6). Esc now works through what there is to cancel — an in-progress polygon, then full screen, then the find bar, then the shortcuts overlay — and, with nothing left to cancel, returns to the Select tool, Acrobat-style. Upstream's "a second Esc exits the app" was deliberately **not** adopted: Esc has never quit TDPdf and making it do so would be a surprising way to lose work.
- **Page geometry is resolved once, and correctly** (upstream v1.6.6). A single inheritance-aware helper now resolves each page's rendered box — CropBox over MediaBox, walking the page-tree `/Parent` chain, honoring a non-zero box origin — and both the form-field and link overlays map through one shared PDF→canvas conversion. This replaced three separate page-size helpers, one of which double-applied the `/Rotate` swap.

### Fixed

- **Exported JPEGs no longer come out as black pages, and exported PNGs no longer carry a needless transparent background** (upstream v1.6.6, #148). PDFium leaves unpainted background pixels fully transparent; the JPEG encoder dropped that alpha channel and kept the zeroed color underneath, so any page without an explicit background — most PDFs — rendered solid black through `--to-image`. Exports now composite over white, which also keeps a full-page alpha channel out of flattened PDFs (`--flatten` and Save Flattened).
- **Interactive form-field overlays sat in the wrong place on non-A4 documents** (upstream v1.6.6). The field parser fell back to a hardcoded A4 page size whenever a page's `/MediaBox` was inherited from an ancestor `/Pages` node rather than written on the page itself, so fields on US Letter and other non-A4 pages sat roughly 40 points adrift. It also ignored `/CropBox` entirely even though the page bitmap is rasterized *to* the CropBox, and assumed a `[0 0 …]` box origin. All three are fixed, and PDF **link** overlays — which had the same defects plus an unapplied rotation — now go through the same corrected mapping.
- **Saving a PDF could plant a zero-size `/MediaBox`** that Adobe rejects as "page dimensions out-of-range". PdfSharpCore's page-box property getters create the entry on read, so merely measuring a page whose box was inherited wrote an empty `[0 0 0 0]` box into the page dictionary. Page-box reads no longer touch those getters, and every save now scrubs a degenerate `/MediaBox` — re-planting the box the page tree really specifies — as well as the degenerate `/CropBox` already handled in v1.18.0.0, so files damaged by an earlier build heal when re-saved.
- **Ctrl+S could write to a temporary file instead of your document.** *(TDPdf-specific, found while porting.)* The in-place save target was the working path, which is repointed into `%TEMP%` after opening a password-protected file, after any structural edit (rotate, delete, reorder, crop), and after the stream-length repair path — so a plain Save wrote to a temp copy that is deleted on exit, cleared the dirty flag, and never updated the real file. Saves now target the document's real on-disk home. A blank **New** document and a raster-recovered document correctly route to Save As instead.
- **Cropping a document retargeted it to a stray file.** *(TDPdf-specific, found while porting.)* Applying a crop reloaded the document from `CropService`'s `<name>.crop-<guid>.pdf` output and adopted that as the document's identity — renaming the tab, adding the stray file to recents, and (with the save-target fix above) making Ctrl+S write to it. An internal reload now preserves the document's identity.
- **Inverted display colors could be baked into a saved PDF.** *(TDPdf-specific, found while porting.)* The image-edit region capture read the on-screen bitmap, so with invert active the captured region was written inverted into the document. It now reads the true-color page.
- **Delete and the arrow keys hijacked typing in the find bar, the page-jump box and form fields.** *(TDPdf-specific, found while porting.)* Window-level key handling ran ahead of several text-entry surfaces, so pressing Delete while editing a form field could delete the page instead of a character, and Home/End/arrows paged the document instead of moving the caret. Key handling now bows out structurally for any focused text box, password box or editable combo — which is also what keeps the new single-key tool shortcuts from firing mid-sentence.
- **A latent crash burning multi-line text** (upstream v1.6.5, #142/#144). The vendored PdfSharpCore text formatter dereferenced the empty line-break blocks a newline produces when justifying, and divided by zero on a single-block line. Guarded, matching the checks its sibling formatter already applied. TDPdf draws annotation text line-by-line and never reaches this path, so this is defensive alignment of the vendored tree rather than a user-visible fix.

### Notes

- **Flowing selection is single-page.** It works in Single, Two-Page and Grid view on the active page; **Continuous view and cross-page selection are not covered**, and the classic behavior is unchanged there. This is architectural rather than an oversight: TDPdf has one annotation canvas bound to the primary page, while upstream maintains a live per-page overlay for every rendered page. Adding cross-page selection is a rendering change, not a selection change, and was deliberately not attempted here rather than shipped half-working. The selection state is page-indexed throughout, so it can be extended once per-page overlays exist.
- **Highlighting a scan still draws a rectangle.** Upstream replaced the rectangle gesture on pages with no text layer with a status hint, having moved that gesture into its Shapes tool. TDPdf's Shape tool never inherited it, so removing it would have been a straight capability loss; Highlight keeps its rectangle on a text-free page and explains in the status bar why it is not hugging words. Strikethrough and Underline — both new — show the hint and draw nothing.
- Upstream's Czech and Japanese localization, the mojibake repairs that go with them, the localized "Page N" labels, the veraPDF results refresh, and the landing-page/README work were skipped: TDPdf is English-only and does not ship upstream's website. Upstream's v1.6.6 **tool hotkey remap** was not adopted as-is — our tool set differs (no Stamp tool, Line is a shape kind, Transform is a dialog, plus Pan / Erase / Edit Text / Edit Image have no upstream equivalent) — see the Tools section above for TDPdf's own map. Upstream's stamp fixes (#145, #147) are **not applicable**: TDPdf has no page-number/watermark stamping feature. Upstream's named-destination bookmark fix (#143), its signature-scrub null guard, and its `.gitattributes` protection for vendored sources were already present in TDPdf.
- **Needs Windows verification.** This release changes save targeting, page-box geometry and the annotation canvas, none of which can be exercised on the (macOS) build machine. Worth a smoke test: saving a protected / rotated / cropped document in place; a US Letter form with an inherited MediaBox; image export and Save Flattened output (no black JPEGs, no stray alpha); flowing selection and the three markup tools; polygon export in Acrobat; the sidebar glide at 70% and 250% app scale; and the themed password dialog in all three themes.

## [1.18.0.0] - 2026-07-20

Fork-sync release porting the bug fixes and features from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) **v1.6.3** and **v1.6.4**, adapted to TDPdf's diverged multi-tab architecture. The headline items — a virtualized continuous view, an editable bookmark outline, a visual keyboard map, a headless command-line interface, and the vendored PdfSharpCore with PDF/A conformance patches — are all built on TDPdf's own pipelines.

### Added

- **Editable Outline (bookmark) panel** (upstream v1.6.4, #133). The sidebar outline is now a full bookmark editor: add a bookmark (a "+ Add bookmark" row named in place), inline rename (F2), add child, reorder up/down, retarget to the current page, delete, and delete-all, with Ctrl/Shift multi-select and a right-click menu. Every edit is one Ctrl+Z (rides TDPdf's document-snapshot undo). Read-only (owner-restricted) files show the tree without editing. Built on PdfSharpCore's live outline model so edits are written on save.
- **Headless command-line interface** (upstream v1.6.4). `TDPdf.exe` now runs headlessly with meaningful exit codes while the GUI can be open: `--merge`, `--extract-pages`, `--split`, `--decrypt`, `--to-image`, `--flatten`, `--print`, `--ocr`, `--batch-resave`, `--version`, and `--help`. Each command reuses the pipeline its GUI equivalent runs (merge link rewriting, the pre-save scrubs, lossless PDFium decrypt, the rotation-safe rasterizer, the searchable-PDF OCR builder), so CLI output matches what the app produces. A launch with no recognized command still opens the file in the app as before.
- **Visual keyboard map in the shortcuts overlay** (upstream v1.6.4). The F1 / Ctrl+? overlay gains a LIST / KEYBOARD toggle; in KEYBOARD mode a rendered keyboard lights every bound key, color-coded by category, previews the Ctrl/Shift/Alt layers, and shows each key's action on hover. The choice is remembered and follows the theme.
- **Jump history** (upstream v1.6.4). Alt+Left / Alt+Right and the mouse back / forward buttons retrace bookmark, link, jump-box, and Home/End hops, browser-style, per tab.
- **More keyboard conventions** (upstream v1.6.4): Home / End jump to the first / last page; Ctrl+1 / Ctrl+2 / Ctrl+3 set actual size / fit width / fit page (Ctrl+0 stays the 100% reset); the Menu key or Shift+F10 opens the right-click menu at the current selection.

### Changed

- **Continuous view is virtualized to bound memory** (upstream v1.6.3, #122). Continuous view previously kept a rendered bitmap for every page for the life of the document (a large image-heavy PDF could pin gigabytes). It now keeps bitmaps only for a window of pages around the viewport — rendering pages as they approach and releasing those that leave, with page slot heights held stable so nothing reflows. The per-tab rendered-page cache is also capped, and closing a tab compacts the heap so a big document's memory is actually returned.
- **Two-Page mode navigates a full spread at a time** (upstream v1.6.3, #120). Arrow keys, PgUp/PgDn, and the wheel's edge page-flip now advance one two-page spread instead of one page.
- **Grid view tracks the current page while scrolling** (upstream v1.6.4). The statusbar page counter follows the tile nearest the viewport center as you scroll the grid.

### Fixed

- **Saving no longer writes three classes of structural corruption** (upstream v1.6.3/v1.6.4). Every save now scrubs the document first, and re-saving heals files damaged by other tools: a dangling `/Outlines` reference with no bookmarks is dropped (#103); a zero-size `/CropBox` that Adobe rejects as "page dimensions out-of-range" is dropped (the page falls back to its MediaBox); and a stale digital-signature value plus its `/Perms` entry — which a full rewrite mathematically invalidates — is stripped so strict PDF/A validators accept the result.
- **Intermittent hard crash while scrolling annotation-heavy pages** (upstream v1.6.4). TDPdf's direct `pdfium.dll` calls (the encryption-strip repair path) now hold the same lock Docnet uses, so a direct call can no longer run inside single-threaded PDFium at the same instant as a background page render (native heap corruption).
- **Crash opening a PDF whose page tree parses to zero pages** (upstream v1.6.4, #130): Continuous view is now guarded against the out-of-range page index.
- **Bookmark titles showed as mojibake in password-protected PDFs** (upstream v1.6.4, #133): UTF-16 outline titles that arrive byte-widened with a raw BOM prefix (most visible on Chinese outlines) are now re-decoded for display.
- **Switching from Grid to Continuous clipped zoomed pages** (upstream v1.6.3): Continuous now restores its own scrollbar setup, so Grid's disabled horizontal scrollbar no longer leaks in.
- **Re-saving a PDF no longer reduces its PDF/A conformance** (upstream v1.6.4). PdfSharpCore (MIT) is now vendored under `third_party/` at the same 1.3.67 version, carrying six conformance patches: no Producer/Creator stamping into an imported document's Info dictionary, no `/ModDate` rewrite at open, no transparency `/Group` injected into every page, stream `/Length` always matching the spec byte count (empty streams included), booleans written as lowercase `true`/`false`, and the debug-only verbose file layout removed.

### Notes

- Upstream's PDFium-based link reader was **not** adopted (TDPdf reads links via PdfSharpCore), so upstream's #129 "file in use" fix does not apply here — the underlying cached-handle bug is structurally absent. Upstream's shortcut remap (About→F12/Doc Info→F4), German/Japanese localization, the veraPDF validation harness, and Costura/Fody bundling were skipped (TDPdf keeps F12 = Document Info, is English-only, and uses SDK single-file publish). Deferred within otherwise-ported features: the outline panel's sidebar auto-fit width and in-tree arrow-key navigation.
- **Needs Windows verification.** Several ported areas change or add runtime behavior that could not be exercised on the (macOS) build machine and should get a smoke test on Windows: the continuous-view virtualization (scroll a 200+ page image PDF — pages must fill on approach and never blank or jump), the editable outline TreeView, the visual keyboard map, the CLI commands (`--print`/`--ocr` against real printers/Tesseract in particular), and — because the vendored PdfSharpCore swaps the core PDF writer — a broad save/open regression pass plus a veraPDF conformance check.

## [1.17.0.0] - 2026-07-10

Fork-sync release porting the reopen-documents-on-launch feature from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) v1.6.1 (Issue #105), built on TDPdf's multi-tab `DocumentContext` model (which previously had no session persistence).

### Added

- **Reopen your documents on next launch** (upstream v1.6.1, Issue #105). TDPdf now remembers the set of open documents when you quit and restores them as tabs the next time you open the app, re-selecting the tab that was active. On quit with documents open it asks once whether to reopen them next time, with a **"Remember my choice"** option that stops the prompt (choose Yes to always reopen, or No to never). A document opened directly (double-clicking a PDF, or a file passed on the command line) takes precedence and opens on its own without also restoring the previous session. Only real, saved files are remembered — untitled, merged-on-drop, imported-image, and recovered documents (which have no lasting on-disk home) are never persisted, and a manually-closed tab does not come back. When you choose not to reopen, the remembered file list is cleared so no paths linger on disk.

### Notes

- The reopen preference is stored in a new `ReopenSession` setting (`Ask` by default; `Yes`/`No` once you tick "Remember my choice"); the file list lives in `SessionFiles` / `SessionActiveFile`. There is no separate Settings-panel toggle yet — the quit-time prompt is the control surface, matching upstream.

## [1.16.0.0] - 2026-07-10

Fork-sync release porting the link-safety, print-responsiveness, and page-navigation improvements from upstream [KillerPDF](https://github.com/SteveTheKiller/KillerPDF) v1.6.2, adapted to TDPdf's existing link overlays and themed print dialog.

### Added

- **Page Up / Page Down navigate pages** (upstream v1.6.2). PgUp/PgDn now go to the previous / next page, consistently regardless of what has focus — a focused sidebar thumbnail no longer pages its own selection instead. They never reorder pages (that stays on the toolbar Move Up / Move Down buttons). Folds into TDPdf's existing arrow-key page navigation.
- **Link target shown in the status bar on hover** (upstream v1.6.2). Hovering a PDF link now shows its destination — the URL, or "Go to page N" for an internal jump — in the status bar, restoring the previous status when the pointer leaves.
- **Confirmation before opening an external link** (upstream v1.6.2). Clicking a URL link asks before launching it outside TDPdf, showing the target, with a "Don't ask again" option (persisted in a new `SkipLinkConfirm` setting). Internal go-to-page links jump immediately without a prompt.
- **Scheme-less links open** (upstream v1.6.2). A bare domain-shaped target such as `www.example.com` is opened as `https://…` instead of being ignored.

### Changed

- **Print no longer freezes the window** (upstream v1.6.2). Clicking Print now covers the print card with a progress scrim (spinner + "Preparing page X of N" → "Sending to printer…") and moves the 300 DPI page re-rasterization onto a background thread, so the window keeps painting instead of going grey / "Not Responding" and looking like a crash on large jobs. The Print button is disabled and the scrim swallows clicks so a job can't be double-triggered mid-print.
- **Print preview follows the typed page range** (upstream v1.6.2). Typing a range in the Pages box (e.g. `6` or `2-4`) now filters the preview to exactly those pages, so the preview always shows what will actually print; the 1-up page label reads the real page number (e.g. "Page 6 of 108").

### Fixed

- **Hardened link opening against malicious PDFs** (upstream v1.6.2). A PDF can embed any URI, and the previous click handler passed it straight to the OS shell — a crafted `file://`, UNC path, `javascript:`, or protocol-handler link (e.g. `ms-msdt:` / `search-ms:`) could have been launched. Link clicks are now restricted to an `http` / `https` / `mailto` allow-list; anything else is refused and reported in the status bar. Both the single-page and grid-tile click paths route through one checked choke point, and a failed open is reported instead of being silently swallowed.

### Notes

- Upstream's switch to reading links via PDFium and its asynchronous XPS spool were **not** adopted: TDPdf's existing PdfSharpCore-based link overlays already work across single, continuous, and grid views, and TDPdf keeps its proven synchronous spool with driver-level copy handling (the earlier #83/#107 copy fix). The heavy 300 DPI rasterization — the part that actually blocked the UI — is now off-thread, so the freeze fix is preserved without changing the print-submission path. Upstream's Japanese localization, the KillerFind cross-promotion, and the in-app self-updater checksum change do not apply to TDPdf (English-only strings, no cross-promo, no in-app updater) and were skipped.

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

[Unreleased]: https://github.com/doodlemania2/TDPdf/compare/v1.26.0.0...HEAD
[1.26.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.25.0.0...v1.26.0.0
[1.25.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.24.1.0...v1.25.0.0
[1.24.1.0]: https://github.com/doodlemania2/TDPdf/compare/v1.24.0.0...v1.24.1.0
[1.24.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.23.7.0...v1.24.0.0
[1.23.7.0]: https://github.com/doodlemania2/TDPdf/compare/v1.23.6.0...v1.23.7.0
[1.23.6.0]: https://github.com/doodlemania2/TDPdf/compare/v1.23.5.0...v1.23.6.0
[1.23.5.0]: https://github.com/doodlemania2/TDPdf/compare/v1.23.4.0...v1.23.5.0
[1.23.4.0]: https://github.com/doodlemania2/TDPdf/compare/v1.23.3.0...v1.23.4.0
[1.23.3.0]: https://github.com/doodlemania2/TDPdf/compare/v1.23.2.0...v1.23.3.0
[1.23.2.0]: https://github.com/doodlemania2/TDPdf/compare/v1.23.1.0...v1.23.2.0
[1.23.1.0]: https://github.com/doodlemania2/TDPdf/compare/v1.23.0.0...v1.23.1.0
[1.23.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.22.1.0...v1.23.0.0
[1.22.1.0]: https://github.com/doodlemania2/TDPdf/compare/v1.22.0.0...v1.22.1.0
[1.22.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.21.0.0...v1.22.0.0
[1.21.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.20.0.0...v1.21.0.0
[1.20.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.19.0.0...v1.20.0.0
[1.19.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.18.0.0...v1.19.0.0
[1.18.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.17.0.0...v1.18.0.0
[1.17.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.16.0.0...v1.17.0.0
[1.16.0.0]: https://github.com/doodlemania2/TDPdf/compare/v1.15.0.0...v1.16.0.0
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
