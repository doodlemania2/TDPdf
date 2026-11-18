---
name: tdpdf-release
description: >-
  Ship a TDPdf version end to end: the version-bump PR, the tag, signing, the GitHub release, the Microsoft
  Store submission, the Intune rollout through tdpdf-fleet, tdpdf.com's download and changelog pages, the
  fleet-presence dispatches and the post-release screenshots, and how to prove each one landed. Use when Derek
  says "merge and deploy", "cut a release", "ship 2.x", "send to the fleet and Microsoft", "push it to Intune"
  or "update the Store", or when a release run failed and needs redoing.
---

# Releasing TDPdf

General rules (one PR, push once, "done" means landed, what needs Derek's yes) are in the `tdp-house-rules`
skill. Load it first. This skill covers only the TDPdf release. `CLAUDE.md` in this repo is the source of truth
and wins if the two disagree.

## What a release is

One tag push does everything. `.github/workflows/release.yml` runs on the self-hosted Linux runner `tdpdf` and:

1. checks the tag, the version and that the commit is on `origin/main`;
2. runs the `tests/PdfCore` harness (must print ALL PASS);
3. publishes the single-file exe and signs it with Azure Artifact Signing (Jsign, not signtool);
4. creates the GitHub release (exe, GPLv3 source zip, `SHA256SUMS.txt`);
5. copies the exe to the Store's direct-download URL and submits it to the Store;
6. sends `tdpdf-released` to `doodlemania2/tdpdf-fleet`, which deploys to Intune.

The Store, Intune and the site then follow by themselves. Nothing runs by hand after the tag.

There is **no MSIX build** and no VM 100 build step. TDPdf is listed in the Store as an "EXE or MSI app", so the
Store gets a URL to our own signed exe. Do not add MSIX packaging or move the release onto `tdp-windows-build`.

Tagging is a public release, so do it only on Derek's explicit go ("merge and deploy" counts).

## 1. The version-bump PR

All of these land in one PR. `main` is push-protected; required check `Analyze (csharp)`.

- `TDPdf.csproj`: `<Version>`, `<AssemblyVersion>` and `<FileVersion>`, all the same `x.y.z.w`.
- `CHANGELOG.md`: rename `## [Unreleased]` content into `## [x.y.z.w] - YYYY-MM-DD`, leave an empty
  `## [Unreleased]` above it, and update the compare links at the bottom
  (`[Unreleased]: .../compare/vx.y.z.w...HEAD` plus a new `[x.y.z.w]: .../compare/v<previous>...vx.y.z.w`).
- `.github/release-notes/vx.y.z.w.md`: **required**. A missing file fails the tagged run, and it is the
  historic cause of failed releases. It becomes tdpdf.com/changelog/, so write it for users: no issue
  numbers, commit hashes, internal system names, money figures, TODOs, hype words or em dashes, and only
  what the product does today. Copy the shape of a recent file (v2.0.1.0 or later), never an older one.
- `presence/features.json`: flip any feature this release ships from `building` to `ready`. That flip is
  what sends `feature-shipped` (step 6), and it fires when this PR merges to `main`.

Check before pushing:

```sh
dotnet build TDPdf.csproj -c Release --no-incremental -p:EnableWindowsTargeting=true   # 0 warnings, 0 errors
dotnet run --project tests/PdfCore/PdfCore.Tests.csproj -c Release                     # ALL PASS
```

Open the PR with `--repo doodlemania2/TDPdf` (gh otherwise targets upstream KillerPDF). Merge it once the
check is green and Derek has said go.

## 2. Tag

From the primary checkout, after the merge:

```sh
git -C /Volumes/Data/repos/TDPdf fetch origin
git -C /Volumes/Data/repos/TDPdf tag -a vx.y.z.w -m "TDPdf x.y.z.w" origin/main
git -C /Volumes/Data/repos/TDPdf push origin vx.y.z.w
```

The tag must equal `<AssemblyVersion>` exactly (four parts, `v` prefix). A tag push refuses to overwrite an
existing release.

To **redo** a release in place (for example after a Store or fleet failure), do not re-tag. Run
`gh workflow run release.yml --repo doodlemania2/TDPdf -f tag=vx.y.z.w`; it also accepts
`-f skip_store=true` and `-f skip_intune=true`.

## 3. Watch the run

```sh
gh run list --repo doodlemania2/TDPdf --workflow release.yml --limit 1
gh run watch <run-id> --repo doodlemania2/TDPdf --exit-status    # run it in the background, not a foreground sleep
gh run view <run-id> --repo doodlemania2/TDPdf --log-failed      # on failure
```

The run summary has a table: **Signed** must say `yes`, **Intune** `dispatched to tdpdf-fleet`, **Store**
`submitted`. A failed run also posts to ntfy.

## 4. Signing

- Azure Artifact Signing, account `tdp`, profile `TDPCertProfile`, via Jsign. Secrets `SIGN_TENANT_ID`,
  `SIGN_CLIENT_ID`, `SIGN_CLIENT_SECRET`.
- No secret means an **unsigned** exe with a warning, and the Store and Intune steps are skipped (both need
  a publicly trusted signature). Never ship that as a real release: fix the secret and redo the run.
- Do not add `--tsaurl`; the service timestamps the signature itself.

Verify: the summary says `Signed | yes`, and the release has `TDPdf-vx.y.z.w-win-x64.exe` plus `SHA256SUMS.txt`:

```sh
gh release view vx.y.z.w --repo doodlemania2/TDPdf --json assets --jq '.assets[].name'
```

## 5. Microsoft Store

- Product page: https://apps.microsoft.com/detail/XPDLZDWVQV3W5W
- Gated on the `MSSTORE_*` secrets **and** the repo variable `MSSTORE_ENABLED=true` (set since 2026-10-19).
- The Store refuses an installer URL that redirects ("Provide a download URL without redirection"), and
  GitHub release assets always redirect. So the run first uploads the signed exe to
  `https://tdpdfreleases.blob.core.windows.net/releases/vx.y.z.w/TDPdf-vx.y.z.w-win-x64.exe` and checks
  that it answers 200 with the same SHA-256. Never point the Store at a GitHub asset URL.
- The submission is `build/store/publish-store.py` (Store submission API, called directly; the msstore CLI
  is not on NuGet). It repoints the installer URL, commits, waits and submits.
- The Store step is `continue-on-error`, so a Store failure never blocks Intune. Read the summary row, not
  the run's colour.
- A 404 "No Product Found" means the product has no published submission yet. The first submission is made
  by hand in Partner Center.
- To send an **already published** release to the Store without rebuilding it:
  `gh workflow run store.yml --repo doodlemania2/TDPdf -f tag=vx.y.z.w`. It checks the exe against
  `SHA256SUMS.txt` first.

Verify the blob URL (it must be 200, not 302):

```sh
curl -sI https://tdpdfreleases.blob.core.windows.net/releases/vx.y.z.w/TDPdf-vx.y.z.w-win-x64.exe | head -1
```

Certification then takes Microsoft hours to days. Its status is visible only in Partner Center, so that check
is Derek's. The Store only serves the new version to new installs; it does not update existing ones.

## 6. Intune, through tdpdf-fleet

- Lives in the private repo `doodlemania2/tdpdf-fleet`, workflow `deploy.yml`, owned by the M365 Administrator agent
  in Paperclip. Nothing about Intune goes into this public repo.
- Triggered by `tdpdf-released` from the release run (needs `FLEET_DISPATCH_TOKEN`). Its own schedule is
  only a fallback: GitHub runs it every 3 to 5 hours, not every 15 minutes.
- It downloads the exe, refuses it unless the SHA-256, the Authenticode signer (`CN=The Doodle Project`) and
  the file version all match the tag, stamps the detection rule, and adds a content version to the existing
  Intune app (assignments untouched).
- If `FLEET_DISPATCH_TOKEN` is rejected, the release is already published but the dispatch step fails. The
  token is Derek's gh login; resetting it is his call
  (`gh auth token | gh secret set FLEET_DISPATCH_TOKEN --repo doodlemania2/TDPdf`). Do not change it yourself.

Verify, and redeploy by hand if needed:

```sh
gh run list --repo doodlemania2/tdpdf-fleet --workflow deploy.yml --limit 3
gh workflow run deploy.yml --repo doodlemania2/tdpdf-fleet -f tag=vx.y.z.w           # add -f dry_run=true to rehearse
```

The fleet run's summary says `TDPdf vx.y.z.w → Intune`, `Mode | deploy`, `Result | success`. Devices pick
up the new content on their next Intune check-in, so device-side proof comes later.

## 7. tdpdf.com and docs.tdpdf.com

Private repo `doodlemania2/tdpdf-site`, deployed to doodlessd. No action needed.

- `site.yml` (tdpdf.com, including /download/ and /changelog/) rebuilds on `tdpdf-release` or `app-changed`
  dispatches and on a 15-minute fingerprint check of the latest release and `.github/release-notes/`.
  `SITE_DISPATCH_TOKEN` is not set, so after a release the 15-minute check is what picks it up.
- `docs.yml` (docs.tdpdf.com) rebuilds on `app-changed` and nightly.

Verify (allow up to about 20 minutes after the release is published):

```sh
curl -s https://tdpdf.com/download/  | grep -oE 'v?[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+' | sort -u
curl -s https://tdpdf.com/changelog/ | grep -oE '[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+' | head -1
gh run list --repo doodlemania2/tdpdf-site --workflow site.yml --limit 3
```

Both must show the new version. If they don't after half an hour, the site is the tdpdf-site agent's to fix;
hand it over (see `fleet-agent-handoff`).

## 8. Fleet-presence dispatches

`.github/workflows/presence.yml` calls the shared
`doodlemania2/fleet-presence-dispatch/.github/workflows/presence-dispatch.yml@main` on **every push to
`main`**, not on the tag:

- `app-changed` goes to `doodlemania2/tdpdf-site` when the push touched any feature's `sources` globs in
  `presence/features.json`;
- `feature-shipped` goes to `doodlemania2/TDPPublic` (social posts) when a feature flipped to `ready`.

So the dispatches fire when the version-bump PR merges. Verify:

```sh
gh run list --repo doodlemania2/TDPdf --workflow presence.yml --limit 1
```

Open that run: the `Send app-changed` job should be green, and `Send feature-shipped` runs only if something
became `ready`. Without the token it warns and exits green, so read the log, not just the status.

## 9. Screenshots

`screenshots.yml` starts by itself after a successful release run (`workflow_run`) on the Windows capture runner
(`vars.CAPTURE_RUNNER = tdp-windows-capture`, VM 100). It captures the new release exe and publishes one
`screenshots` artifact that tdpdf-site fetches. Owner-only; never trigger it from a pull request.

```sh
gh run list --repo doodlemania2/TDPdf --workflow screenshots.yml --limit 1
```

Its `capture`, `install-clean` and `publish` jobs should all succeed. A failure here does not affect the release.

## Done means

The release is done when all of these are true, and you report each one:

- the release exists, signed (`Signed | yes`), with exe, source zip and `SHA256SUMS.txt`;
- the Store row says `submitted`, and the blob URL answers 200 (certification is Derek's to watch);
- the tdpdf-fleet deploy run for the tag succeeded in `deploy` mode;
- tdpdf.com/download/ and /changelog/ show the new version;
- the presence run for the merge commit sent `app-changed`.

Anything only Derek can do (Partner Center, a token reset, a rejected certification) goes on the Paperclip
board as a task, per `tdp-house-rules`.
