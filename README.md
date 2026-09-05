# TDPdf

> **Fork notice.** TDPdf is a fork of [**SteveTheKiller/KillerPDF**](https://github.com/SteveTheKiller/KillerPDF) (GPLv3). All credit for the original design, codebase, and engineering belongs to Steve. TDPdf is maintained by **The Doodle Project, LLC** (Frisco, TX). See [NOTICE](NOTICE) for the full attribution and the list of modifications relative to upstream.

PDF editor for Windows. View, annotate, merge, split, edit text, draw, sign, print, flatten, and open password-protected PDFs without an Adobe subscription or a phone-home. Install or run portable. Single Windows EXE, self-contained — no runtime install required.

## Features

- High-quality rendering via PDFium (Docnet.Core)
- Merge multiple PDFs and split out selected pages, drag-and-drop page reordering
- Inline text editing with font matching against the original document
- Text boxes, freehand drawing, and highlight overlays with adjustable color, size, and opacity
- Draw and save reusable signatures or import a PNG/JPG/BMP image as a signature, click to place anywhere on a page
- Insert images onto any page as resizable annotations — drag the corner handle to scale, burned into the PDF on save
- Right-click sidebar: insert blank page, rotate CW/CCW, move up/down, extract, or delete — works on multi-page selections
- Clickable PDF links and internal cross-references, including TOC back-links
- Multi-page grid view at low zoom levels for context across the whole document
- Zoom preset dropdown with scroll-wheel sync
- Full-text search across the entire document with result highlighting, drag-select to copy text
- Open multiple PDFs as tabs in a single window — File ▸ Open (multi-select), drag-and-drop, or double-clicking PDFs in Explorer all add tabs instead of new windows (toggleable in Settings)
- Unsaved-changes protection with dirty tracking and title bar indicator, per tab
- Close file/tab without quitting (Ctrl+W)
- Print with annotations flattened into the output
- Save Flattened PDF: rasterizes every page at 150 DPI via PDFium into a fully uneditable document
- Password-protected PDF support: prompts for password instead of erroring, decrypted copy held in temp for the session
- Self-installing EXE: running from outside the install path shows an Install / Run Portable dialog; running a newer version shows an Update prompt instead. Installs per-user to %LOCALAPPDATA% (no UAC), registers as PDF file handler, adds Start Menu and optional Desktop shortcuts, uninstalls cleanly via Add/Remove Programs

## Screenshots

![TDPdf image annotation](screenshots/add_image.png)

![TDPdf right-click sidebar](screenshots/right_click_sidebar.png)

## Requirements

- Windows 10 or 11 (x64)
- No runtime install. Everything needed is inside the self-contained single-file EXE.

## Download

Grab the latest signed `TDPdf.exe` from the [Releases page](https://github.com/doodlemania2/TDPdf/releases/latest).

## Build from source

```powershell
git clone https://github.com/doodlemania2/TDPdf.git
cd TDPdf
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true
```

Output lands in `bin/Release/net10.0-windows/win-x64/publish/`. The publish step produces a self-contained single-file `TDPdf.exe` plus a versioned `TDPdf-<version>-src.zip` for GPL3 corresponding-source distribution.

Requires Windows and the .NET 9 SDK to build.

## Privacy

**Building this repository produces a binary that reports to nobody.** No telemetry endpoint is
committed here, and since 1.24.0.0 none is embedded into released binaries either — the
destination is supplied at runtime by administrator-pushed policy, never compiled in. Reporting
needs both a user consent setting (Settings → Privacy, on by default) *and* a configured
destination; a build with no destination is inert whichever way the setting is set, so the default
costs a self-builder nothing.

A destination is configured deliberately, in one of two ways: the `TDPDF_OTLP_ENDPOINT` and
`TDPDF_OTLP_TOKEN` environment variables, or administrator-pushed policy values at
`HKLM\SOFTWARE\Policies\TDPdf\Telemetry\OtlpEndpoint` and `…\OtlpToken`. **Machines under
managed deployment receive those values from an Intune configuration profile** and report to an
OpenTelemetry collector operated by the deploying organisation — if you are running such a machine
rather than your own build, reporting is on unless you turn it off.

When reporting is on, TDPdf sends event names, coarse technical context (app version, install
scope, Windows version) and sanitised crash reports. Document contents, file names, file paths,
user names and persistent device identifiers are never sent, and stack traces are scrubbed of
paths before transmission. Reports do carry the machine name.

Turn it off per user in Settings → Privacy, or device-wide with `TDPdf.exe /clear-telemetry`,
which outranks any administrator-pushed destination.

**Full detail, enumerated from the source: [PRIVACY.md](PRIVACY.md).** Provisioning and rotation
for managed deployments: [`docs/intune-distribution.md`](docs/intune-distribution.md).

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## License

GPLv3. See [LICENSE](LICENSE). TDPdf is a modified version of KillerPDF; the corresponding source for every released binary is published alongside the EXE as `TDPdf-<version>-src.zip` (GPLv3 §6). If you fork, modify, or redistribute TDPdf, your version must also be released under GPLv3 with source available.
