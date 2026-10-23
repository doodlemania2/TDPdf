#Requires -Version 7.0
<#
.SYNOPSIS
    Builds tdpdf.com as a static site: one HTML file per page, plus its assets.

.DESCRIPTION
    tdpdf.com is plain HTML served by nginx from a container on doodlessd (see
    build/site/README.md). This script is the whole site generator. It reads pages.json,
    renders each page's Markdown with ConvertFrom-Markdown (Markdig, built into PowerShell 7,
    so there is no package manifest to keep alive), wraps it in layout.html and writes the
    result to -OutputDir, ready to copy into the image.

    The download page states the version, size and SHA-256 of the exe people actually get, so
    those come from the newest PUBLISHED GitHub release, never from TDPdf.csproj: on main the
    csproj is bumped in the release PR before its tag exists, and a page built then would
    point at a file that is not there yet. Pass -Tag to build against a specific release.

    Pages whose source lives outside build/site (PRIVACY.md, THIRD-PARTY-NOTICES.md) are read
    at build time, every time, so the site cannot drift from the files the application ships.

.PARAMETER OutputDir
    Where to write the site. Created if missing; emptied first.

.PARAMETER Tag
    Release tag to describe (e.g. v2.0.2.0). Defaults to the latest published release.

.PARAMETER Regenerate
    Run each page's "regenerate" script first (THIRD-PARTY-NOTICES.md needs a restored
    NuGet cache, so this is off by default and the committed file is used).

.EXAMPLE
    pwsh -File build/site/build-site.ps1 -OutputDir out/site
    docker build -t tdpdf-site -f build/site/Dockerfile .
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDir,
    [string]$Tag,
    [switch]$Regenerate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = $PSScriptRoot
$repoRoot  = (Resolve-Path (Join-Path $scriptDir '../..')).Path
$utf8      = [Text.UTF8Encoding]::new($false)

function Write-Step { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note { param([string]$Message) Write-Host "    $Message" -ForegroundColor DarkGray }
function Stop-WithMessage { param([string]$Message) throw $Message }

# ── Markdown -> HTML ───────────────────────────────────────────────────────────────────

function ConvertTo-GitHubSlug {
    <#
        Markdig's auto-identifier and GitHub's differ, and the difference is load-bearing:
        "## 0. Upstream project" becomes `upstream-project` under Markdig but
        `0-upstream-project` on GitHub. THIRD-PARTY-NOTICES.md carries its own table of
        contents written against GitHub's spelling, so leaving Markdig's ids in place
        publishes a licences page whose every contents link is dead.
    #>
    param([string]$Text)
    $s = $Text.ToLowerInvariant()
    $s = [regex]::Replace($s, '[^a-z0-9 _\-]', '')   # GitHub drops punctuation outright
    $s = $s.Trim() -replace '\s+', '-'
    return $s
}

function Repair-HeadingId {
    param([string]$Html)
    $seen = @{}
    $callback = {
        param($m)
        $level = $m.Groups['level'].Value
        $inner = $m.Groups['inner'].Value
        $text  = [System.Net.WebUtility]::HtmlDecode([regex]::Replace($inner, '<[^>]+>', ''))
        $slug  = ConvertTo-GitHubSlug $text
        if ([string]::IsNullOrWhiteSpace($slug)) { $slug = 'section' }
        if ($seen.ContainsKey($slug)) { $seen[$slug]++; $slug = "$slug-$($seen[$slug])" } else { $seen[$slug] = 0 }
        "<h$level id=`"$slug`">$inner</h$level>"
    }
    return [regex]::Replace($Html, '<h(?<level>[1-6])(?:\s[^>]*)?>(?<inner>.*?)</h\k<level>>', $callback,
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
}

function Remove-LeadingH1 {
    # The layout prints the page title itself, so a leading `# Title` would print it twice.
    param([string]$Markdown)
    return [regex]::Replace($Markdown, '^\s*#\s+[^\r\n]*\r?\n+', '', 'Singleline')
}

function ConvertTo-PageHtml {
    param([Parameter(Mandatory)][string]$Markdown)
    $body = Remove-LeadingH1 $Markdown
    # ConvertFrom-Markdown has no string-in/string-out mode that keeps the extension set
    # (pipe tables, auto-identifiers), so round-trip through a temp file.
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "tdpdf-site-$([guid]::NewGuid()).md"
    try {
        [IO.File]::WriteAllText($tmp, $body, $utf8)
        $html = (ConvertFrom-Markdown -Path $tmp).Html
    } finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
    return (Repair-HeadingId $html).Trim()
}

function ConvertTo-Attr { param([string]$Text) return [System.Net.WebUtility]::HtmlEncode($Text) }

# ── Templating ─────────────────────────────────────────────────────────────────────────

function Expand-Token {
    <#
        No page names a version by hand. Every release-shaped string is derived from the
        release being described and the asset naming in release.yml, so a new release and a
        rebuild is the whole update. A token left unfilled stops the build.
    #>
    param([string]$Text, [hashtable]$Tokens)
    $out = $Text
    foreach ($k in $Tokens.Keys) { $out = $out.Replace("{{$k}}", [string]$Tokens[$k]) }
    $leftovers = [regex]::Matches($out, '\{\{([A-Z_]+)\}\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    if ($leftovers) { Stop-WithMessage "Unsubstituted template token(s): $($leftovers -join ', '). Add them to the token table in build-site.ps1 or fix the typo." }
    return $out
}

function Expand-Shot {
    <#
        Screenshot slots. A page marks one as
            <!--shot:NAME--> ...{{SHOT}}... <!--/shot:NAME-->
        with an optional fallback
            <!--noshot:NAME--> ... <!--/noshot:NAME-->
        The shot is used when build/site/screenshots/NAME.png exists; it is copied into the
        site and referenced with ?v=<hash> so a replaced screenshot is never served stale.
        Otherwise the fallback (an illustration) is kept.
    #>
    param([string]$Text, [hashtable]$Shots)
    $names = [regex]::Matches($Text, '<!--(?:no)?shot:([a-z0-9-]+)-->') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    foreach ($n in $names) {
        $src      = Join-Path $scriptDir "screenshots/$n.png"
        $shotRx   = "(?s)<!--shot:$n-->(.*?)<!--/shot:$n-->"
        $noshotRx = "(?s)<!--noshot:$n-->(.*?)<!--/noshot:$n-->"
        if (Test-Path -LiteralPath $src) {
            $hash = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash.Substring(0, 10).ToLowerInvariant()
            $url  = "/assets/shots/$n.png?v=$hash"
            $Shots[$n] = $src
            $Text = [regex]::Replace($Text, $shotRx, { param($m) $m.Groups[1].Value.Replace('{{SHOT}}', $url) })
            $Text = [regex]::Replace($Text, $noshotRx, '')
            Write-Note "screenshot $n"
        } else {
            $Text = [regex]::Replace($Text, $shotRx, '')
            $Text = [regex]::Replace($Text, $noshotRx, { param($m) $m.Groups[1].Value })
            Write-Note "screenshot $n : none yet, illustration used"
        }
    }
    return $Text
}

# ── Release facts ──────────────────────────────────────────────────────────────────────

$repoSlug = 'doodlemania2/TDPdf'
$repoUrl  = "https://github.com/$repoSlug"
$headers  = @{ 'User-Agent' = 'tdpdf-build-site'; 'Accept' = 'application/vnd.github+json' }
# Unauthenticated calls share a 60/hour limit per IP, which a CI runner pool can exhaust.
if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }

function Get-Release {
    param([string]$Tag)
    $path = if ($Tag) { "releases/tags/$Tag" } else { 'releases/latest' }
    try {
        return Invoke-RestMethod -Uri "https://api.github.com/repos/$repoSlug/$path" -Headers $headers
    } catch {
        Stop-WithMessage "Could not read release '$(if ($Tag) { $Tag } else { 'latest' })' from GitHub ($($_.Exception.Message)). The download page states the exe's size and SHA-256, so the site is not built without them."
    }
}

# ── Build ──────────────────────────────────────────────────────────────────────────────

Write-Step 'Reading manifest'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $scriptDir 'pages.json') | ConvertFrom-Json
$siteUrl  = $manifest.site.url.TrimEnd('/')
$layout   = Get-Content -Raw -LiteralPath (Join-Path $scriptDir 'layout.html')

$release = Get-Release -Tag $Tag
$tag     = $release.tag_name
$version = $tag -replace '^v', ''
$exe     = "TDPdf-$tag-win-x64.exe"          # release.yml: the exe carries the tag (leading v)
$srcZip  = "TDPdf-$version-src.zip"          # bundle-source.ps1: <AssemblyName>-<Version>
$asset   = @($release.assets | Where-Object name -eq $exe)[0]
if (-not $asset) { Stop-WithMessage "Release $tag has no $exe." }
$sums    = (Invoke-WebRequest -Uri "$repoUrl/releases/download/$tag/SHA256SUMS.txt" -Headers @{ 'User-Agent' = 'tdpdf-build-site' }).Content
if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
$sumLine = ($sums -split "`n") | Where-Object { $_ -match [regex]::Escape($exe) } | Select-Object -First 1
if (-not $sumLine) { Stop-WithMessage "SHA256SUMS.txt for $tag has no line for $exe." }

$tokens = @{
    VERSION          = $version
    TAG              = $tag
    REPO_URL         = $repoUrl
    SITE_URL         = $siteUrl
    EXE_ASSET        = $exe
    SRC_ASSET        = $srcZip
    EXE_SIZE_MB      = [string][math]::Round($asset.size / 1MB)
    EXE_SHA256       = ($sumLine -split '\s+')[0].ToLowerInvariant()
    RELEASE_DOWNLOAD = "$repoUrl/releases/download/$tag"
    RELEASE_PAGE     = "$repoUrl/releases/tag/$tag"
    RELEASE_PAGE_ALL = "$repoUrl/releases"
}
Write-Note "Release : $tag ($($tokens.EXE_SIZE_MB) MB)"

if ($Regenerate) {
    foreach ($page in $manifest.pages) {
        if ($page.PSObject.Properties.Name -notcontains 'regenerate') { continue }
        Write-Step "Regenerating source for '$($page.slug)'"
        & (Join-Path $scriptDir $page.regenerate)
    }
}

# A fresh directory each time, so a page removed from the manifest leaves the site with it.
if (Test-Path -LiteralPath $OutputDir) { Remove-Item -LiteralPath $OutputDir -Recurse -Force }
$null = New-Item -ItemType Directory -Force -Path (Join-Path $OutputDir 'assets/shots')
$OutputDir = (Resolve-Path $OutputDir).Path

# Assets first: their hashes go into every page's links.
Copy-Item -LiteralPath (Join-Path $scriptDir 'site.css') -Destination (Join-Path $OutputDir 'assets/site.css')
Copy-Item -LiteralPath (Join-Path $repoRoot 'Resources/splash-logo.png') -Destination (Join-Path $OutputDir 'assets/logo.png')
$cssVer  = (Get-FileHash -LiteralPath (Join-Path $OutputDir 'assets/site.css') -Algorithm SHA256).Hash.Substring(0, 10).ToLowerInvariant()
$logoVer = (Get-FileHash -LiteralPath (Join-Path $OutputDir 'assets/logo.png') -Algorithm SHA256).Hash.Substring(0, 10).ToLowerInvariant()
$tokens.LOGO = "/assets/logo.png?v=$logoVer"

function Get-NavHtml {
    param($Items, [string]$CurrentPath)
    ($Items | ForEach-Object {
        $current = if ($_.url -eq $CurrentPath) { ' aria-current="page"' } else { '' }
        "<li><a href=`"$(ConvertTo-Attr $_.url)`"$current>$(ConvertTo-Attr $_.label)</a></li>"
    }) -join ''
}

function Write-Page {
    param([string]$Path, [string]$Title, [string]$Description, [string]$BodyClass, [string]$Main)
    $canonical = "$siteUrl$Path"
    $html = $layout.
        Replace('{{PAGE_TITLE}}', (ConvertTo-Attr $Title)).
        Replace('{{PAGE_DESCRIPTION}}', (ConvertTo-Attr $Description)).
        Replace('{{CANONICAL}}', (ConvertTo-Attr $canonical)).
        Replace('{{BODY_CLASS}}', $BodyClass).
        Replace('{{CSS}}', "/assets/site.css?v=$cssVer").
        Replace('{{LOGO}}', $tokens.LOGO).
        Replace('{{SITE_TITLE}}', (ConvertTo-Attr $manifest.site.title)).
        Replace('{{NAV}}', (Get-NavHtml $manifest.site.navigation $Path)).
        Replace('{{FOOTER_NAV}}', (Get-NavHtml $manifest.site.secondaryNavigation $Path)).
        Replace('{{YEAR}}', [string](Get-Date).Year).
        Replace('{{MAIN}}', $Main)
    $file = if ($Path -eq '/') { 'index.html' } elseif ($Path.EndsWith('/')) { "$($Path.Trim('/'))/index.html" } else { $Path.TrimStart('/') }
    $target = Join-Path $OutputDir $file
    $null = New-Item -ItemType Directory -Force -Path (Split-Path $target)
    [IO.File]::WriteAllText($target, $html, $utf8)
}

Write-Step "Rendering $(@($manifest.pages).Count) page(s)"
$shots = @{}
$urls  = @()
foreach ($page in $manifest.pages) {
    $source = Join-Path $scriptDir $page.source
    if (-not (Test-Path -LiteralPath $source)) { Stop-WithMessage "Source not found for '$($page.slug)': $source" }

    $markdown = Get-Content -Raw -LiteralPath $source
    $markdown = Expand-Shot -Text $markdown -Shots $shots
    $markdown = Expand-Token -Text $markdown -Tokens $tokens
    $content  = ConvertTo-PageHtml -Markdown $markdown

    $path  = $page.path
    $title = if ($page.PSObject.Properties.Name -contains 'metaTitle') { $page.metaTitle } else { "$($page.title) · $($manifest.site.title)" }
    $hero  = ($page.PSObject.Properties.Name -contains 'hero') -and $page.hero
    $main  = if ($hero) {
        # Product pages open with their own hero section, so the layout adds no title.
        "<div class=`"canvas`">$content</div>"
    } else {
        "<article class=`"canvas doc`"><h1 class=`"doc-title`">$(ConvertTo-Attr $page.title)</h1>$content</article>"
    }
    Write-Page -Path $path -Title $title -Description $page.metaDescription -BodyClass "page-$($page.slug)" -Main $main
    $urls += "$siteUrl$path"
    Write-Note "$path"
}

foreach ($n in $shots.Keys) { Copy-Item -LiteralPath $shots[$n] -Destination (Join-Path $OutputDir "assets/shots/$n.png") }

# 404, served by nginx for any unknown path.
Write-Page -Path '/404.html' -Title "Page not found · $($manifest.site.title)" -Description 'This page does not exist.' -BodyClass 'page-404' `
    -Main '<article class="canvas doc"><h1 class="doc-title">Page not found</h1><p>There is nothing at this address. Try the <a href="/">home page</a> or <a href="/download/">download TDPdf</a>.</p></article>'

$sitemap = @('<?xml version="1.0" encoding="UTF-8"?>', '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">') +
           ($urls | ForEach-Object { "  <url><loc>$_</loc></url>" }) + '</urlset>'
[IO.File]::WriteAllText((Join-Path $OutputDir 'sitemap.xml'), ($sitemap -join "`n") + "`n", $utf8)
[IO.File]::WriteAllText((Join-Path $OutputDir 'robots.txt'), "User-agent: *`nAllow: /`nSitemap: $siteUrl/sitemap.xml`n", $utf8)

Write-Step "Built $($urls.Count) page(s) into $OutputDir"
