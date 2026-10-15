# Download

The current release is **{{VERSION}}**. It runs on Windows 10 and 11, 64-bit. There is
no macOS or Linux build, and there will not be one — the app is WPF on top of a native
`pdfium.dll`.

## Direct download

| | |
|---|---|
| **[{{EXE_ASSET}}]({{RELEASE_DOWNLOAD}}/{{EXE_ASSET}})** | The application. One self-contained executable — no runtime to install first. |
| **[SHA256SUMS.txt]({{RELEASE_DOWNLOAD}}/SHA256SUMS.txt)** | Checksums for every file in this release. |
| **[{{SRC_ASSET}}]({{RELEASE_DOWNLOAD}}/{{SRC_ASSET}})** | GPLv3 corresponding source for this exact binary. |

All three, plus the release notes, are on the
[GitHub releases page]({{RELEASE_PAGE}}).

### Verify what you downloaded

`SHA256SUMS.txt` is generated from the files in the release, in the same job that
signs them. Check yours matches before you run it:

```powershell
Get-FileHash .\{{EXE_ASSET}} -Algorithm SHA256
```

Compare the hash against the line for `{{EXE_ASSET}}` in `SHA256SUMS.txt`. If they
differ, the file you have is not the file that was published — delete it.

The executable is also Authenticode-signed. Right-click ▸ Properties ▸ Digital
Signatures shows the signer, and Windows will tell you itself if the signature is
missing or broken.

## Microsoft Store

> **Not yet available.** TDPdf's Store listing is gated on a publicly trusted code
> signing certificate, and that is not in place. Until it is, the direct download above
> is the only consumer channel, and this section is a placeholder. When the Store
> listing goes live, the link will appear here.
>
> If you are reading this and the Store link is still absent, nothing has gone wrong —
> the channel simply is not open yet.

## Running it

Double-click the executable. Run from anywhere other than its install location, it simply
**runs portable** — from where it is, installing nothing. Fine from a USB stick.

When you want it installed, click **Install TDPdf…** in the status bar. It copies itself to
`%LOCALAPPDATA%\Programs\TDPdf\`, adds a Start Menu entry, registers as a handler for
`.pdf`, and creates one Add/Remove Programs entry. **No administrator rights, and no UAC
prompt** — nothing outside your own profile is written.

If TDPdf is already installed, the same button shows the installed version and offers to
**upgrade** (or reinstall) it from the copy you are running, or to **uninstall** it. On a
machine where your organisation installed TDPdf for everyone, it says so and leaves that
install alone.

To open a file straight away: `TDPdf.exe "C:\path\to\file.pdf"`.

## Unattended and managed deployment

```
TDPdf.exe /install /silent      # install, no UI
TDPdf.exe /uninstall /silent    # remove, no UI
```

`/S`, `/quiet`, `/verysilent` and `--silent` are accepted as synonyms for
`/install /silent`, so whichever switch your deployment tooling reaches for by habit
works.

Run as SYSTEM — which is what Intune does — it installs per machine into
`%ProgramFiles%\TDPdf\` instead. Either way it registers the `.pdf` handler, adds a
Start Menu entry and exactly one Add/Remove Programs entry, and uninstalls cleanly.

Deploying it across a fleet, including detection rules and telemetry provisioning, is
documented in [`docs/intune-distribution.md`]({{REPO_URL}}/blob/main/docs/intune-distribution.md).

## Building it yourself

```powershell
git clone {{REPO_URL}}.git
cd TDPdf
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true
```

Output lands in `bin/Release/net10.0-windows/win-x64/publish/`, and the publish step
also produces the versioned source zip.

A build from a public checkout of this repository **has no telemetry destination in it**
and reports to nobody — see [privacy](/privacy/) and [telemetry](/telemetry/). That is
not a build flag you have to remember to set; there is nowhere for a destination to be
compiled in from.

Building requires Windows and the .NET 10 SDK.

## Earlier releases

Every previous release, with its own source zip and checksums, stays on the
[releases page]({{RELEASE_PAGE_ALL}}). Nothing is removed — GPLv3 §6 obliges the
corresponding source to remain available for the binaries that were distributed, not
just the current one.
