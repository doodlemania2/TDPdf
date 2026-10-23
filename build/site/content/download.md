# Download

<section class="tdp tdp-band tdp-hero" style="padding-bottom:2rem">
<div class="tdp-wrap">
<span class="tdp-eyebrow">Free · Windows 10 &amp; 11 · 64-bit</span>
<h1>Download TDPdf <em>{{VERSION}}</em></h1>
<p class="tdp-lead">One signed file, about {{EXE_SIZE_MB}}&nbsp;MB. Run it straight away, or click <i>Install TDPdf…</i> in the status bar to install it for your account. You don't need to be an administrator.</p>
<div class="tdp-dl" style="margin-top:1.8rem">
<img class="tdp-dl-mark" src="{{LOGO}}" alt="" width="88" height="88">
<div>
<h2 style="font-size:1.35rem">TDPdf for Windows</h2>
<p class="tdp-muted" style="margin:0">Version {{VERSION}} · {{EXE_SIZE_MB}} MB · signed by The Doodle Project</p>
<div class="tdp-ctas" style="margin:1rem 0 .4rem"><a class="tdp-btn primary" href="{{RELEASE_DOWNLOAD}}/{{EXE_ASSET}}">Download {{EXE_ASSET}}</a><a class="tdp-btn ghost" href="https://apps.microsoft.com/detail/XPDLZDWVQV3W5W">Get it from the Microsoft Store</a><a class="tdp-btn ghost" href="{{RELEASE_PAGE}}">Release notes</a></div>
<div class="tdp-hash">SHA-256 {{EXE_SHA256}}</div>
</div>
</div>
<div class="tdp-strip" style="margin-top:1.2rem">
<div><b>Microsoft Store</b><span><a href="https://apps.microsoft.com/detail/XPDLZDWVQV3W5W">TDPdf on the Store</a></span></div>
<div><b>Source code</b><span><a href="{{RELEASE_DOWNLOAD}}/{{SRC_ASSET}}">{{SRC_ASSET}}</a></span></div>
<div><b>Checksums</b><span><a href="{{RELEASE_DOWNLOAD}}/SHA256SUMS.txt">SHA256SUMS.txt</a></span></div>
<div><b>Every release</b><span><a href="{{RELEASE_PAGE_ALL}}">on GitHub</a></span></div>
</div>
</div>
</section>

### Check the download

Each release includes `SHA256SUMS.txt`. To check your copy, run:

```powershell
Get-FileHash .\{{EXE_ASSET}} -Algorithm SHA256
```

The result should match the line for `{{EXE_ASSET}}` in `SHA256SUMS.txt` (and the
SHA-256 shown above). If it doesn't, delete the file.

The exe is code-signed by **The Doodle Project** through Microsoft Artifact Signing. To
see the signature, right-click the file and choose Properties ▸ Digital Signatures.

## Running it

Double-click `{{EXE_ASSET}}`. It runs from wherever it is without installing anything,
so it works from a USB stick too.

To install it, click **Install TDPdf…** in the status bar. It copies itself to
`%LOCALAPPDATA%\Programs\TDPdf\`, adds a Start Menu entry, becomes an option for
opening `.pdf` files, and adds an entry to Add/Remove Programs. It only writes to your
own user profile, so there's no admin prompt.

If TDPdf is already installed, the same button shows the installed version and lets you
upgrade, reinstall or uninstall it. If your organisation installed it for everyone on the
machine, TDPdf says so and leaves that install alone.

To open a file from the command line: `TDPdf.exe "C:\path\to\file.pdf"`.

## Silent install and Intune

```
TDPdf.exe /install /silent      # install, no UI
TDPdf.exe /uninstall /silent    # remove, no UI
```

`/S`, `/quiet`, `/verysilent` and `--silent` also work as `/install /silent`.

When it runs as SYSTEM, as it does under Intune, TDPdf installs to
`%ProgramFiles%\TDPdf\` for every user on the machine. Detection rules and telemetry
settings for Intune are in [`docs/intune-distribution.md`]({{REPO_URL}}/blob/main/docs/intune-distribution.md).

## Building it yourself

You need Windows and the .NET 10 SDK.

```powershell
git clone {{REPO_URL}}.git
cd TDPdf
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true
```

The exe ends up in `bin/Release/net10.0-windows/win-x64/publish/`, next to a zip of the
source. A build like this has no telemetry destination, so it sends nothing. See
[privacy](/privacy/) and [telemetry](/telemetry/) for details.

## Earlier releases

Every past release, with its source and checksums, is on the
[releases page]({{RELEASE_PAGE_ALL}}).
