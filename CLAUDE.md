# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

TDPdf is a Windows-only WPF PDF editor shipped as a single self-contained `TDPdf.exe`. It is a GPLv3 fork of [SteveTheKiller/KillerPDF](https://github.com/SteveTheKiller/KillerPDF); see `NOTICE`. Target is `net10.0-windows` / `win-x64`.

## Build / publish / release

- Dev build (Windows): `dotnet build -c Release`
- Cross-compiling from macOS/Linux (where this repo often lives): add `-p:EnableWindowsTargeting=true`. The app itself only runs on Windows (WPF + native `pdfium.dll` P/Invoke).
- Publish single-EXE: `dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true` → `bin/Release/net10.0-windows/win-x64/publish/TDPdf.exe`. Publish also runs the `BundleSource` MSBuild target (`build/bundle-source.ps1`) producing the GPLv3 corresponding-source zip; it uses `git ls-files`, so untracked files are silently excluded.
- Local build: `./release.ps1` (Windows) is an **unsigned** local build only (no Certum, no signtool). Signed releases come only from pushing a tag, which runs `.github/workflows/release.yml`.
- **Tests: `tests/PdfCore`** is a macOS/Linux/Windows-runnable console harness (about 2,050 checks) covering the pure logic in `Services/` and the PDF round-trips. Run `dotnet run --project tests/PdfCore/PdfCore.Tests.csproj -c Release`; it must print **ALL PASS**. It gates `release.yml` (nothing is signed unless it passes). Add tests for any pure logic you add or move in `Services/`: compile the service file into the test project with a `<Compile Include>` in `tests/PdfCore/PdfCore.Tests.csproj` and register the suite in `tests/PdfCore/Program.cs`. There is no linter; `dotnet build` warnings are the lint signal — do not introduce new warnings. Nullable reference types are enabled project-wide; don't silence `CS8602` etc. with `!` unless the invariant is genuinely guaranteed.
- Single-file publish uses SDK properties in `TDPdf.csproj` (`IncludeNativeLibrariesForSelfExtract`, compression); do not reintroduce Costura/Fody bundling. `PublishTrimmed` and Native AOT are intentionally off — WPF and the PDF libraries are not trim-safe.
- **Always check warnings with `--no-incremental`.** An incremental build skips `CoreCompile` and can report a false clean that hides every `CS*` warning you just introduced. The baseline is **0 warnings, 0 errors** (it was 4 before 2.2.1.0 cleared the `MSB3243` and `CS8602` ones); keep it there.

### Release checklist

Bumping the version touches **three** files (plus the licence check below when it applies). They must all land in the **same PR**, because `main` is push-protected and a follow-up fix needs a whole second PR:

1. `TDPdf.csproj` — `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, all three together.
2. `CHANGELOG.md` — a `## [x.y.z.w] - YYYY-MM-DD` section (Keep a Changelog / SemVer) **and** the compare links at the bottom.
3. `.github/release-notes/v<x.y.z.w>.md` — the human-facing notes. **Required before the tag exists.** `release.yml` passes `--notes-file .github/release-notes/$TAG.md`, so a missing file fails the tagged build outright; historically that single omission is the only reason release runs have failed.
   The notes are also the source of tdpdf.com's `/changelog/`, so write them for the people who use TDPdf, under the fleet copy rules: no issue numbers, commit hashes, internal system names, money figures, TODOs or hype words, no em dashes, and only what the product does today. Every file from v2.0.1.0 on follows this; older ones don't, and should not be used as a template.

4. **Microsoft Store** — nothing to edit, but know it fires. `release.yml` copies the signed exe to `https://tdpdfreleases.blob.core.windows.net/releases/<tag>/TDPdf-<tag>-win-x64.exe` and repoints the Store listing there — **not** at the GitHub release asset, because Partner Center rejects any installer URL that redirects and GitHub assets always do. TDPdf is listed as an **"EXE or MSI app"**, not MSIX: the Store holds a URL to our own installer rather than a package, which is what lets the single-file exe, the self-installer, the Linux runner and the Intune channel all stay as they are. The Store does **not** re-sign our binary, so the step is skipped for an unsigned build — submitting one would be rejected at certification. Gated on `MSSTORE_CLIENT_ID`, `MSSTORE_PRODUCT_ID` **and the repo variable `MSSTORE_ENABLED=true`**, which stays unset until the first submission is published (so a release never touches a submission that is in certification); the workflow only updates an existing submission — the first one is made by hand in Partner Center; `workflow_dispatch` accepts `skip_store: true`. The submission itself is `build/store/publish-store.py` (Store submission API for EXE/MSI apps, called directly; the msstore CLI is not on NuGet). To send an **already-published** release to the Store without rebuilding it, run `.github/workflows/store.yml` (workflow_dispatch, input `tag`): it verifies the release exe against `SHA256SUMS.txt`, copies it to the blob URL and submits. The Store does not push updates to people who already installed; it only serves the newest version to new installs.

Then, to actually cut a release:

- Merging to `main` builds **nothing**. The only CI on push is `codeql.yml` (required check `Analyze (csharp)`, ~6 min).
- **Runner rule.** Anything triggered by `pull_request` runs on the isolated `tdpdf-pr` scale set (`codeql.yml`): ephemeral, no sudo, no docker, no secrets, internet-only egress. Never add a secret to a PR-triggered job. The self-hosted `tdpdf` runner is on the internal network with passwordless sudo, so it only takes triggers outside contributors cannot reach: tag pushes and `workflow_dispatch`. The fork-PR approval policy is `all_external_contributors`. Derek wants every job on our own runners: `release.yml`, `store.yml` and `pages.yml` are on `tdpdf`; `codeql.yml` is on `tdpdf-pr`; `screenshots.yml` and `esign-smoke.yml` need an interactive Windows desktop. Derek decided on 2026-11-15 (recorded in fleet-presence STANDARD.md) to use a self-hosted Windows runner, `vm100-tdpdf` on pve VM 100 (labels `tdp-windows`, `tdp-windows-capture`, `tdp-windows-build`), which replaces the 2026-10-19 "no self-hosted Windows runner" rule. `screenshots.yml` runs there once the repo variable `CAPTURE_RUNNER` is `tdp-windows-capture` (hosted `windows-2022` until then). It runs after every successful Release (`workflow_run`), on `workflow_dispatch` and on `screenshots/**` pushes, and only for the repo owner; tdpdf-site fetches its `screenshots` artifact. Any job on a `tdp-windows*` label must start only from push, `workflow_dispatch` or a `workflow_run` of a workflow that itself meets this rule, never `pull_request` or `pull_request_target`, and must carry `if: github.actor == github.repository_owner`. `esign-smoke.yml` stays on hosted `windows-2022` so signing-account credentials never reach the VM, and tdpdf-fleet's Intune deploy stays on hosted `windows-latest` (its Graph credentials on VM 100 are Derek's call).
- A binary is produced only by `.github/workflows/release.yml`, which is **tag-triggered** (`push: tags: [v*]`) and runs on the self-hosted **`tdpdf`** runner. Tag style is annotated: `git tag -a v1.21.0.0 -m "TDPdf 1.21.0.0"`.
- **`release.yml` gates** (all run before anything is signed or published): the tag must match `vN.N.N.N` and equal the csproj `<AssemblyVersion>`; the tagged commit must be reachable from `origin/main`; on a tag push it refuses to overwrite an existing GitHub release (a `workflow_dispatch` re-release of a tag still works, which is the documented way to redo one in place); the `tests/PdfCore` harness must print ALL PASS; and it warns (does not fail) when the ImageSharp licence is within 60 days of expiry.
- **Pushing the tag now does the whole thing.** `release.yml` signs the EXE, publishes the GitHub Release (exe + GPLv3 source zip + `SHA256SUMS.txt`), updates the Store, and tells the private `doodlemania2/tdpdf-fleet` repo to deploy to Intune — see *Automated release pipeline* below. `./release.ps1` is only an unsigned local build.

#### Automated release pipeline

The `tdpdf` runner is **Linux** (self-hosted ARC on the K3s cluster), so the workflow signs with **Jsign** against Azure Artifact Signing (whose own tooling is a Windows-only signtool dlib). Do not "fix" it back to `signtool` unless the runner becomes Windows.

Each stage is gated on its secret being present, so a missing or rotated secret degrades the run to build-and-release rather than failing it:

| Secret | Enables | If absent |
|---|---|---|
| `SIGN_TENANT_ID`, `SIGN_CLIENT_ID`, `SIGN_CLIENT_SECRET` | Azure Artifact Signing — the publicly-trusted signature the Store and the fleet require | **unsigned exe**, with a warning; Store and fleet skipped |
| `MSSTORE_*` | Store listing repointed at the new exe | Store step skipped |
| `FLEET_DISPATCH_TOKEN` (optional) | `repository_dispatch` `tdpdf-released` to `doodlemania2/tdpdf-fleet`, so Intune updates at once | the fleet's scheduled check deploys it eventually (every 3-5 hours in practice). **Set** since 2026-11-08 |

**Intune lives in the private repo `doodlemania2/tdpdf-fleet`** (#178), not here: the deploy script, the detection and telemetry-policy scripts, the tenant ids and the Graph credentials. On the dispatch above (or its fallback schedule) its workflow compares the latest public release with the version Intune reports. It downloads the exe from the public release, refuses it unless the SHA-256, the Authenticode signature (`CN=The Doodle Project`) and the file version all match the tag, stamps the detection rule's minimum version from the tag, and adds a content version to the existing Intune app (assignments untouched). `FLEET_DISPATCH_TOKEN` **is set** (2026-11-08), so a signed release reaches Intune within minutes. It holds Derek's `gh` login token (repo + workflow scope), because GitHub has no API to mint the narrower fine-grained token (Contents: write on tdpdf-fleet only). If Derek logs `gh` in again that token is revoked: the dispatch step then fails the release run (the release itself is already published), so reset it with `gh auth token | gh secret set FLEET_DISPATCH_TOKEN --repo doodlemania2/TDPdf`. The fleet's cron says every 15 minutes, but GitHub ran it only every 3-5 hours (2026-11-07), so don't rely on the schedule. `workflow_dispatch` here accepts `skip_intune: true`; the fleet's own Actions tab redeploys any tag. Nothing about Intune goes back into this public repo. `docs/intune-distribution.md` stays public as the general admin guide (switches, scopes, detection, policy keys) for anyone deploying TDPdf.

- Signing is **Azure Artifact Signing** (account `tdp`, profile `TDPCertProfile`, endpoint `https://eus.codesigning.azure.net`), reached with Jsign 7.5 pinned by SHA-256. The private key lives in Microsoft's HSMs and never touches the runner. Its certificates last **three days**, so the service timestamps every signature itself — do not add a `--tsaurl`. The old private-PFX fallback was retired after v2.0.0.0 shipped through Artifact Signing.
- The public site is **tdpdf.com**, built from the private repo `doodlemania2/tdpdf-site` (moved out of `build/site/` on 2026-10-31). That repo checks this one out and reads `PRIVACY.md`, `THIRD-PARTY-NOTICES.md` and `Resources/splash-logo.png` from it, so those three stay here and the privacy policy still changes in the same PR as the app change. It rebuilds on a 15-minute fingerprint check (latest release plus those files), on a push there, and on a `tdpdf-release` dispatch that `release.yml` sends when the optional `SITE_DISPATCH_TOKEN` secret exists (not set; the check covers it). It deploys to the edge registry and a doodlessd runner; the container publishes only on `127.0.0.1:18106` and the shared Cloudflare tunnel `public-fleet` (K3s repo, `services/edge/public-fleet/config.yml`) is the only way in. Nothing to run by hand after a release. Do not reintroduce a CMS (Ghost was retired 2026-10-23). The screenshot demo PDFs live in `build/screenshots/demo/` (used by `screenshots.yml` and the TextSearch test).

## Architecture

Single-window WPF app with no DI and no view-model layer beyond `ZoomViewModel`. Most UI behavior lives on the `MainWindow` class, which is **split across one `partial class` per feature area** — `MainWindow.xaml.cs` (about 8,500 lines) plus the files below. `MainWindow.xaml.cs` is still the largest by far and still holds everything that has not been extracted: window chrome, menus, settings, view modes, link overlays, tools (crop, redaction, shapes, form-field authoring), selection, annotation management and rendering, measure, dirty tracking, zoom, drag/drop, MRU, print, themes, install/uninstall. New UI features go in the partial that owns the area, or in `MainWindow.xaml.cs` when no partial fits.

| File | Holds |
|---|---|
| `MainWindow.Files.cs` | File operations (open/save/save-as/flatten/merge/split) + the file toolbar handlers |
| `MainWindow.Canvas.cs` | Canvas interaction — pointer/mouse handling, panning, tool gestures, annotation hit-testing, drag/resize |
| `MainWindow.TextEditing.cs` | Inline text editing (double-click) + text box handling |
| `MainWindow.Forms.cs` | Interactive AcroForm field overlays (form filling) |
| `MainWindow.Signatures.cs` | Signatures: draw/import/place, `signatures.json` store |
| `MainWindow.ContinuousView.cs` | Continuous (vertical-strip) view |
| `MainWindow.Sidebar.cs` | The page sidebar (PageList) / page viewer and multi-document tab switching |
| `MainWindow.Bookmarks.cs` | Bookmark editing (#133): add / rename / child / reorder / retarget / delete |
| `MainWindow.Undo.cs` | Snapshot-based undo helpers |
| `MainWindow.Placement.cs` | Remembers the main window's position, size and maximized state; opens a torn-off tab's window beside its source (rules in `Services/WindowPlacementMath.cs`) |
| `MainWindow.ThumbOverlay.cs` | Unsaved annotations drawn on the Pages sidebar's thumbnails |
| `MainWindow.HighlightComposite.cs` | Highlights as a multiply blend on screen, matching how they are saved and printed |
| `MainWindow.Session.cs` | Offering the last session back at launch (the save/merge side and `RestoreSessionAsync` live in `MainWindow.xaml.cs`) |
| `MainWindow.WhatsNew.cs` | "What's new": the release notes of every version since the last one seen, shown once after an update |
| `MainWindow.MultiSelect.cs` | The mark selection set (Shift/Ctrl click, drag box, move/nudge/delete together, restyle together) and grouping |
| `MainWindow.MarkClipboard.cs` | Copy / cut / paste of marks, pictures and text (private clipboard format, JSON) |
| `MainWindow.MarkArrange.cs` | Align, space evenly and match size of selected marks (geometry in `Services/MarkArrange.cs`) |
| `MainWindow.ContextMenus.cs` | Every right-click menu, built for what is under the pointer from `Services/ContextMenuModel.cs` (quick-action row, colour/opacity/width rows, the user's order from `Services/MenuLayouts.cs`, "Edit Menu…" in `TdpDialog.EditMenu.cs`) |
| `MainWindow.Search.cs` | Search bar (Ctrl+F) and match navigation |
| `MainWindow.Keyboard.cs` | Keyboard shortcut dispatch |
| `MainWindow.KeyboardMapOverlay.cs` | Visual keyboard for the F1 / Ctrl+? shortcuts overlay |
| `MainWindow.ToolSettings.cs` | Tool settings bars (draw, crop, text, shape) and the colour palette / picker |
| `MainWindow.TabDrag.cs` | Cross-window tab drag (uses `Services/WindowTransfer.cs`) |
| `MainWindow.AnnotationBake.cs` | Baking annotations into the saved PDF |
| `MainWindow.TextSelection.cs` | Flowing text selection and line-hugging markup |
| `MainWindow.Ocr.cs` | OCR (Tesseract): recognize text on a page/region, searchable-PDF export |
| `MainWindow.Transform.cs` | Transform tool (fine-angle rotate, scale, flip, straighten) |
| `MainWindow.DocInvert.cs` | Invert document colours / night mode |
| `MainWindow.ExportImages.cs` | Export pages as PNG/JPEG |
| `MainWindow.AppScale.cs` | App-wide UI chrome scale |
| `MainWindow.BatchMode.cs` | Headless `--batch-resave` CLI mode |
| `MainWindow.Cli.cs` | Dispatcher for every headless CLI command |
| `MainWindow.ESignature.cs` | Electronic Signature (Documenso): File menu handlers, settings section, connect-on-demand |
| `MainWindow.ESignatureFields.cs` | Electronic Signature: field placement mode on the canvas |
| `MainWindow.ESignatureRequests.cs` | Electronic Signature: Signature Requests, downloads, signed-document state |
| `MainWindow.ESignatureSelfSign.cs` | Electronic Signature: Sign This Document |
| `MainWindow.ESignatureVerify.cs` | Electronic Signature: Verify Signatures and Document Info digital signatures |
| `MainWindow.ESignatureWeb.cs` | Electronic Signature: the signing service's pages inside TDPdf (WebView2) |

`TdpDialog` is **not** a `MainWindow` partial — it is the standalone themed `MessageBox` replacement, a `static partial class` split across `TdpDialog.cs` and `TdpDialog.PageRange.cs` (the page-range prompt). Alongside `Services/`, `ViewModels/` and `Diagnostics/`, `Models/` holds `Annotations.cs` and `PageThumbItem.cs`, and `Controls/` holds the small reusable WPF controls and layout helpers (`SafeCanvas`, `SafeWrapPanel`, `GridLengthAnimation`, `RadioGroupKeyboard`, `LayoutRace`).

These files were produced by a pure move-only split; the banner comments (`// ====`) inside them are the original region markers, so a region's members are still in their original order. Private nested types (`DocumentContext`, `UndoEntry`, `RenderedPage`, `LinkInfo`, `FormFieldInfo`, …) may be declared in any partial — most remain in `MainWindow.xaml.cs`.

- There is no `MainWindowViewModel` (the never-instantiated stub was deleted in 2.2.1.0). `ViewModels/` holds only `ZoomViewModel`. `Services/` holds the extracted, UI-independent logic (mostly pure and covered by `tests/PdfCore`): PDF read/write helpers (`PdfItems`, `PdfNamedDestinations`, `PdfSaveNormalizer`, `PdfFormText`, `PageRaster`), `TempFiles`, `ZipLimits`, `OcrModelCatalog`, the Documenso client, printing, and so on. `Diagnostics/PrivilegedPathGuard` guards privileged file paths.
- `Models/Annotations.cs` holds the entire annotation data model: `PageAnnotation` subclasses (`TextAnnotation`, `InkAnnotation`, `HighlightAnnotation`, `TextEditAnnotation`, `ImageEditAnnotation`, `CropAnnotation`, `SignatureAnnotation`) plus `SavedSignature` for JSON persistence.

**Three PDF libraries, each used for what it's best at — don't swap them:**

| Library | Role |
|---|---|
| Docnet.Core (PDFium) | Page rasterization for on-screen rendering and the 150 DPI "Save Flattened" export. Docnet provides the managed wrapper only; `bblanchon.PDFium.Win32` supplies the native `pdfium.dll`. |
| PdfSharpCore | Page geometry, merge/split, writing annotations into the saved PDF. |
| PdfPig (aliased `PdfPigDoc`) | Text extraction: search, drag-select copy, font matching for inline text edits. |

**Annotation flow:** `_annotations` is a `Dictionary<int pageIndex, List<PageAnnotation>>`. On-screen annotations live on a transient WPF `AnnotationCanvas` overlay; on save they're baked into the PDF via PdfSharpCore. "Save Flattened" instead rasterizes every page through PDFium so nothing remains editable.

**Other load-bearing behaviors:**
- Window frame: the native frame is the default (`UseNativeWindowFrame`, `Properties/Settings.cs`, applied at startup in `ApplyInitialWindowChromeSettings`, so changing it needs a restart). The custom chrome is the opt-out: `WindowStyle=None` + `AllowsTransparency=True`. The custom frame's `WM_GETMINMAXINFO` hook in `MainWindow_SourceInitialized` keeps maximize from covering the taskbar — preserve it if touching window sizing. All dialogs are custom dark-themed windows; never use `MessageBox.Show` or default file-dialog chrome for new UI.
- Self-installer: launching from outside the install path runs portable with a status-bar "Install TDPdf..." button (`MainWindow.Install_Click`); if TDPdf is already installed per-user it shows the installed version and offers Upgrade / Reinstall / Uninstall into the DETECTED scope (decision in `Services/PortableInstallPolicy.cs`), and a machine-wide (Intune) install is only explained, never overwritten or removed. A fresh install installs per-user to `%LOCALAPPDATA%\Programs\TDPdf\` (no UAC), registers ProgID `TDPdf.pdf`, uninstalls via deferred batch file. `TDPdf.exe "file.pdf"` opens directly.
- Password-protected PDFs: prompt for password, write a decrypted copy to a temp file, work against that for the session.
- Signatures persist to `signatures.json` in `%LOCALAPPDATA%\TDPdf` (a copy left next to the EXE by older versions is migrated on first read, never overwriting). Drawn (`Strokes`) and imported-image (`ImageData` base64 PNG) variants share `SavedSignature`; `ImageData != null` is the discriminator.
- **`PRIVACY.md` is the published privacy policy** (tdpdf.com/privacy, Help ▸ Privacy Policy). Any change to what `Diagnostics/` collects or what Electronic Signature sends updates it in the same PR. Keep it written for users: no maintainer notes or version history in it.
- Telemetry (`Diagnostics/`) is opt-in and a no-op by default — gated on an OTLP collector provisioned by Intune policy under `HKLM\SOFTWARE\Policies\TDPdf\Telemetry` (`OtlpEndpoint` + `OtlpToken`), which exports to self-hosted SigNoz. Never expose a raw `TrackException`; crash reporting goes through `Sanitizer` to scrub paths. Application Insights, the build-time-embedded key and `%ProgramData%\TDPdf\telemetry.dat` were all removed in 1.24.0.0 — do not reintroduce a compiled-in destination.

## Conventions

- XAML-named controls the codegen can't resolve are re-fetched in the constructor via `FindName(...)!` into `_camelCase` fields (see the `// Manual element refs` block). Follow that pattern for new named XAML elements.
- UI palette is centralized in `MainWindow.xaml` resources (`BgDark`, `BgPanel`, `AccentGreen`, `DangerRed`, …) and theme dictionaries under `Themes/` (Dark, Light, HighContrast). Use those brushes; don't hardcode hex colors. Toolbar glyphs are `Segoe MDL2 Assets`.
- Call `MarkDirty()` on any change that mutates the document (it sets `_isDirty` and recolours the Save button), and route open/close paths through the existing dirty-check prompts.
- **App icon.** `Resources/tdpdf-icon.ico` is generated by `build/generate-app-icon.py` (needs Pillow, build-time only — the checked-in `.ico` is what the build consumes). It lifts the gold mark out of `Resources/splash-logo.png` at generation time, so changing the brand logo and re-running keeps them in sync. Three artworks, not one scaled down: 48px and up carry ruled lines, 32px drops them, 24px and below give the mark most of the page — a thin-stroked glyph goes sub-pixel at 16px otherwise. The ICO container is hand-written for that reason; Pillow's writer rescales one source and cannot take per-size artwork.
- **Third-party notices.** TDPdf ships as one self-contained exe, so MIT/BSD/Apache notices must travel inside the binary. `THIRD-PARTY-NOTICES.md` is generated by `build/generate-third-party-notices.ps1`, embedded via `EmbeddedResource`, shown under Help > Third-Party Licenses, and attached to each release. **Regenerate it whenever a `PackageReference` changes in `TDPdf.csproj` or `third_party/PdfSharpCore/PdfSharpCore.csproj`** — nothing fails if you forget, the notices just go stale. The native payloads (PDFium's 1,300-line aggregate, Leptonica, the Tesseract engine) are invisible to licence scanners and are curated by hand in that script.
- **ImageSharp licence.** SixLabors.ImageSharp 4.x (via `third_party/PdfSharpCore`) fails a **Release** build without a Six Labors licence. `third_party/PdfSharpCore/sixlabors.lic` is TDPdf's free community licence, scoped to the assemblies `TDPdf`, `PdfSharpCore` and `PdfCore.Tests`, and expires **2028-01-14**: renew it at licensing.sixlabors.com before then (`release.yml` warns when a release is cut within 60 days of expiry). Six Labors allows committing it publicly. Don't delete it, and don't drop back to ImageSharp 2.x/3.x, which carry unfixed advisories.
- Versioning: see the **Release checklist** under Build / publish / release. Three files move together, and `main` is push-protected, so they must all be in the same PR — there is no fixing one up afterwards without a second PR.
- **Feature list.** `presence/features.json` lists every user-facing feature for tdpdf.com and its docs, checked against `schemas/features.schema.json` in the private `doodlemania2/fleet-presence` repo. Add an entry (`status: building`) when a new feature starts, flip it to `ready` in the PR that ships it (that flip drafts a social post), and keep each feature's `sources` globs pointing at the files that implement it, because they decide which screenshots a push re-captures. Summaries follow the release-notes copy rules.
- GPLv3 compliance: keep `LICENSE` and `NOTICE` intact, preserve upstream copyright headers, never reintroduce upstream personal branding. New dialog titles / product strings must say "TDPdf".

<!-- BEGIN derek-task-inbox — shared block, identical in every CLAUDE.md under /Volumes/Data/repos. Edit all copies together. Canonical source: the Outline "Protocol" page linked below. -->

## Handing work back to Derek — a Paperclip task

Derek runs many agents at once, so a task mentioned only in a chat reply is lost. **Anything
that requires Derek personally must be filed as a Paperclip task in the same turn you discover
it**, and then also mentioned in your reply. The Outline Task Inbox closed on 2026-10-01; its
page is read-only history, so do not append to it.

- Board: company **TDP** at `https://m4.tail890bc.ts.net:8443` (Paperclip API or the
  `create_task` tool), in the project that owns the work
- Full spec: [Protocol — how agents file tasks](https://outline.thedoodleproject.net/doc/protocol-how-agents-file-tasks-lLeYcUvbS4) — canonical if it disagrees with this block

**Which direction is which.** A Paperclip task is work *for Derek*: approvals, decisions,
merging to `main`, entering a credential, vendor-UI clicks (CCB, Stripe, Power Platform, Azure
Portal, Exchange), phone calls, hand-verifying prod. Work *for agents* — bugs, features,
refactors, investigations — is a **GitHub issue** in the owning repo. If an agent could do it,
do it. Status and findings belong in your reply.

**Filing a task.** Search the board first; if it is already filed, comment on that task instead
of filing a second copy. The description carries **Why**, **Do (Derek)**, **Done when** and
**Context**, and must stand alone: Derek opens it cold days later and finishes it without asking
a question. That means real GUIDs and record IDs, the Dataverse org name (`stfoafrisco-prod` vs
`stfoafrisco-staging`), the PR/issue link, and copy-pasteable commands. Never put a secret value
in a task; name the Key Vault secret instead. Set priority `high` (an agent or a parishioner is
stuck), `medium` or `low`, and status `blocked` with an unblock action naming what Derek does.
A decision also gets an `ask_user_questions` card with the real options. Give the task
identifier in your reply. There is nothing to tick or sweep; closing the task is the whole of it.

If your session cannot reach the board (no `create_task` tool and no Paperclip credentials —
the usual case for a local Claude Code session), say so explicitly and put the fully-formatted
task inline in your reply rather than dropping it.

<!-- END derek-task-inbox -->

## Worktrees

Follow the shared convention in `/Volumes/Data/repos/CLAUDE.md`:

- Create: `/Volumes/Data/repos/_tools/wt new TDPdf <issue#>-<slug> [branch]`. It freshens the primary and cuts `/Volumes/Data/repos/TDPdf.worktrees/<issue#>-<slug>` from `origin/main`.
- Reclaim: `/Volumes/Data/repos/_tools/wt done TDPdf <issue#>-<slug>` once the branch has landed on `main`.
- Leave the primary checkout on `main` and clean. Do not use `git stash`, because all worktrees share one stash stack.
