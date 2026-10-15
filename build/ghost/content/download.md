# Download

<section class="tdp tdp-band tdp-hero" style="padding-bottom:2rem">
<div class="tdp-wrap">
<span class="tdp-eyebrow">Free · Windows 10 &amp; 11 · 64-bit</span>
<h1>Download TDPdf <em>{{VERSION}}</em></h1>
<p class="tdp-lead">One signed file, about {{EXE_SIZE_MB}}&nbsp;MB. Run it straight away, or click <i>Install TDPdf…</i> in the status bar to install it for your account — no administrator needed.</p>
<div class="tdp-dl" style="margin-top:1.8rem">
<img class="tdp-dl-mark" src="https://raw.githubusercontent.com/doodlemania2/TDPdf/main/Resources/splash-logo.png" alt="" width="88" height="88">
<div>
<h2 style="font-size:1.35rem">TDPdf for Windows</h2>
<p class="tdp-muted" style="margin:0">Version {{VERSION}} · {{EXE_SIZE_MB}} MB · signed by The Doodle Project</p>
<div class="tdp-ctas" style="margin:1rem 0 .4rem"><a class="tdp-btn primary" href="{{RELEASE_DOWNLOAD}}/{{EXE_ASSET}}">Download {{EXE_ASSET}}</a><a class="tdp-btn ghost" href="{{RELEASE_PAGE}}">Release notes</a></div>
<div class="tdp-hash">SHA-256 {{EXE_SHA256}}</div>
</div>
</div>
<div class="tdp-strip" style="margin-top:1.2rem">
<div><b>Microsoft Store</b><span>Coming soon — in certification now</span></div>
<div><b>Source code</b><span><a href="{{RELEASE_DOWNLOAD}}/{{SRC_ASSET}}">{{SRC_ASSET}}</a></span></div>
<div><b>Checksums</b><span><a href="{{RELEASE_DOWNLOAD}}/SHA256SUMS.txt">SHA256SUMS.txt</a></span></div>
<div><b>Every release</b><span><a href="{{RELEASE_PAGE_ALL}}">on GitHub</a></span></div>
</div>
</div>
</section>

### Verify what you downloaded

`SHA256SUMS.txt` is generated from the files in the release, in the same job that
signs them. Check yours matches before you run it:

```powershell
Get-FileHash .\{{EXE_ASSET}} -Algorithm SHA256
```

Compare the hash against the line for `{{EXE_ASSET}}` in `SHA256SUMS.txt`. If they
differ, the file you have is not the file that was published — delete it.

The executable is also Authenticode-signed by **The Doodle Project**, through
Microsoft's Artifact Signing service, so it chains to a root Windows already trusts.
Right-click ▸ Properties ▸ Digital Signatures shows the signer, and Windows will tell
you itself if the signature is missing or broken.

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
