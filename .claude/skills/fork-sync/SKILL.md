---
name: fork-sync
description: Curated fork-sync for TDPdf — pull the useful, not-yet-adopted features and fixes from upstream KillerPDF, adapt them to TDPdf's diverged multi-tab architecture, bump the version, and open a PR. Use when asked to "sync the fork", "pull in upstream", "update from KillerPDF", or do a fork-sync.
---

# TDPdf Fork-Sync

TDPdf is a GPLv3 fork of [SteveTheKiller/KillerPDF](https://github.com/SteveTheKiller/KillerPDF) that has **diverged heavily**: we rebranded everything KillerPDF→TDPdf, added multi-tab documents (`DocumentContext`), telemetry/crash recovery, a startup splash, and our own theme/settings system. A blind `git merge upstream/main` is **never** correct — it would produce thousands of conflicts and reintroduce KillerPDF branding.

Instead, this fork practices **curated, per-feature porting**: identify what upstream added that we genuinely lack, adapt each piece to our architecture, and ship it with a version bump and a CHANGELOG note that credits the upstream version. The CHANGELOG history (e.g. "Ports … from upstream KillerPDF v1.4.2") is the model to follow.

## Ground truth (verify, don't assume)

- Remotes: `origin` = `doodlemania2/TDPdf`, `upstream` = `SteveTheKiller/KillerPDF`.
- `main` is **push-protected** (CodeQL rule blocks direct pushes) → always work on a branch and open a PR.
- `gh pr create` defaults to the **upstream** repo — you MUST pass `--repo doodlemania2/TDPdf`.
- Build (from macOS/Linux where this repo usually lives): `dotnet build -c Release -p:EnableWindowsTargeting=true`. The app only *runs* on Windows (WPF + native pdfium.dll), so you cannot run it here — verify by **clean compile with no new warnings**.
- There is no test suite or linter. Build warnings are the only lint signal. Record the **baseline warning set first** and never add new warning codes/locations.
- Read `CLAUDE.md` before touching code — architecture, the three PDF libraries (don't swap them), branding rules, conventions.

## Procedure

### 1. Establish state
```
git fetch upstream
git rev-list --left-right --count main...upstream/main          # how far ahead/behind
git log --oneline --no-merges main..upstream/main               # what's new upstream
MB=$(git merge-base main upstream/main); git diff --stat $MB upstream/main
git show upstream/main:CHANGELOG.md                             # upstream feature/fix narrative
```
Read **our** `CHANGELOG.md` top-to-bottom too: it records exactly which upstream versions/features we have already ported. Our `<Version>` in `TDPdf.csproj` is independent of upstream's.

### 2. Gap analysis (the real work)
For each upstream feature/fix since our last sync, decide: **already have it? want it? would it break our rebrand?** Don't trust the changelog alone — many upstream features we *appear* to lack we actually implemented independently (and vice-versa). Confirm presence/absence in **our** working tree with `Explore` agents (fan out: one per feature cluster), reporting PRESENT / ABSENT / PARTIAL with file:line evidence.

**Skip** anything that is: branding/landing-page (`pdf-landing/`, README marketing, screenshots, og-images), upstream's extra "edgy" themes (Blood/Greed/Cyanotic), Chocolatey/winget packaging, or localization unless explicitly requested (our product strings differ). **Port** genuine bug fixes and user-facing features.

Group the keepers into clusters by size/risk and present them to the user (an `AskUserQuestion` with multiSelect works well) — confirm scope and whether to deliver as one combined PR + version bump or one PR per cluster.

### 3. Port each cluster (adapt, never blind-copy)
All feature work tends to touch the ~8000-line `MainWindow.xaml.cs`, so **port clusters sequentially, not in parallel worktrees** (parallel edits to that file conflict badly). For each cluster, delegate to a `general-purpose` implementation agent with a precise brief that includes:
- The exact upstream commit(s) to study (`git show <hash> -- MainWindow.xaml.cs`; `git show upstream/main:<file>`).
- Our equivalent code to integrate with (find OUR method, don't replicate upstream's structure).
- The non-negotiables: namespace `TDPdf`, no KillerPDF/Steve/upstream strings in shipping code, theme brushes not hex, custom dark dialogs only (never `MessageBox.Show`), set `MarkDirty()`/`_isDirty` on mutations, per-document state on `DocumentContext`, nullable-clean (no new warnings, no `!` unless guaranteed).
- The build command + baseline warning count, and "do NOT bump version / edit CHANGELOG / commit — that's done centrally."

**Review every agent's output yourself** — read the diff, don't just trust the report. Real bugs slip through (e.g. a "repair" that silently discards the user's edit). Verify the build and warning count after each cluster.

### 4. Finalize (once, centrally)
- Bump `<Version>`, `<AssemblyVersion>`, `<FileVersion>` together in `TDPdf.csproj` (minor bump for features, patch for fixes-only).
- Add one `## [x.y.z.0] - YYYY-MM-DD` section to `CHANGELOG.md` (Keep a Changelog: Added/Changed/Fixed), each entry crediting the upstream version it came from. Add the compare link at the bottom.
- Final clean build: `dotnet build -c Release -p:EnableWindowsTargeting=true --no-incremental` — confirm 0 errors and only baseline warning codes.
- Branding leak scan: `git diff HEAD~1 --name-only | grep -E '\.(cs|xaml)$' | xargs grep -nI "KillerPDF\|thekiller\|SteveTheKiller"` — the only allowed hits are pre-existing GPL attribution (e.g. the About dialog's "Forked from …") and code comments; no new product strings.
- Commit (end message with the `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` trailer), push the branch, and:
  ```
  gh pr create --repo doodlemania2/TDPdf --base main --head <branch> --title "…" --body "…"
  ```
  End the PR body with the Claude Code generated-with line.

### 5. Keep branches aligned
After the PR merges, fast-forward local `main` to `origin/main` and delete the merged sync branch. Leave `upstream/main` tracked for the next sync. Do not force-push `main`.

## Guardrails
- If a feature can't be ported faithfully without destabilizing existing behavior (e.g. our render pipeline is hardcoded to a single annotation canvas), implement the safe subset, **say so explicitly** in the report and CHANGELOG, and don't ship something half-broken.
- Preserve load-bearing behaviors noted in CLAUDE.md (the `WM_GETMINMAXINFO` maximize hook, `SafeWrapPanel` grid hardening, the self-installer, password/temp-file handling, opt-in telemetry).
- GPLv3: keep `LICENSE`/`NOTICE` intact, preserve upstream copyright headers, never reintroduce upstream personal branding.
