<#
.SYNOPSIS
    Regenerates THIRD-PARTY-NOTICES.md at the repository root.

.DESCRIPTION
    TDPdf ships as a single self-contained EXE, so every managed dependency and several
    native ones are physically inside the binary we hand to users. MIT and BSD both require
    their copyright notice to travel with binary distributions, and Apache-2.0 section 4(a)
    requires a copy of the licence itself. One aggregated notices file discharges all three.

    WHY THIS IS A SCRIPT AND NOT A HAND-WRITTEN FILE
    The managed inventory changes whenever a PackageReference moves, and getting it wrong is
    silent. Regenerating is cheap; auditing 35 components by hand is not.

    WHY IT IS NOT FULLY AUTOMATIC
    Licence-scanning tools read NuGet package metadata, and the two largest obligations here
    are invisible to that:

      * pdfium.dll arrives via bblanchon.PDFium.Win32 under runtimes/<rid>/native/ and carries its own
        1,300-line aggregate notice covering PDFium plus libpng, LibTIFF, agg23, FreeType,
        lcms, OpenJPEG, zlib, libjpeg-turbo, IJG and ICU. No scanner will find it.
      * leptonica and the tesseract engine are EmbeddedResource blobs in TDPdf.csproj,
        extracted at runtime. They have no package metadata at all.
      * ddjvu.exe and djvused.exe (DjVuLibre, GPL-2.0-or-later) are EmbeddedResource files when
        third_party/djvulibre/win-x64 is present at build time (TDPdf.csproj); no package metadata.
      * liblcms2-2.dll (Little CMS 2.20, MIT) is an EmbeddedResource taken from MSYS2's UCRT64 package
        (third_party/lcms2); no package metadata. Its licence text is third_party/lcms2/licenses.
      * WebView2Loader.dll is likewise an EmbeddedResource (TDPdf.csproj excludes it as a
        package asset); its licence is the Microsoft.Web.WebView2 package's own BSD-3-Clause.

    So the native section is curated below and the managed section is generated.

.PARAMETER OutputPath
    Where to write the file. Defaults to THIRD-PARTY-NOTICES.md at the repo root.

.EXAMPLE
    pwsh -File build/generate-third-party-notices.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md' }

# NuGet cache location. NUGET_PACKAGES wins so this works on the CI runner, which relocates it.
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
if (-not (Test-Path $nuget)) { throw "NuGet package cache not found at $nuget. Run 'dotnet restore' first." }

function Get-PackageFile {
    param([string]$Package, [string]$Version, [string]$RelativePath)
    $p = Join-Path $nuget "$Package/$Version/$RelativePath"
    if (-not (Test-Path $p)) { throw "Missing $p - run 'dotnet restore' first, or update the pinned version in this script." }
    return (Get-Content -Raw -LiteralPath $p)
}

$lcms2Text = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'third_party/lcms2/licenses/LICENSE-lcms2-MIT')

# ── Component inventory ────────────────────────────────────────────────────────────────────
# Grouped by licence so one licence text can serve many components, which is what Apache-2.0
# section 4(a) ("a copy of this License") and the MIT/BSD notice clauses actually require.
# Versions must match TDPdf.csproj and third_party/PdfSharpCore/PdfSharpCore.csproj.

$mitComponents = @(
    @{ Name = 'PdfSharpCore (vendored, modified)'; Version = '1.3.67'; Url = 'https://github.com/ststeiger/PdfSharpCore'; Holder = 'Copyright (c) 2005-2007 empira Software GmbH, Cologne (Germany)' + [Environment]::NewLine + 'Modified work Copyright (c) 2016 David Dunscombe' }
    @{ Name = 'Docnet.Core (managed wrapper only)'; Version = '2.6.0'; Url = 'https://github.com/GowenGit/docnet'; Holder = 'Copyright (c) 2018 GowenGit' }
    @{ Name = 'SharpZipLib';                       Version = '1.4.2';  Url = 'https://github.com/icsharpcode/SharpZipLib'; Holder = 'Copyright (c) 2000-2018 SharpZipLib Contributors' }
    @{ Name = 'CommunityToolkit.Mvvm';             Version = '8.4.2';  Url = 'https://github.com/CommunityToolkit/dotnet'; Holder = 'Copyright (c) .NET Foundation and Contributors' }
    @{ Name = 'Microsoft.Extensions.* and System.* runtime packages'; Version = '10.0.14'; Url = 'https://github.com/dotnet/runtime'; Holder = 'Copyright (c) .NET Foundation and Contributors' }
)

$apacheComponents = @(
    @{ Name = 'PdfPig';                    Version = '0.1.16';        Url = 'https://github.com/UglyToad/PdfPig' }
    @{ Name = 'Tesseract (.NET wrapper)';  Version = '5.2.0';         Url = 'https://github.com/charlesw/tesseract' }
    @{ Name = 'Tesseract OCR engine (tesseract50.dll, embedded)'; Version = '5.0';  Url = 'https://github.com/tesseract-ocr/tesseract' }
    @{ Name = 'SixLabors.ImageSharp';      Version = '4.1.3';         Url = 'https://github.com/SixLabors/ImageSharp' }
    @{ Name = 'SixLabors.Fonts';           Version = '3.2.0';  Url = 'https://github.com/SixLabors/Fonts' }
    @{ Name = 'OpenTelemetry .NET';        Version = '1.20.1';        Url = 'https://github.com/open-telemetry/opentelemetry-dotnet' }
    @{ Name = 'bblanchon.PDFium.Win32 (PDFium build and packaging)'; Version = '159.0.8138'; Url = 'https://github.com/bblanchon/pdfium-binaries' }
    @{ Name = 'Tesseract trained data (downloaded on demand, not bundled)'; Version = 'n/a'; Url = 'https://github.com/tesseract-ocr/tessdata_fast' }
)

# ── Licence texts ──────────────────────────────────────────────────────────────────────────

# Apache-2.0, taken from a package that ships it verbatim rather than retyped.
$apacheText = Get-PackageFile -Package 'opentelemetry' -Version '1.20.1' -RelativePath 'LICENSE.TXT'

# The PDFium aggregate. Copied whole and never summarised: it is itself a bundle of eleven
# upstream licences, and abridging it would drop notices that must be reproduced.
#
# Read out of the Docnet package even though the DLL we ship now comes from
# bblanchon.PDFium.Win32, which carries no licence file of its own. This is PDFium's own
# notice and applies to any build of it; Docnet simply happens to be a package already on
# disk that vendors a verbatim copy. Docnet is still referenced for its managed wrapper, so
# the path is always present after restore.
$pdfiumText = Get-PackageFile -Package 'docnet.core' -Version '2.6.0' -RelativePath 'runtimes/win-x64/native/LICENSE'

# Carried forward from packages that ship their own downstream notices.
$otelThirdParty = Get-PackageFile -Package 'opentelemetry' -Version '1.20.1' -RelativePath 'THIRD-PARTY-NOTICES.TXT'
$mvvmThirdParty = Get-PackageFile -Package 'communitytoolkit.mvvm' -Version '8.4.2' -RelativePath 'ThirdPartyNotices.txt'

# Microsoft Edge WebView2 SDK: the managed wrappers (Microsoft.Web.WebView2.Core/.Wpf/.WinForms)
# and the embedded native WebView2Loader.dll. BSD-3-Clause, read verbatim from the package, plus
# the package's own notices file. The WebView2 Runtime (the browser engine) is NOT distributed.
$webView2Version = '1.0.4314.35'
$webView2Text = Get-PackageFile -Package 'microsoft.web.webview2' -Version $webView2Version -RelativePath 'LICENSE.txt'
$webView2ThirdParty = Get-PackageFile -Package 'microsoft.web.webview2' -Version $webView2Version -RelativePath 'NOTICE.txt'

$mitText = @'
Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
'@

$leptonicaText = @'
Copyright (C) 2001-2020 Leptonica.  All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above
   copyright notice, this list of conditions and the following
   disclaimer in the documentation and/or other materials
   provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL ANY
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY
OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
'@

# ── Emit ───────────────────────────────────────────────────────────────────────────────────

$sb = [System.Text.StringBuilder]::new()
function Add-Line { param([string]$Text = '') ; [void]$sb.AppendLine($Text) }

Add-Line '# Third-party notices for TDPdf'
Add-Line ''
Add-Line 'TDPdf is distributed under the GNU General Public License v3.0 (see `LICENSE`). It is'
Add-Line 'built as a single self-contained executable, so the components below are physically'
Add-Line 'inside the binary you received. Each is reproduced here with its copyright notice and'
Add-Line 'licence text, as those licences require.'
Add-Line ''
Add-Line 'Every licence listed here is compatible with GPLv3. Note that Apache-2.0 is compatible'
Add-Line 'with GPL **version 3** but not with GPLv2 — TDPdf is GPLv3-only, so this holds, and any'
Add-Line 'future relicensing to "GPLv2 or later" would break it.'
Add-Line ''
Add-Line 'Generated by `build/generate-third-party-notices.ps1`. Do not edit by hand.'
Add-Line ''
Add-Line '## Contents'
Add-Line ''
Add-Line '0. [Upstream project](#0-upstream-project)'
Add-Line '1. [Native components](#1-native-components)'
Add-Line '2. [Apache License 2.0 components](#2-apache-license-20-components)'
Add-Line '3. [MIT components](#3-mit-components)'
Add-Line '4. [Notices carried forward from dependencies](#4-notices-carried-forward-from-dependencies)'
Add-Line ''
Add-Line '---'
Add-Line ''
Add-Line '## 0. Upstream project'
Add-Line ''
Add-Line 'TDPdf began as a fork of **KillerPDF** by SteveTheKiller'
Add-Line '(https://github.com/SteveTheKiller/KillerPDF), which is also licensed under the GNU'
Add-Line 'General Public License v3.0.'
Add-Line ''
Add-Line 'As required by GPLv3 section 5(a): this is a modified version of that work. TDPdf has been'
Add-Line 'substantially rewritten and rebranded by The Doodle Project since the fork, including its'
Add-Line 'multi-document architecture, telemetry and crash reporting, theme and settings systems,'
Add-Line 'and PDF save pipeline. The full list of modifications and the relevant dates are recorded'
Add-Line 'in NOTICE at the root of this repository and of the source bundle.'
Add-Line ''
Add-Line 'Upstream copyright headers are preserved in the files that retain upstream code.'
Add-Line ''
Add-Line '---'
Add-Line ''
Add-Line '## 1. Native components'
Add-Line ''
Add-Line 'These ship as native libraries inside the executable and are extracted at runtime.'
Add-Line ''
Add-Line '| Component | Version | Licence | Upstream |'
Add-Line '|---|---|---|---|'
Add-Line '| PDFium (`pdfium.dll`, via bblanchon.PDFium.Win32) | 159.0.8138 | BSD-3-Clause, plus the aggregate below | https://pdfium.googlesource.com/pdfium/ |'
Add-Line '| Leptonica (`leptonica-1.82.0.dll`) | 1.82.0 | BSD-2-Clause | https://github.com/DanBloomberg/leptonica |'
Add-Line '| Tesseract OCR engine (`tesseract50.dll`) | 5.0 | Apache-2.0 (see section 2) | https://github.com/tesseract-ocr/tesseract |'
Add-Line ('| Microsoft Edge WebView2 loader (`WebView2Loader.dll`) | {0} | BSD-3-Clause (see 1.3) | https://aka.ms/webview2 |' -f $webView2Version)
Add-Line '| Little CMS (`liblcms2-2.dll`, colour management) | 2.20 | MIT (see 1.5) | https://github.com/mm2/Little-CMS |'
Add-Line '| DjVuLibre (`ddjvu.exe`, `djvused.exe`, run as separate programs) | version as shipped | GPL-2.0-or-later (see 1.4) | https://djvu.sourceforge.net/ |'
Add-Line ''
Add-Line '### 1.1 PDFium'
Add-Line ''
Add-Line 'The notice below is reproduced verbatim from the PDFium distribution. It is itself an'
Add-Line 'aggregate covering PDFium and the libraries it embeds — libpng, LibTIFF, agg23, FreeType,'
Add-Line 'Little CMS, OpenJPEG, zlib, libjpeg-turbo, the Independent JPEG Group, and ICU.'
Add-Line ''
Add-Line '```'
Add-Line $pdfiumText.TrimEnd()
Add-Line '```'
Add-Line ''
Add-Line '### 1.2 Leptonica'
Add-Line ''
Add-Line '```'
Add-Line $leptonicaText.TrimEnd()
Add-Line '```'
Add-Line ''
Add-Line '### 1.3 Microsoft Edge WebView2 SDK'
Add-Line ''
Add-Line ('Microsoft.Web.WebView2 {0}: the native `WebView2Loader.dll` and the managed wrappers' -f $webView2Version)
Add-Line '`Microsoft.Web.WebView2.Core`, `.Wpf` and `.WinForms`, which host the signing service''s'
Add-Line 'pages inside TDPdf. The browser engine itself — the Microsoft Edge WebView2 Runtime — is'
Add-Line 'part of Windows and is **not** distributed with TDPdf. The package''s own third-party'
Add-Line 'notices are carried forward in section 4.'
Add-Line ''
Add-Line '```'
Add-Line $webView2Text.TrimEnd()
Add-Line '```'
Add-Line ''
Add-Line '### 1.4 DjVuLibre'
Add-Line ''
Add-Line 'TDPdf opens DjVu books by running DjVuLibre''s `ddjvu.exe` and `djvused.exe` as separate'
Add-Line 'programs, one page at a time; it does not link to them. A build that includes them carries'
Add-Line 'the two programs and the libraries they need inside the executable and extracts them on first'
Add-Line 'use; a build that does not uses a DjVuLibre installed under Program Files. DjVuLibre is by'
Add-Line 'Leon Bottou, Yann LeCun and contributors and is licensed under the GNU General Public'
Add-Line 'License, version 2 or (at your option) any later version, which is compatible with the'
Add-Line 'GNU GPL version 3 that TDPdf is licensed under. Its licence is at'
Add-Line 'https://www.gnu.org/licenses/old-licenses/gpl-2.0.html. The complete corresponding source of'
Add-Line 'every DjVuLibre release is at https://sourceforge.net/projects/djvu/files/DjVuLibre/.'
Add-Line ''
Add-Line '### 1.5 Little CMS'
Add-Line ''
Add-Line 'Colour management (ICC profiles, CMYK and soft proofing) uses Little CMS 2.20 (`liblcms2-2.dll`),'
Add-Line 'the MSYS2 UCRT64 build of the upstream release, which TDPdf extracts on first use and loads'
Add-Line 'only when its SHA-256 matches the one recorded in the app. Only the MIT-licensed core library'
Add-Line 'is bundled; the GPL-3.0 `fast_float` plugin from the same package is not. Corresponding'
Add-Line 'source: https://github.com/mm2/Little-CMS/releases/tag/lcms2.20.'
Add-Line ''
Add-Line '```'
Add-Line $lcms2Text.TrimEnd()
Add-Line '```'
Add-Line ''
Add-Line '---'
Add-Line ''
Add-Line '## 2. Apache License 2.0 components'
Add-Line ''
Add-Line '| Component | Version | Upstream |'
Add-Line '|---|---|---|'
foreach ($c in $apacheComponents) { Add-Line ("| {0} | {1} | {2} |" -f $c.Name, $c.Version, $c.Url) }
Add-Line ''
Add-Line 'The Tesseract trained-data models are **not** bundled. TDPdf downloads them from the'
Add-Line 'upstream repository above when you first enable OCR for a language; they are listed here'
Add-Line 'so their provenance and licence are visible.'
Add-Line ''
Add-Line 'A copy of the Apache License 2.0, as required by its section 4(a):'
Add-Line ''
Add-Line '```'
Add-Line $apacheText.TrimEnd()
Add-Line '```'
Add-Line ''
Add-Line '---'
Add-Line ''
Add-Line '## 3. MIT components'
Add-Line ''
foreach ($c in $mitComponents) {
    Add-Line ("### {0} {1}" -f $c.Name, $c.Version)
    Add-Line ''
    Add-Line $c.Url
    Add-Line ''
    Add-Line '```'
    Add-Line $c.Holder
    Add-Line ''
    Add-Line $mitText.TrimEnd()
    Add-Line '```'
    Add-Line ''
}
Add-Line 'PdfSharpCore is vendored under `third_party/PdfSharpCore/` rather than consumed as a'
Add-Line 'package, so that PDF/A conformance and rendering fixes can be applied at the source. Its'
Add-Line 'upstream copyright headers are preserved in every file, and local changes are marked with'
Add-Line 'a `KillerPDF patch` or `TDPdf patch` comment. See `third_party/PdfSharpCore/VENDORED.txt`'
Add-Line 'for the upstream commit.'
Add-Line ''
Add-Line '---'
Add-Line ''
Add-Line '## 4. Notices carried forward from dependencies'
Add-Line ''
Add-Line 'Reproduced from the packages that supply them.'
Add-Line ''
Add-Line '### OpenTelemetry .NET'
Add-Line ''
Add-Line '```'
Add-Line $otelThirdParty.TrimEnd()
Add-Line '```'
Add-Line ''
Add-Line '### CommunityToolkit.Mvvm'
Add-Line ''
Add-Line '```'
Add-Line $mvvmThirdParty.TrimEnd()
Add-Line '```'
Add-Line ''
Add-Line '### Microsoft Edge WebView2 SDK'
Add-Line ''
Add-Line '```'
Add-Line $webView2ThirdParty.TrimEnd()
Add-Line '```'

# LF endings so the file is stable across the Linux CI runner and Windows dev boxes.
$text = $sb.ToString() -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($OutputPath, $text, [System.Text.UTF8Encoding]::new($false))

Write-Host "Wrote $OutputPath ($([math]::Round((Get-Item $OutputPath).Length / 1KB, 1)) KB)"
Write-Host ("Components: {0} native, {1} Apache-2.0, {2} MIT groups" -f 4, $apacheComponents.Count, $mitComponents.Count)
