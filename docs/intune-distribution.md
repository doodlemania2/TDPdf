# TDPdf — Intune distribution & code signing

Last reviewed: 2026-05-18 (1.0.0.3)

This is the canonical workflow for shipping TDPdf to a corporate Windows
fleet via Microsoft Intune. It supersedes the older `code-signing.md`
roadmap. Everything described here is Windows-only — `signtool`,
`LocalMachine\*` cert stores, `IntuneWinAppUtil.exe`, and `dotnet publish`
for `win-x64` all require running on the Windows release box.

## Overview

TDPdf is distributed as an **Intune Win32 app**, not as a public download:

- Single signed `TDPdf.exe` is packaged into a `.intunewin` archive.
- Intune installs it machine-wide under `%ProgramFiles%\TDPdf\`.
- The app self-installs and self-uninstalls via CLI flags
  (`/install [/silent]` and `/uninstall [/silent]`) so Intune can drive
  the lifecycle headlessly.
- Updates ship by bumping `<Version>` / `<AssemblyVersion>` /
  `<FileVersion>` in `TDPdf.csproj`; Intune's detection rule
  (registry value `HKLM\Software\TDPdf!Version >= <AssemblyVersion>`)
  re-runs the installer on every enrolled device.

## One-time setup

### 1. Self-signed code-signing certificate

TDPdf.exe is signed with a self-signed Authenticode cert issued to
`CN=The Doodle Project (Self-Signed)`. The public part is deployed to
managed devices via Intune so on those devices:

- Authenticode signature validates (cert is in `LocalMachine\Root`).
- SmartScreen / Defender treat it as a known publisher (cert is in
  `LocalMachine\TrustedPublisher`).

**This trust does NOT extend to the public.** On any device outside the
managed fleet, SmartScreen still warns "Unknown publisher". Self-signing
is a stopgap for corporate distribution — nothing more. If you need
public distribution, buy an OV/EV code-signing cert.

Generate (elevated PowerShell on the release box):

```powershell
$cert = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject "CN=The Doodle Project (Self-Signed)" `
  -KeyUsage DigitalSignature `
  -KeyAlgorithm RSA -KeyLength 4096 `
  -HashAlgorithm SHA256 `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -NotAfter (Get-Date).AddYears(5)

# Export the PFX (private key) — keep this file safe, password-protect it.
$pw = Read-Host -AsSecureString "PFX password"
Export-PfxCertificate -Cert $cert -FilePath C:\certs\tdpdf-codesign.pfx -Password $pw

# Export the public .cer for Intune distribution.
Export-Certificate -Cert $cert -FilePath C:\certs\tdpdf-codesign.cer
```

### 2. Deploy the public cert to the fleet

In Intune admin center, deploy `tdpdf-codesign.cer` twice as a
**Trusted Certificate** profile:

- Destination store: **Computer certificate store - Root**
- Destination store: **Computer certificate store - Trusted Publishers**

Assign both profiles to the device group that will receive TDPdf. Until
this lands on a device, signed TDPdf.exe still trips "Unknown
publisher" SmartScreen prompts on it.

### 3. IntuneWinAppUtil

Download `IntuneWinAppUtil.exe` from the [Microsoft Win32 Content Prep
Tool repo](https://github.com/Microsoft/Microsoft-Win32-Content-Prep-Tool)
and drop it at `C:\IntuneWinAppUtil.exe` (the default `release.ps1`
expects this path; override with `-IntuneWinAppUtilPath`).

## Per-release workflow

### 1. Bump versions

Edit `TDPdf.csproj` and bump **all three** version properties together:

```xml
<Version>1.8.0-tdpdf</Version>
<AssemblyVersion>1.8.0.0</AssemblyVersion>
<FileVersion>1.8.0.0</FileVersion>
```

- `<Version>` is SemVer — used for filenames, display, and the
  `.intunewin` filename. Pre-release suffixes (`-tdpdf`) are fine here.
- `<AssemblyVersion>` / `<FileVersion>` are 4-part numeric — written to
  EXE file metadata and used by Intune's detection rule. **These drive
  fleet updates** — if you don't bump them, Intune will not push.

Then add a `## [x.y.z] - YYYY-MM-DD` section to `CHANGELOG.md` and
update the version badge in `pdf-landing/index.html`.

### 2. Run `release.ps1`

From an elevated PowerShell on the Windows release box, in the repo
root:

```powershell
.\release.ps1 -PfxPath C:\certs\tdpdf-codesign.pfx
```

The script will:

1. Prompt for the PFX password via `Read-Host -AsSecureString`.
2. `dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true
   -p:SelfContained=true` → `bin\Release\net9.0-windows\win-x64\publish\TDPdf.exe`.
3. Sign with `signtool sign /fd SHA256 /tr <timestamp-url> /td SHA256
   /f <pfx> /p <pw>` and verify with `signtool verify /pa /v`.
4. Print the SHA256 of the signed EXE.
5. Emit `TDPdf-<Version>-src.zip` (GPLv3 corresponding source, via the
   `BundleSource` MSBuild target that calls `build/bundle-source.ps1`).
6. Stage the signed EXE alone in `intune-staging\` and run
   `IntuneWinAppUtil.exe -c <staging> -s TDPdf.exe -o <publish> -q`,
   producing `TDPdf-<Version>.intunewin`.
7. Print the **exact Intune Win32 app form values** to paste.

Other signing options:

- `-CertStoreThumbprint <hash>` — sign with a cert already in your
  current user `My` store.
- No signing flags + Certum SimplySign cert installed — falls back to
  the original Certum store-cert flow.
- `-PackageIntune:$false` — skip the `.intunewin` step (useful for a
  signing-only dry run; pair with `-SkipSign` for a full no-op build).

### 3. Upload to Intune

In Intune admin center → **Apps → Windows → Add → Windows app (Win32)**:

| Field | Value |
| --- | --- |
| App package file | `TDPdf-<Version>.intunewin` |
| Name | `TDPdf` |
| Publisher | `The Doodle Project` |
| Install command | `TDPdf.exe /install /silent` |
| Uninstall command | `"%ProgramFiles%\TDPdf\TDPdf.exe" /uninstall /silent` |
| Install behavior | **System** |
| Device restart behavior | No specific action |
| Operating system architecture | x64 |
| Minimum operating system | Windows 10 1809 |
| Detection rule | **Use a custom detection script** → upload `build/intune/Detect-TDPdf.ps1` from this repo (see "Detection rule" below). |

> The `/silent` flag on the install command matters. Without it, an error
> inside `DoInstall` running under LocalSystem (session 0) used to try to
> raise a `MessageBox` on an invisible desktop, which could mask the
> failure as "exit code 0, app not detected." `1.0.0.3` removes that
> dialog path entirely, but the headless flag is still the correct
> production setting.

#### Detection rule

The installer writes `HKLM\Software\TDPdf` with `Installed=1`,
`InstallPath`, and `Version` **last** in `DoInstall` — only after the
EXE has been copied, ARP entry written, and PDF file association
registered. Any of the three options below check that marker; pick one.

**Recommended — Custom detection script (most robust):**

1. Under **Detection rules** choose **Rules format: Use a custom
   detection script**.
2. Upload `build/intune/Detect-TDPdf.ps1` from this repo.
3. Leave **Run script as 32-bit process on 64-bit clients = No**.
4. Leave **Enforce script signature check = No** (the script isn't
   signed; you can sign it yourself and flip this on if your tenant
   requires it).

The script reads the registry marker, parses `Version` with
`[Version]::TryParse`, and emits stdout only when the installed version
is greater than or equal to `$MinVersion` (currently `1.24.1.0`). Bump
`$MinVersion` in the script for each release.

**Fallback A — Registry value comparison (manual rule):**

If you prefer a manual rule, Intune validates the fields strictly and
rejects malformed entries with "invalid detection rule, unable to parse
detection rule." Use these exact values:

| Field | Value |
| --- | --- |
| Rule type | `Registry` |
| Key path | `Software\TDPdf` *(no hive prefix, no leading backslash)* |
| Value name | `Version` |
| Detection method | `String comparison` |
| Operator | `Greater than or equal to` |
| Value | `1.0.0.3` *(literal release version — do not paste `<AssemblyVersion>`)* |
| Associated with a 32-bit app on 64-bit clients | `No` |

The hive (`HKEY_LOCAL_MACHINE`) is implicit when "Associated with a
32-bit app on 64-bit clients" is **No**. `String comparison` against a
dotted version like `1.0.0.3` works because Intune compares
left-to-right by numeric segment. Avoid the `Version` data type — some
tenants reject it at save time.

**Fallback B — Registry key exists (simplest):**

If you don't need a minimum-version gate (a fresh deploy after
uninstall always replaces the marker), the simplest possible rule is:

| Field | Value |
| --- | --- |
| Rule type | `Registry` |
| Key path | `Software\TDPdf` |
| Value name | `Installed` |
| Detection method | `Value exists` |
| Associated with a 32-bit app on 64-bit clients | `No` |

This will detect *any* installed TDPdf, including older versions — fine
for first-time rollout, less useful for forcing upgrades.

Assign to the test device group first, validate install on a real
enrolled device, then promote to the broader assignment.

### 4. Tag the release in git

After merge to `main`, move (or create) the release tag:

```bash
git tag -a v<Version> -m "TDPdf v<Version>"
git push origin v<Version>
```

## How the in-app installer works

`App.xaml.cs` handles the install/uninstall paths. Two CLI flag pairs:

- **`/install`** — interactive install (used by the first-launch dialog
  when a user runs the bare EXE).
- **`/install /silent`** — used by Intune. Suppresses MessageBoxes and
  returns exit code 1 on failure so Intune retries on the next sync.
- **`/uninstall`** — interactive uninstall with a confirmation dialog.
- **`/uninstall /silent`** — used by Intune (and the `QuietUninstallString`
  in Add/Remove Programs). No prompts.

Install behavior (silent or interactive):

1. Copies the running EXE to `%ProgramFiles%\TDPdf\TDPdf.exe` (when run
   as SYSTEM) or `%LocalAppData%\Programs\TDPdf\TDPdf.exe` (when run by
   a normal user — the legacy first-launch dialog path).
2. Verifies the copy landed on disk (file exists, non-zero length). If
   not, throws — the caller in `OnStartup` maps the exception to
   `Shutdown(1)` so Intune sees a real failure code rather than a
   swallowed exception.
3. Writes the `TDPdf.pdf` ProgID + `.pdf` extension association under the
   matching hive (`HKLM\Software\Classes` for SYSTEM, `HKCU\Software\Classes`
   otherwise).
4. Writes the Add/Remove Programs entry under the matching hive's
   `Software\Microsoft\Windows\CurrentVersion\Uninstall\TDPdf` with both
   `UninstallString` (interactive) and `QuietUninstallString`
   (`/uninstall /silent`) set.
5. Creates a Start Menu shortcut — All Users (`CommonPrograms`) for the
   system-context install, per-user (`Programs`) otherwise.
6. **Last**, writes the Intune detection marker at
   `Software\TDPdf` (`Installed=1`, `InstallPath`, `Version`). This is
   intentionally the final step: a failure anywhere above must not
   leave a "detected" half-install behind.

Uninstall writes a deferred batch file that self-deletes the install
directory after the process exits, then removes the HKCU entries.

### Install log

Every `/install` and `/uninstall` invocation appends a timestamped log
to `install.log`. Location depends on the scope:

- SYSTEM (Intune install behavior=System) → `%ProgramData%\TDPdf\install.log`
- Interactive user install → `%LocalAppData%\TDPdf\install.log`

The log records the resolved scope, the source EXE path, the
destination path, the post-copy `FileVersionInfo`, every registry step,
and the final `INSTALL OK` / `INSTALL FAILED: <exception>` line. It
rotates at ~1 MB. This is the first thing to check when Intune reports
"installed successfully" but the detection rule fails to flip — the log
will show whether the file copy actually happened and which registry
keys were written.

## Update model

Intune polls each device on its sync cycle (default ~8 hours, manual
sync from Company Portal forces it sooner). On each sync:

1. Intune runs the detection rule: the value at
   `HKLM\Software\TDPdf!Version` is read and version-compared to the
   `<AssemblyVersion>` from the uploaded `.intunewin`.
2. If the installed version is `< <AssemblyVersion>`, Intune re-runs
   the install command (`TDPdf.exe /install /silent`).
3. The installer copies the new EXE over the old one in
   `%ProgramFiles%\TDPdf\` and rewrites the registry marker last.

So **bumping versions in `TDPdf.csproj` and re-uploading the
`.intunewin` is the entire update procedure** — no separate "update"
package, no MSI, no in-app updater.

### Known limitation: locked EXE

If TDPdf is running when Intune pushes an update, `File.Copy` in
`DoInstall` throws `IOException`. `/install /silent` returns exit code
1 and Intune retries on the next sync. Acceptable for an internal app
since the PDF editor may have unsaved work — we don't force-kill it.
If this becomes a real-world pain point, the fix is to copy the new
EXE to a temp path and use `MoveFileEx` with
`MOVEFILE_DELAY_UNTIL_REBOOT`, or restart-the-app pattern.

## Defender / SmartScreen / ASR notes

Cert trust only solves the Authenticode "Unknown publisher" prompt. The
other Windows blocking layers behave independently:

- **SmartScreen "Windows protected your PC"** — reputation-based.
  Self-signed certs cannot pass it on unmanaged devices. **Intune Win32
  deployment bypasses it entirely** (the management agent doesn't apply
  Mark-of-the-Web). Only EV code-signing certs build SmartScreen
  reputation for direct downloads.
- **Attack Surface Reduction rule
  `01443614-cd74-433a-b99e-2ecdc07bfc25`** ("Block executable files
  from running unless they meet a prevalence, age, or trusted list
  criterion") — independent of cert trust. On managed devices it can
  still block TDPdf even after the cert is trusted. Mitigations:
  - Path exclusion to the Intune ASR policy:
    `%ProgramFiles%\TDPdf\*`
  - Or flip the rule to Audit mode for the test group.
  - Or set up WDAC publisher allow-listing (heavier; only worth it if
    you're already running WDAC).
- **UAC** — the Intune system-context install runs as LocalSystem so no
  UAC prompt appears. Interactive end-user installs (double-click EXE
  → click Install) fall back to a per-user install under
  `%LocalAppData%` and also don't require elevation.

## Telemetry (optional)

TDPdf reports to an **OpenTelemetry (OTLP) collector** — self-hosted SigNoz in this deployment —
and to nothing else. With no collector configured it is a no-op: the pipeline initializes with an
empty configuration and makes no network calls.

A **user consent setting** also applies (Settings → Privacy, on by default). Reporting requires
*both* consent and a configured destination, so a build with no destination sends nothing whatever
the setting says. See [`PRIVACY.md`](../PRIVACY.md).

> **Changed in 1.24.0.0.** Azure Application Insights was removed, along with both of its
> provisioning mechanisms: the build-time-embedded key and the per-device
> `%ProgramData%\TDPdf\telemetry.dat` file. Fourteen days of dual export settled it — Azure
> received a lossy ~17% subset (installs and heartbeats, none of the interaction events) while the
> collector carried the full stream. Upgrading deletes any `telemetry.dat` left behind, and the
> remediation script below removes the retired `ConnectionString` policy value. There is no longer
> any destination compiled into the binary, so rotation is always a policy push.

### Telemetry policy profile

The destination lives in `HKLM\SOFTWARE\Policies\TDPdf\Telemetry`:

| Value | Contents |
|---|---|
| `OtlpEndpoint` | Base URL of the OTLP collector, e.g. `https://otlp-tdpdf.thedoodleproject.net` |
| `OtlpToken` | Bearer token for that collector. **TDPdf-scoped** — not the shared cluster token |

Both are required. An endpoint without a token resolves to *no destination* rather than producing
a stream of 401s from every laptop in the fleet.

Deploy it as an **Intune Remediation** (Devices → Remediations), pairing:

- **Detection**: `build/intune/Detect-TDPdfTelemetryPolicy.ps1`
- **Remediation**: `build/intune/Set-TDPdfTelemetryPolicy.ps1`

Run as **SYSTEM**, 64-bit PowerShell, on the same group the TDPdf app targets.

Both scripts ship with `__PLACEHOLDER__` values. **Replace them at upload time — never commit
real values.** Detection compares against the *expected* token rather than merely checking the key
exists, so rotating is: edit both scripts, re-upload, and every device picks the new value up on
its next remediation cycle. A presence-only check would strand the fleet on the old token forever,
which is the failure this design exists to prevent.

`SOFTWARE\Policies\` is deliberate. Besides being the Windows convention for administrator-pushed
settings, `App.Uninstall` runs `DeleteSubKeyTree("Software\TDPdf")` against both hives — so the
obvious location would have an uninstall silently destroy your configuration.

**On the ACL.** The remediation script restricts writes to SYSTEM and Administrators, leaving
authenticated users read-only. TDPdf runs in the user's context and must read the token, so any
signed-in user can read it; that is inherent, and the ACL buys integrity rather than secrecy — a
standard user cannot redirect the fleet's telemetry elsewhere. The token is scoped to TDPdf alone,
so a leak is rotated here without touching any other application on the collector.

### What gets sent (when enabled)

- `App.Startup` — once per interactive launch. Properties: `AppVersion`,
  `InstallScope` (`Installed` / `Portable`), `OSVersion`, `Is64BitProcess`.
- `App.Heartbeat` — every 15 minutes while running, so "the fleet went quiet" is
  distinguishable from "nobody launched it today".
- `App.SessionEnd` — a clean exit. A startup with no matching session end did not finish.
- `Install.Start` / `Install.Success` — when `/install` runs.
- `Install.Crash` / `Crash` — sanitized crash events. Stack traces have Windows/POSIX paths,
  `password=`, and connection-string fragments redacted. A 12-hex-char `GroupingKey` (SHA-256 of
  `type|firstScrubbedFrame`) is included for bucketing. Crashes that killed the process before
  they could be sent are spooled to disk and replayed at the next launch, marked
  `crash.replayed`.
- `Tool.Selected` — tool palette interactions. Property: `Tool` name.
- `Zoom.Churn` — 1.24.1.0. A self-diagnostic for [#132](https://github.com/doodlemania2/TDPdf/issues/132).
  Emitted when the viewport re-applies its zoom 8 or more times per second and **keeps doing so for
  at least 2 seconds**. The sustain requirement is what makes it a defect signature rather than a
  rate: a sidebar animation, a window-edge drag and one flick of Ctrl+wheel all clear 8/s by design
  and none of them holds it. Properties: `Count`, `Via` (the TDPdf method that originated most of
  those zooms — the originator, not the handler they all funnel through), `ViaCount`, `SustainedMs`,
  `FitMode`, `ViewMode` — all compile-time constants, integers, or enum names. Rate-limited to one
  report per 15 minutes.
- `File.Open` / `File.New` / `File.Merge` / `File.Split` /
  `File.Print` — coarse-grained usage. **No file names, paths, sizes, or document content.**
  (Saving is not among these: it is timed instead, and appears as the `Op.*` spans below.)
- `Op.*` spans — durations for timed operations, so latency percentiles are computed by the
  backend rather than pre-aggregated here.

Every record carries resource attributes identifying the application and the run: `service.name`
(`tdpdf`), `service.version`, `deployment.environment`, a per-launch `session.id`, and
`host.name` — the machine name.

**`host.name` is the one identifying field.** It is sent because it is what makes a report
actionable: it separates one machine with a fault from a fleet-wide regression. On a corporate
machine the name is often derived from the user's name, so treat it as identifying that device
and, indirectly, its user. It is disclosed under "Device name" in [`PRIVACY.md`](../PRIVACY.md) —
if you change one, change the other.

What is **never** sent: file paths, file names, document content, user names, form field values,
search terms, signature images, or any persistent *user* identifier. The session identifier is
regenerated on every launch and never written to disk.

### Clearing telemetry on a device

```cmd
"C:\Path\to\TDPdf.exe" /clear-telemetry
```

This writes a `%ProgramData%\TDPdf\telemetry.disabled` sentinel (and deletes any legacy
`telemetry.dat`). The sentinel is checked **before any destination is read**, so it outranks the
managed policy rather than racing it.

So `/clear-telemetry` is **sticky**: neither an Intune re-install nor a remediation cycle will
silently re-enable reporting on a device that was opted out. To re-enable, delete the sentinel
file.

### Rotating the token

Edit `OtlpToken` in both `build/intune/Set-TDPdfTelemetryPolicy.ps1` and
`build/intune/Detect-TDPdfTelemetryPolicy.ps1`, re-upload the remediation, and every device picks
the new value up on its next cycle. No build, no signing, no release — which was the entire point
of moving off the embedded key, a key that was never once rotated because rotating it cost a full
release.

Because detection compares against the *expected* token rather than checking the value merely
exists, a rotation actually propagates. A presence-only check would strand the fleet on the old
token indefinitely.

## GPLv3 compliance reminder

Every release ships `TDPdf-<Version>-src.zip` alongside `TDPdf.exe` to
satisfy §5(a) "corresponding source". `release.ps1`'s publish step
triggers the `BundleSource` MSBuild target which calls
`build/bundle-source.ps1`. The zip is built from `git ls-files`, so any
**unstaged** file is excluded — commit everything before releasing.
Don't strip `LICENSE`, `NOTICE`, or the "fork of KillerPDF" attribution
in the About dialog / README / CHANGELOG.
