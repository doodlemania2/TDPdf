# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

TDPdf is a Windows-only WPF PDF editor shipped as a single self-contained `TDPdf.exe`. It is a GPLv3 fork of [SteveTheKiller/KillerPDF](https://github.com/SteveTheKiller/KillerPDF); see `NOTICE`. Target is `net9.0-windows` / `win-x64`.

## Build / publish / release

- Dev build (Windows): `dotnet build -c Release`
- Cross-compiling from macOS/Linux (where this repo often lives): add `-p:EnableWindowsTargeting=true`. The app itself only runs on Windows (WPF + native `pdfium.dll` P/Invoke).
- Publish single-EXE: `dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true` → `bin/Release/net9.0-windows/win-x64/publish/TDPdf.exe`. Publish also runs the `BundleSource` MSBuild target (`build/bundle-source.ps1`) producing the GPLv3 corresponding-source zip; it uses `git ls-files`, so untracked files are silently excluded.
- Full signed release: `./release.ps1` (Windows; needs Certum cert + signtool), or `./release.ps1 -SkipSign` for a dry run.
- **There is no test suite or linter.** `dotnet build` warnings are the only lint signal — do not introduce new warnings. Nullable reference types are enabled project-wide; don't silence `CS8602` etc. with `!` unless the invariant is genuinely guaranteed.
- Single-file publish uses SDK properties in `TDPdf.csproj` (`IncludeNativeLibrariesForSelfExtract`, compression); do not reintroduce Costura/Fody bundling. `PublishTrimmed` and Native AOT are intentionally off — WPF and the PDF libraries are not trim-safe.
- **Always check warnings with `--no-incremental`.** An incremental build skips `CoreCompile` and reports only the 2 `MSB3243` warnings — a false clean that hides every `CS*` warning you just introduced. The real baseline is **6 warnings, 0 errors**.

### Release checklist

Bumping the version touches **four** files. They must all land in the **same PR**, because `main` is push-protected and a follow-up fix needs a whole second PR:

1. `TDPdf.csproj` — `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, all three together.
2. `CHANGELOG.md` — a `## [x.y.z.w] - YYYY-MM-DD` section (Keep a Changelog / SemVer) **and** the compare links at the bottom.
3. `build/intune/Detect-TDPdf.ps1` — `$MinVersion`. **Easy to forget, and nothing fails loudly when you do:** the build is green, the release publishes, and the miss only shows up later as Intune reporting every machine already compliant so the new version never installs.
4. `.github/release-notes/v<x.y.z.w>.md` — the human-facing notes. **Required before the tag exists.** `release.yml` passes `--notes-file .github/release-notes/$TAG.md`, so a missing file fails the tagged build outright; historically that single omission is the only reason release runs have failed.

Then, to actually cut a release:

- Merging to `main` builds **nothing**. The only CI on push is `codeql.yml` (required check `Analyze (csharp)`, ~6 min).
- A binary is produced only by `.github/workflows/release.yml`, which is **tag-triggered** (`push: tags: [v*]`) and runs on the self-hosted **`tdpdf`** runner. Tag style is annotated: `git tag -a v1.21.0.0 -m "TDPdf 1.21.0.0"`.
- **Pushing the tag now does the whole thing.** `release.yml` signs the EXE, publishes the GitHub Release (exe + GPLv3 source zip + `SHA256SUMS.txt`), and updates the existing Intune app — see *Automated release pipeline* below. `./release.ps1` remains the manual Windows path (Certum via SimplySign, `-SkipSign` for a dry run) and is still the way to produce a real `.intunewin` for portal work.

#### Automated release pipeline

The `tdpdf` runner is **Linux** (self-hosted ARC on the K3s cluster). That single fact explains the design: `signtool.exe` and `IntuneWinAppUtil.exe` are Windows-only, so the workflow signs with **`osslsigncode`** and talks to **Microsoft Graph directly**. A `.intunewin` is only a transport container for the portal UI — Graph accepts the encrypted payload plus a `fileEncryptionInfo` block, which `build/intune/Deploy-IntuneUpdate.ps1` produces itself.

Each stage is gated on its secret being present, so a missing or rotated secret degrades the run to build-and-release rather than failing it:

| Secret | Enables | If absent |
|---|---|---|
| `CODESIGN_PFX_BASE64`, `CODESIGN_PFX_PASSWORD` | Authenticode signing | **unsigned exe**, with a warning |
| `INTUNE_APP_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` | Intune app updated | Intune step skipped |

- **The Intune step updates an app that already exists and is already targeted.** It adds a `mobileAppContentVersion` and repoints `committedContentVersion`. Assignments hang off the *app*, not the content version, so **targeting is never touched** — the script also refuses to run against anything that is not a `win32LobApp`.
- It PATCHes `displayVersion` and the PowerShell detection rule **in the same call** that commits the content, which is what structurally prevents the old failure mode of a package and its detection script disagreeing about the version.
- `displayVersion` comes from `<AssemblyVersion>` in the csproj, not from the tag, so a mistyped tag cannot mislabel the app.
- Run `Deploy-IntuneUpdate.ps1 -DryRun` to zip, encrypt and digest locally with no Graph writes. Do that first when changing anything in that script.
- `workflow_dispatch` accepts `skip_intune: true` to cut a GitHub Release without touching the fleet.
- The signing and Intune steps only work off Windows — do not "fix" them back to `signtool`/`IntuneWinAppUtil` unless the runner becomes Windows.
- `pdf-landing/` is not part of this ritual — it has been stale for many versions and no workflow deploys it. Leave it alone unless asked.

## Architecture

Single-window WPF app with MVVM foundations but no DI. Almost all UI behavior lives in `MainWindow.xaml.cs` (~8,000 lines): tools, rendering, search, signatures, save/flatten, install/uninstall, print, crop, zoom, dialogs, themes. New UI features usually go there unless there's a strong reason to split.

- `ViewModels/MainWindowViewModel.cs` is a foundation-only stub — per its header comment, do NOT wire it into MainWindow yet; migration happens in separate PRs (issue #18). `Services/` is likewise a placeholder for future extractions (only `ZoomViewModel` and the few existing services are live).
- `Models/Annotations.cs` holds the entire annotation data model: `PageAnnotation` subclasses (`TextAnnotation`, `InkAnnotation`, `HighlightAnnotation`, `TextEditAnnotation`, `ImageEditAnnotation`, `CropAnnotation`, `SignatureAnnotation`) plus `SavedSignature` for JSON persistence.

**Three PDF libraries, each used for what it's best at — don't swap them:**

| Library | Role |
|---|---|
| Docnet.Core (PDFium) | Page rasterization for on-screen rendering and the 150 DPI "Save Flattened" export. Supplies native `pdfium.dll`. |
| PdfSharpCore | Page geometry, merge/split, writing annotations into the saved PDF. |
| PdfPig (aliased `PdfPigDoc`) | Text extraction: search, drag-select copy, font matching for inline text edits. |

**Annotation flow:** `_annotations` is a `Dictionary<int pageIndex, List<PageAnnotation>>`. On-screen annotations live on a transient WPF `AnnotationCanvas` overlay; on save they're baked into the PDF via PdfSharpCore. "Save Flattened" instead rasterizes every page through PDFium so nothing remains editable.

**Other load-bearing behaviors:**
- Custom chrome: `WindowStyle=None` + `AllowsTransparency=True`. A `WM_GETMINMAXINFO` hook in `MainWindow_SourceInitialized` keeps maximize from covering the taskbar — preserve it if touching window sizing. All dialogs are custom dark-themed windows; never use `MessageBox.Show` or default file-dialog chrome for new UI.
- Self-installer: first launch from outside the install path offers Install / Run Portable; installs per-user to `%LOCALAPPDATA%\Programs\TDPdf\` (no UAC), registers ProgID `TDPdf.pdf`, uninstalls via deferred batch file. `TDPdf.exe "file.pdf"` opens directly.
- Password-protected PDFs: prompt for password, write a decrypted copy to a temp file, work against that for the session.
- Signatures persist to `signatures.json` next to the EXE. Drawn (`Strokes`) and imported-image (`ImageData` base64 PNG) variants share `SavedSignature`; `ImageData != null` is the discriminator.
- Telemetry (`Diagnostics/`) is opt-in and a no-op by default — gated on an OTLP collector provisioned by Intune policy under `HKLM\SOFTWARE\Policies\TDPdf\Telemetry` (`OtlpEndpoint` + `OtlpToken`), which exports to self-hosted SigNoz. Never expose a raw `TrackException`; crash reporting goes through `Sanitizer` to scrub paths. Application Insights, the build-time-embedded key and `%ProgramData%\TDPdf\telemetry.dat` were all removed in 1.24.0.0 — do not reintroduce a compiled-in destination.

## Conventions

- XAML-named controls the codegen can't resolve are re-fetched in the constructor via `FindName(...)!` into `_camelCase` fields (see the `// Manual element refs` block). Follow that pattern for new named XAML elements.
- UI palette is centralized in `MainWindow.xaml` resources (`BgDark`, `BgPanel`, `AccentGreen`, `DangerRed`, …) and theme dictionaries under `Themes/` (Dark, Light, HighContrast). Use those brushes; don't hardcode hex colors. Toolbar glyphs are `Segoe MDL2 Assets`.
- Set `_isDirty = true` on any change that mutates the document, and route open/close paths through the existing dirty-check prompts.
- Versioning: see the **Release checklist** under Build / publish / release. Four files move together, and `main` is push-protected, so they must all be in the same PR — there is no fixing one up afterwards without a second PR.
- GPLv3 compliance: keep `LICENSE` and `NOTICE` intact, preserve upstream copyright headers, never reintroduce upstream personal branding. New dialog titles / product strings must say "TDPdf".

<!-- BEGIN derek-task-inbox — shared block, identical in every CLAUDE.md under /Volumes/Data/repos. Edit all copies together. Canonical source: the Outline "Protocol" page linked below. -->

## Handing work back to Derek — the Outline Task Inbox

Derek runs many agents at once, so a task mentioned only in a chat reply is lost. **Anything
that requires Derek personally must be filed as an item in the Outline Task Inbox in the same
turn you discover it**, and then also mentioned in your reply.

- Inbox: [Derek's Task Inbox](https://outline.thedoodleproject.net/doc/dereks-task-inbox-qJpbaMEN3j) — `documentId: ae39277c-130c-4e95-90e0-80d7ed4138e4`
- Full spec: [Protocol — how agents file tasks](https://outline.thedoodleproject.net/doc/protocol-how-agents-file-tasks-lLeYcUvbS4) — canonical if it disagrees with this block

**Which direction is which.** The inbox is work *for Derek*: approvals, decisions, merging to
`main`, entering a credential, vendor-UI clicks (CCB, Stripe, Power Platform, Azure Portal,
Exchange), phone calls, hand-verifying prod. Work *for agents* — bugs, features, refactors,
investigations — is a **GitHub issue** in the owning repo, never an inbox item. If an agent
could do it, do it. Status and findings belong in your reply, not the inbox.

**Filing an item**

1. `mcp__outline__fetch` (`resource: "document"`, `id: "ae39277c-130c-4e95-90e0-80d7ed4138e4"`)
   first. If the task is already filed, patch that item instead of adding a duplicate.
2. `mcp__outline__update_document` with `editMode: "patch"`, `findText` = the target section
   heading, `text` = that heading + a blank line + your new item, so it lands at the top of the
   section. Sections: `## 🔴 Blocking` (an agent or a parishioner is stuck), `## 🟡 Normal`
   (needed soon, nothing stuck), `## 🔵 Whenever`. Delete the `*Nothing pending.*` placeholder
   when a section gets its first real item.

```markdown
- [ ] **<Imperative one-line title>** · `<repo>` · [#123](https://github.com/doodlemania2/<repo>/issues/123) · _YYYY-MM-DD_
  - **Why:** what is broken or blocked until this happens
  - **Do:** the exact steps or command to run
  - **Done when:** the observable condition that means it worked
  - **Context:** IDs, URLs, env/org, file paths — everything needed to act without asking
  - **Filed by:** agent · <branch / worktree / session hint>
```

Every field is required, and the item must stand alone: Derek opens it cold days later with no
memory of the session and finishes it without asking a question. That means real GUIDs and
record IDs, the Dataverse org name (`stfoafrisco-prod` vs `stfoafrisco-staging`), the PR/issue
link, and copy-pasteable commands — not "the affected family" or "the usual script". Never put
a secret value in an item; name the Key Vault secret instead. One task per item.

Never tick, reorder, or delete an item — checking off and archiving are Derek's alone. If a task
you filed became unnecessary, patch it to `~~struck through~~` with the reason and date. If the
Outline MCP is unavailable in your session (headless and cron runs sometimes lack it), say so
explicitly and put the fully-formatted item inline in your reply rather than dropping it.

<!-- END derek-task-inbox -->
