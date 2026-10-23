# Deploying TDPdf in an organisation

This guide is for administrators who want to install TDPdf on managed Windows machines,
with Intune or any other tool that can run an installer and check a registry value. It covers
the install switches, where TDPdf installs, how to detect it, how updates work, and the
policy settings for telemetry and the update check.

TDPdf is one signed file, `TDPdf-<version>-win-x64.exe`, from the
[releases page](https://github.com/doodlemania2/TDPdf/releases). It is signed by
**The Doodle Project** through Microsoft Artifact Signing, so the signature validates on any
Windows machine without deploying a certificate first. Check a download against the release's
`SHA256SUMS.txt`.

## Install and uninstall switches

| Command | What it does |
|---|---|
| `TDPdf.exe /install /silent` | Installs with no UI. Exits 0 on success; see [Exit codes](#exit-codes) for failures. |
| `TDPdf.exe /uninstall /silent` | Uninstalls with no UI. Same exit codes. |
| `TDPdf.exe /install` | Installs, showing errors in a dialog. |
| `TDPdf.exe /uninstall` | Uninstalls after a confirmation. |
| `TDPdf.exe /clear-telemetry` | Opts the device out of telemetry (see below). |

`/S`, `/quiet`, `/verysilent` and `--silent` on their own are treated as `/install /silent`.

Use `/silent` for any unattended install. It never shows a window, so a failure under SYSTEM
cannot hang on a dialog nobody can see.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success. |
| 5 | Access denied: the account cannot write the install folder or registry hive. |
| 112 | The disk is full. |
| 1223 | Cancelled. |
| 1618 | Another TDPdf install or uninstall is already running in the same scope. Intune retries this code by itself. |
| 1 | Any other failure. The install log has the details. |

## Where it installs

The scope follows the account that runs the installer:

| Run as | Installs to | Registry hive | Start Menu |
|---|---|---|---|
| SYSTEM (Intune "Install behavior: System", SCCM, etc.) | `%ProgramFiles%\TDPdf\` | `HKLM` | All users |
| A signed-in user | `%LOCALAPPDATA%\Programs\TDPdf\` | `HKCU` | That user |

A per-user install needs no elevation. In either scope the installer:

1. copies the exe and checks the copy is on disk and not empty;
2. registers the `TDPdf.pdf` ProgID and the `.pdf` association;
3. writes the Add/Remove Programs entry (`Software\Microsoft\Windows\CurrentVersion\Uninstall\TDPdf`:
   name `TDPdf`, publisher `The Doodle Project`, `UninstallString` and `QuietUninstallString`);
4. creates the Start Menu shortcut;
5. **last**, writes the install marker `Software\TDPdf` (`Installed=1`, `InstallPath`, `Version`).

The marker is written last on purpose. If any earlier step fails, nothing reports TDPdf as
installed, so your deployment tool retries instead of recording a half-install as success.

If someone runs a downloaded copy on a machine where IT installed TDPdf for all users, TDPdf
says so and leaves that install alone.

### Install log

Every `/install` and `/uninstall` appends to `install.log`:

- SYSTEM installs: `%ProgramData%\TDPdf\install.log`
- per-user installs: `%LOCALAPPDATA%\TDPdf\install.log`

It records the scope, source and destination paths, the copied file's version, each registry
step, and a final `INSTALL OK` or `INSTALL FAILED: <exception>`. It rotates at about 1 MB. Check
it first when a deployment reports success but detection does not.

## Intune Win32 app settings

Wrap the release exe with Microsoft's
[Win32 Content Prep Tool](https://github.com/Microsoft/Microsoft-Win32-Content-Prep-Tool)
(`IntuneWinAppUtil.exe -c <folder with TDPdf.exe> -s TDPdf.exe -o <out>`). Rename the exe to
`TDPdf.exe` first, so the commands below stay the same for every release.

| Field | Value |
|---|---|
| Name / Publisher | `TDPdf` / `The Doodle Project` |
| Install command | `TDPdf.exe /install /silent` |
| Uninstall command | `"%ProgramFiles%\TDPdf\TDPdf.exe" /uninstall /silent` |
| Install behavior | **System** |
| Device restart behavior | No specific action |
| Operating system architecture | x64 |
| Minimum operating system | Windows 10 1809 |
| Detection rule | see below |

### Detection rule

**A custom detection script** is the most robust. Intune treats "exit 0 with output" as detected:

```powershell
$MinVersion = [Version]'2.0.0.0'   # the release you are deploying
try {
    $p = Get-ItemProperty 'HKLM:\Software\TDPdf' -ErrorAction Stop
    $v = $null
    if ($p.Installed -eq 1 -and [Version]::TryParse([string]$p.Version, [ref]$v) -and $v -ge $MinVersion) {
        Write-Output "TDPdf $v detected"
    }
} catch { }
exit 0
```

Run it as a 64-bit process. Raise `$MinVersion` with each release you deploy, or every machine
will keep reporting compliant and the new version will never install.

**Or a registry rule:**

| Field | Value |
|---|---|
| Rule type | `Registry` |
| Key path | `Software\TDPdf` (no hive prefix, no leading backslash) |
| Value name | `Version` |
| Detection method | `String comparison` |
| Operator | `Greater than or equal to` |
| Value | the release version, e.g. `2.0.0.0` |
| Associated with a 32-bit app on 64-bit clients | `No` |

The hive is `HKEY_LOCAL_MACHINE` when the 32-bit option is `No`. Avoid the `Version` data type;
some tenants reject it when saving.

If you only need "is it installed at all", use `Software\TDPdf` → `Installed` → `Value exists`.
That detects any version, so it will not push upgrades.

Assign to a test group first, check an install on a real enrolled device, then widen it.

## Updates

To update, deploy the new release with a higher version in the detection rule. On its next
check-in (about every eight hours, or sooner from Company Portal ▸ Sync), Intune sees the
installed version is too low and runs the install command again. The installer replaces the
exe and rewrites the marker last.

**If TDPdf is open during an update**, the installer renames the running exe out of the way and
puts the new one in place. The person keeps working in the old version until they next start
TDPdf, and the status bar tells them a restart is pending. The install does not fail and nothing
is force-closed.

### Update check

Twice a day TDPdf asks GitHub whether a newer release exists (an anonymous HTTPS GET to
`api.github.com` for the latest release tag, with no identifier and not even the installed
version). On an Intune-enrolled device it then starts the enrollment client's own sync task, the
same thing Company Portal's **Sync** button does, so Intune delivers the update sooner. **It never
downloads or installs anything itself**: on a managed device, updates stay Intune's job.

| Value | Effect |
|---|---|
| `HKLM\SOFTWARE\Policies\TDPdf\Update` → `Enabled` (DWORD) | `0` turns the check off. Absent or any other value leaves it on. |

If your egress policy blocks `api.github.com`, the check fails quietly and nothing else changes.

## Electronic Signature

**File ▸ Electronic Signature** sends documents for signature through a signing service,
`https://sign.tdpdf.com` by default. Each person connects their own account; nothing is sent until
they send or sign a document. Control it with `HKLM\SOFTWARE\Policies\TDPdf\Documenso`:

| Value | Effect |
|---|---|
| `Enabled` (DWORD) | `0` removes the feature from the app. Absent or any other value leaves it on. |
| `BaseUrl` (REG_SZ) | Points TDPdf at your own signing server instead of sign.tdpdf.com. |
| `AllowUserOverride` (DWORD) | With `BaseUrl` set, `0` stops people changing the server in Settings. |

### Signing inside TDPdf: the WebView2 Runtime

TDPdf shows the signing service's pages (a signing page, a request, its audit log) in a window
of its own, built on the **Microsoft Edge WebView2 Runtime**. It uses the Evergreen runtime
already on the device rather than bundling one:

- **Windows 11** includes it.
- **Windows 10** gets it through Windows Update or Microsoft 365 Apps on almost every managed
  device. Where it is missing, deploy Microsoft's Evergreen Standalone Installer
  (`MicrosoftEdgeWebView2RuntimeInstallerX64.exe` from
  https://developer.microsoft.com/microsoft-edge/webview2/) as a separate Win32 app: install
  command `MicrosoftEdgeWebView2RuntimeInstallerX64.exe /silent /install`, detected by the
  registry value `pv` under
  `HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`
  being present and not `0.0.0.0`. Don't make it a dependency of the TDPdf app.

Without the runtime nothing breaks: TDPdf opens the same page in the default browser instead and
says so in the status bar, with Microsoft's download link.

It writes a per-user browser profile to `%LOCALAPPDATA%\TDPdf\WebView2` (cookies and site data
for the signing service only) and extracts `WebView2Loader.dll` once per version to
`%LOCALAPPDATA%\TDPdf\webview2-loader\<version>\`. Neither is under `Program Files`, so a
machine-wide install needs no extra permissions.

### Sign-in

**Connect** opens the signing service's sign-in page in the default browser and receives the
token on a loopback address (`http://127.0.0.1:<random port>/cb`, or `http://localhost:<port>/cb`)
for at most five minutes. Nothing listens on a network interface, so no firewall rule is needed
and Windows Defender Firewall does not prompt. If Windows refuses both addresses, Connect says so
and offers pasting a token instead. `Enabled = 0` above removes all of this along with the rest
of the feature.

## Defender, SmartScreen and ASR

- **SmartScreen** checks reputation on downloaded files. Deployment through Intune does not apply
  Mark-of-the-Web, so SmartScreen does not prompt for an Intune install.
- **ASR rule `01443614-cd74-433a-b99e-2ecdc07bfc25`** ("Block executable files from running unless
  they meet a prevalence, age, or trusted list criterion") works independently of the signature
  and can block a new release in its first days. Add a path exclusion for `%ProgramFiles%\TDPdf\*`
  to the ASR policy, or put the rule in Audit mode for a test group.
- **UAC** does not prompt for a SYSTEM install, and a per-user install needs no elevation.

## Telemetry

TDPdf sends telemetry only to an **OpenTelemetry (OTLP) collector you configure**. Nothing is
compiled into the exe, so with no policy set it sends nothing. People can also turn it off in
**Settings ▸ Privacy**; reporting needs both their consent and a configured collector. The user
side is described in [`PRIVACY.md`](../PRIVACY.md).

### Policy

Set the destination in `HKLM\SOFTWARE\Policies\TDPdf\Telemetry`:

| Value (REG_SZ) | Contents |
|---|---|
| `OtlpEndpoint` | Base URL of your OTLP/HTTP collector, e.g. `https://otlp.example.com` |
| `OtlpToken` | Bearer token for that collector. Use one scoped to TDPdf. |

Both are required. An endpoint without a token counts as no destination, rather than producing
a stream of 401s from every machine.

In Intune, deploy it as a **Remediation** (detection + remediation script pair, run as SYSTEM,
64-bit) on the same group the app targets. Make the detection script compare the token against
the one you expect, not just check that the value exists, so a token rotation actually reaches
every device. Lock the key so only SYSTEM and Administrators can write it. Users must still be
able to read it, because TDPdf runs as the user; the ACL protects where telemetry goes, not the
token itself.

Use `SOFTWARE\Policies\`, not `Software\TDPdf`: uninstalling TDPdf deletes `Software\TDPdf` in
both hives, which would remove your configuration with it.

### What is sent

- `App.Startup` (app version, `Installed`/`Portable`, OS version, 64-bit or not), `App.Heartbeat`
  every 15 minutes while running, and `App.SessionEnd` on a clean exit.
- `Install.Start` / `Install.Success` when `/install` runs.
- `Crash` / `Install.Crash`: crash reports with paths, `password=` values and connection-string
  fragments removed, plus a short grouping key. A crash that killed the process is sent at the next
  launch, marked as replayed.
- `Tool.Selected` (the tool's name), and `File.Open` / `File.New` / `File.Merge` / `File.Split` /
  `File.Print` as counts only.
- `Zoom.Churn`, a self-diagnostic sent at most once every 15 minutes when the view keeps re-zooming
  in a loop. Its properties are counts, durations and internal method or mode names.
- `Update.Available` / `Update.SyncTriggered` / `Update.RestartPending` / `Update.CheckFailed`:
  version numbers, a result, and an exception type name.
- `Op.*` durations for timed operations such as saving.

Every record carries `service.name` (`tdpdf`), `service.version`, `deployment.environment`, a
random `session.id` that is new on every launch and never stored, and `host.name`, the machine
name. **`host.name` is the one identifying field.** It is what lets you tell one faulty machine
from a fleet-wide problem, but machine names often contain a person's name, so treat it as
identifying.

Never sent: file names or paths, document content, user names, form values, search terms,
signature images, or any lasting user identifier.

### Opting a device out

```cmd
"%ProgramFiles%\TDPdf\TDPdf.exe" /clear-telemetry
```

This needs an administrator or SYSTEM. It writes `%ProgramData%\TDPdf\telemetry.disabled`, which
TDPdf checks before reading any policy, so the device stays opted out through reinstalls and
remediation runs. Delete the file to opt back in.

## Licence

TDPdf is GPLv3. Every release carries `TDPdf-<version>-src.zip`, the corresponding source, next to
the exe. Redistributing it inside your organisation is covered by the licence; keep `LICENSE` and
`NOTICE` with it.
