<#
.SYNOPSIS
    Publishes the www.tdpdf.com pages to Ghost from the files in this repository.

.DESCRIPTION
    Five pages make up the site. Two of them - privacy and third-party licences - are
    repository documents, and the whole point of this script is that they are READ FROM
    THE REPOSITORY AT PUBLISH TIME rather than pasted into Ghost once.

    A pasted licences page is correct exactly once. The first PackageReference that moves
    afterwards makes it a false statement about what is inside a GPLv3 binary we hand to
    people, and nothing anywhere fails to tell us. So the manifest points those pages at
    ../../PRIVACY.md and ../../THIRD-PARTY-NOTICES.md, the content is re-read on every run,
    and -Regenerate re-runs generate-third-party-notices.ps1 first so the notices cannot
    even be stale relative to the current package set.

    WHAT IS PUBLISHED
    build/ghost/pages.json is the manifest and the only thing that decides site content.
    See the $comment block in it.

    MARKDOWN -> GHOST
    The live instance is Ghost 6.x, which is lexical-native. Each page is rendered to HTML
    with ConvertFrom-Markdown (Markdig, built into PowerShell 7 - no external dependency
    for a repo that has no Python or Node manifest), then wrapped in a lexical document
    containing a single 'html' card. Ghost renders an html card verbatim, so what the
    repository says is exactly what the page says.

    The alternative - POSTing `html` with ?source=html and letting Ghost convert - was
    rejected: that conversion is Ghost-version-dependent and lossy around tables, and
    these pages are mostly tables. A single html card is deterministic. It also renders as
    one block in the Ghost editor, which is a feature here: it makes hand-editing a
    generated page obviously wrong rather than quietly tempting.

    IDEMPOTENCE
    Pages are matched on slug. Absent -> create; present -> update in place, carrying the
    page's `updated_at` so Ghost's own collision detection applies. Running this twice
    produces no duplicates and no second copy of anything.

    AUTHENTICATION
    The Ghost Admin API does not take the key as a bearer token. The key is `id:secret`,
    and each request carries a short-lived JWT signed HS256 with the hex-decoded secret,
    `kid` set to the key id, `aud` of /admin/, and a five-minute expiry, sent as
    `Authorization: Ghost <jwt>`. Both are generated per invocation and neither the key
    nor the JWT is ever written to output - including inside error text, which is scrubbed
    before it is shown.

.PARAMETER DryRun
    Render everything and print what would happen. Makes no write of any kind. This is the
    mode to use without an Admin key, and the mode to use before every real publish.

.PARAMETER SiteUrl
    Ghost site URL. Defaults to the manifest's site.url.

.PARAMETER Slug
    Publish only these slugs. Default is every page in the manifest.

.PARAMETER Regenerate
    Re-run the generator named by a page's `regenerate` entry before reading its source.
    Needs a restored NuGet cache (`dotnet restore`).

.PARAMETER SkipTheme
    Do not touch the Ghost code injection fields.

.PARAMETER SkipSettings
    Do not touch site title, description, accent colour or navigation.

.PARAMETER ContentKey
    Ghost Content API key (read-only, public by design). Optional, and used only to tell
    CREATE from UPDATE during a dry run, where there is no Admin key to ask with. May also
    be supplied as $env:GHOST_CONTENT_KEY. Never store it in this repository.

.PARAMETER OutputDir
    Write each rendered page's HTML and lexical to this directory for inspection.

.PARAMETER ShowFull
    Print the whole rendered HTML for each page rather than a preview.

.EXAMPLE
    pwsh -File build/ghost/publish-site.ps1 -DryRun

    Renders all five pages and prints what it would do. No network write, no key needed.

.EXAMPLE
    $env:GHOST_ADMIN_KEY = '<id>:<secret>'
    pwsh -File build/ghost/publish-site.ps1 -Regenerate

    Regenerates THIRD-PARTY-NOTICES.md, then creates or updates all five pages.

.NOTES
    The Admin API key comes from $env:GHOST_ADMIN_KEY and from nowhere else. There is no
    default, no config file and no prompt fallback, because each of those is a way for a
    key to end up committed. To mint one: Ghost admin -> Settings -> Integrations ->
    Add custom integration -> copy the Admin API key (the `id:secret` pair, not the
    Content API key).
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [string]$SiteUrl,
    [string[]]$Slug,
    [switch]$Regenerate,
    [switch]$SkipTheme,
    [switch]$SkipSettings,
    [string]$ContentKey,
    [string]$OutputDir,
    [switch]$ShowFull
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7 or later is required (ConvertFrom-Markdown and Convert::FromHexString). Found $($PSVersionTable.PSVersion)."
}

$scriptDir = $PSScriptRoot
$repoRoot  = (Resolve-Path (Join-Path $scriptDir '..' '..')).Path

# ── Output helpers ─────────────────────────────────────────────────────────────────────

function Write-Step  { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note  { param([string]$Message) Write-Host "    $Message" -ForegroundColor DarkGray }
function Write-Good  { param([string]$Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn2 { param([string]$Message) Write-Host "    $Message" -ForegroundColor Yellow }

# ── Secret handling ────────────────────────────────────────────────────────────────────

# Everything that could carry the key or a JWT off this process - error text, a thrown
# message, a URL with a Content API key in the query string - goes through here first.
# Registered lazily so a secret is redacted from the moment it is known.
$script:SecretFragments = [System.Collections.Generic.List[string]]::new()

function Register-Secret {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return }
    if (-not $script:SecretFragments.Contains($Value)) { $script:SecretFragments.Add($Value) }
}

function Protect-Text {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $out = $Text
    foreach ($s in $script:SecretFragments) {
        if ($s.Length -ge 4) { $out = $out.Replace($s, '[redacted]') }
    }
    # Catch-alls for shapes we may not have registered: a JWT, and a key in a query string.
    $out = [regex]::Replace($out, 'eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+', '[jwt redacted]')
    $out = [regex]::Replace($out, '(?i)((?:key|token|secret)=)[^&\s"'']+', '$1[redacted]')
    $out = [regex]::Replace($out, '(?i)(Authorization:\s*\S+\s*)\S+', '$1[redacted]')
    return $out
}

function Stop-WithMessage {
    param([string]$Message)
    throw (Protect-Text $Message)
}

# ── Admin API key ──────────────────────────────────────────────────────────────────────

function Get-AdminKey {
    <#
        Returns @{ Id; Secret } or $null. Deliberately reads exactly one place: an
        environment variable. No file, no parameter, no prompt - every one of those is a
        path by which a key ends up in a repository or a shell history.
    #>
    $raw = $env:GHOST_ADMIN_KEY
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }

    $raw = $raw.Trim()
    Register-Secret $raw

    $parts = $raw.Split(':')
    if ($parts.Count -ne 2) {
        Stop-WithMessage "GHOST_ADMIN_KEY is not in the expected 'id:secret' form. Copy the Admin API key from Ghost admin -> Settings -> Integrations -> your integration; it contains exactly one colon."
    }

    $id     = $parts[0].Trim()
    $secret = $parts[1].Trim()
    Register-Secret $secret

    if ($id -notmatch '^[0-9a-fA-F]+$' -or $secret -notmatch '^[0-9a-fA-F]+$') {
        Stop-WithMessage "GHOST_ADMIN_KEY does not look like a Ghost Admin API key: both halves should be hexadecimal. Check you copied the Admin API key and not the Content API key."
    }
    if ($secret.Length % 2 -ne 0) {
        Stop-WithMessage "GHOST_ADMIN_KEY secret has an odd number of hex characters, so it cannot be decoded. It looks truncated."
    }

    return @{ Id = $id; Secret = $secret }
}

# ── JWT ────────────────────────────────────────────────────────────────────────────────

function ConvertTo-Base64Url {
    param([byte[]]$Bytes)
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function New-GhostJwt {
    <#
        Ghost Admin API auth. Not a bearer token: a JWT signed with the hex-decoded half of
        the key, identified by the other half in `kid`, audience /admin/, short expiry, and
        sent with the scheme `Ghost` rather than `Bearer`. Getting any one of those wrong
        yields a 401 that says nothing useful, so they are all spelled out here.
    #>
    param([Parameter(Mandatory)][hashtable]$Key)

    $header  = '{"alg":"HS256","typ":"JWT","kid":"' + $Key.Id + '"}'
    $now     = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    # Five minutes. Ghost rejects anything longer, and the JWT only has to outlive one run.
    $payload = '{"iat":' + $now + ',"exp":' + ($now + 300) + ',"aud":"/admin/"}'

    $encodedHeader  = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($header))
    $encodedPayload = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($payload))
    $signingInput   = "$encodedHeader.$encodedPayload"

    $keyBytes = [Convert]::FromHexString($Key.Secret)
    $hmac = [System.Security.Cryptography.HMACSHA256]::new($keyBytes)
    try {
        $signature = $hmac.ComputeHash([Text.Encoding]::ASCII.GetBytes($signingInput))
    } finally {
        $hmac.Dispose()
        [Array]::Clear($keyBytes, 0, $keyBytes.Length)
    }

    $jwt = "$signingInput." + (ConvertTo-Base64Url $signature)
    Register-Secret $jwt
    return $jwt
}

# ── HTTP ───────────────────────────────────────────────────────────────────────────────

function Invoke-GhostAdmin {
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PUT')][string]$Method,
        [Parameter(Mandatory)][string]$Path,          # e.g. 'pages/' or 'pages/slug/privacy/'
        [object]$Body,
        [Parameter(Mandatory)][hashtable]$Key,
        [Parameter(Mandatory)][string]$BaseUrl,
        [switch]$AllowNotFound
    )

    $uri = "$BaseUrl/ghost/api/admin/$Path"
    $headers = @{
        'Authorization'  = "Ghost $(New-GhostJwt -Key $Key)"
        'Accept-Version' = 'v6.0'
        'User-Agent'     = 'tdpdf-publish-site/1.0'
    }

    $requestArgs = @{
        Method      = $Method
        Uri         = $uri
        Headers     = $headers
        ContentType = 'application/json; charset=utf-8'
        ErrorAction = 'Stop'
    }
    if ($null -ne $Body) {
        # -Depth 100: a lexical document nests, and the default of 2 silently truncates it
        # to the string "System.Collections.Hashtable".
        $requestArgs['Body'] = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 100 -Compress))
    }

    try {
        return Invoke-RestMethod @requestArgs
    } catch [Microsoft.PowerShell.Commands.HttpResponseException] {
        $status = [int]$_.Exception.Response.StatusCode
        if ($AllowNotFound -and $status -eq 404) { return $null }

        $detail = ''
        try { $detail = $_.ErrorDetails.Message } catch { }
        if ([string]::IsNullOrWhiteSpace($detail)) { $detail = $_.Exception.Message }

        $hint = switch ($status) {
            401 { " The key was rejected. Check GHOST_ADMIN_KEY is the Admin API key (id:secret), not the Content API key, and that this machine's clock is correct - a JWT is time-sensitive." }
            403 { " Authenticated, but not permitted. The integration needs to still exist and be enabled in Ghost admin -> Settings -> Integrations." }
            404 { " Not found at $uri - check the site URL." }
            default { '' }
        }
        Stop-WithMessage "Ghost API $Method $Path failed with HTTP $status.$hint`n$(Protect-Text $detail)"
    } catch {
        Stop-WithMessage "Ghost API $Method $Path failed: $(Protect-Text $_.Exception.Message)"
    }
}

function Test-PageExistsViaContentApi {
    <#
        Dry-run convenience only. The Content API is read-only and its key is public by
        design, but it still does not live in this repository - it is passed in or taken
        from the environment. Returns $true, $false, or $null for "could not tell".

        Caveat, and it matters: the Content API only serves PUBLISHED pages, so a draft
        reads as absent here. This is a hint for the dry-run report, never a decision.
    #>
    param([string]$BaseUrl, [string]$PageSlug, [string]$Key)
    if ([string]::IsNullOrWhiteSpace($Key)) { return $null }
    try {
        $null = Invoke-RestMethod -Method GET -ErrorAction Stop `
            -Uri "$BaseUrl/ghost/api/content/pages/slug/$PageSlug/?key=$Key" `
            -Headers @{ 'Accept-Version' = 'v6.0' }
        return $true
    } catch [Microsoft.PowerShell.Commands.HttpResponseException] {
        if ([int]$_.Exception.Response.StatusCode -eq 404) { return $false }
        return $null
    } catch {
        return $null
    }
}

# ── Markdown -> HTML -> lexical ────────────────────────────────────────────────────────

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

        $text = [regex]::Replace($inner, '<[^>]+>', '')       # strip nested markup
        $text = [System.Net.WebUtility]::HtmlDecode($text)

        $slug = ConvertTo-GitHubSlug $text
        if ([string]::IsNullOrWhiteSpace($slug)) { $slug = "section" }

        if ($seen.ContainsKey($slug)) {
            $seen[$slug] = $seen[$slug] + 1
            $slug = "$slug-$($seen[$slug])"
        } else {
            $seen[$slug] = 0
        }

        "<h$level id=`"$slug`">$inner</h$level>"
    }

    return [regex]::Replace(
        $Html,
        '<h(?<level>[1-6])(?:\s[^>]*)?>(?<inner>.*?)</h\k<level>>',
        $callback,
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
}

function Remove-LeadingH1 {
    <#
        Ghost stores a page's title in its own field and the theme renders it above the
        content, so a leading `# Title` in the markdown would print the title twice.
        Only the first one goes, and only if it is genuinely the first content.
    #>
    param([string]$Markdown)
    return [regex]::Replace($Markdown, '^\s*#\s+[^\r\n]*\r?\n+', '', 'Singleline')
}

function ConvertTo-GhostHtml {
    param([Parameter(Mandatory)][string]$Markdown)

    $body = Remove-LeadingH1 $Markdown

    # ConvertFrom-Markdown has no string-in/string-out mode that keeps the extension set,
    # so round-trip through a temp file. Markdig's PowerShell pipeline includes pipe
    # tables and auto-identifiers, both of which these documents rely on.
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "tdpdf-ghost-$([guid]::NewGuid()).md"
    try {
        [IO.File]::WriteAllText($tmp, $body, [Text.UTF8Encoding]::new($false))
        $html = (ConvertFrom-Markdown -Path $tmp).Html
    } finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }

    return (Repair-HeadingId $html).Trim()
}

function ConvertTo-LexicalDocument {
    <#
        A lexical document whose entire body is one html card. Ghost renders an html card
        verbatim, so the published page is byte-for-byte what this script produced from the
        repository - no Ghost-version-dependent HTML-to-lexical conversion in the middle,
        which is exactly where tables get mangled.
    #>
    param([Parameter(Mandatory)][string]$Html)

    $doc = [ordered]@{
        root = [ordered]@{
            children = @(
                [ordered]@{
                    type    = 'html'
                    version = 1
                    html    = $Html
                }
            )
            direction = $null
            format    = ''
            indent    = 0
            type      = 'root'
            version   = 1
        }
    }
    return ($doc | ConvertTo-Json -Depth 20 -Compress)
}

# ── Repository facts ───────────────────────────────────────────────────────────────────

function Get-RepoVersion {
    $csproj = Join-Path $repoRoot 'TDPdf.csproj'
    $text = Get-Content -Raw -LiteralPath $csproj
    if ($text -match '<Version>([^<]+)</Version>') { return $Matches[1].Trim() }
    Stop-WithMessage "Could not read <Version> from $csproj."
}

function Get-RepoUrl {
    try {
        $url = (& git -C $repoRoot remote get-url origin 2>$null)
        if ($LASTEXITCODE -eq 0 -and $url) { return ($url.Trim() -replace '\.git$', '') }
    } catch { }
    return 'https://github.com/doodlemania2/TDPdf'
}

function Expand-Token {
    <#
        The download page must never name a version by hand. Every release-shaped string on
        it is derived here from TDPdf.csproj and the asset naming in release.yml, so
        bumping the version and republishing is the whole update.
    #>
    param([string]$Text, [hashtable]$Tokens)
    $out = $Text
    foreach ($k in $Tokens.Keys) { $out = $out.Replace("{{$k}}", [string]$Tokens[$k]) }

    $leftovers = [regex]::Matches($out, '\{\{([A-Z_]+)\}\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    if ($leftovers) {
        Stop-WithMessage "Unsubstituted template token(s) in page content: $($leftovers -join ', '). Add them to the token table in publish-site.ps1 or fix the typo in the markdown."
    }
    return $out
}

# ── Load manifest ──────────────────────────────────────────────────────────────────────

Write-Step 'Reading manifest'

$manifestPath = Join-Path $scriptDir 'pages.json'
if (-not (Test-Path $manifestPath)) { Stop-WithMessage "Manifest not found at $manifestPath." }
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

if (-not $SiteUrl) { $SiteUrl = $manifest.site.url }
$SiteUrl = $SiteUrl.TrimEnd('/')

$version = Get-RepoVersion
$repoUrl = Get-RepoUrl
$tag     = "v$version"

$tokens = @{
    VERSION          = $version
    TAG              = $tag
    REPO_URL         = $repoUrl
    SITE_URL         = $SiteUrl
    # Asset names come from .github/workflows/release.yml. The exe is tagged (leading v),
    # the source zip is versioned by <AssemblyName>-<Version> from bundle-source.ps1.
    EXE_ASSET        = "TDPdf-$tag-win-x64.exe"
    SRC_ASSET        = "TDPdf-$version-src.zip"
    RELEASE_DOWNLOAD = "$repoUrl/releases/download/$tag"
    RELEASE_PAGE     = "$repoUrl/releases/tag/$tag"
    RELEASE_PAGE_ALL = "$repoUrl/releases"
}

Write-Note "Site      : $SiteUrl"
Write-Note "Version   : $version (tag $tag)"
Write-Note "Repo      : $repoUrl"
Write-Note "Mode      : $(if ($DryRun) { 'DRY RUN - no writes' } else { 'PUBLISH' })"

# ── Key ────────────────────────────────────────────────────────────────────────────────

$adminKey = Get-AdminKey
if (-not $ContentKey) { $ContentKey = $env:GHOST_CONTENT_KEY }
if ($ContentKey) { $ContentKey = $ContentKey.Trim() }

if (-not $adminKey) {
    if (-not $DryRun) {
        # Printed rather than thrown: PowerShell reflows a multi-line exception message into
        # its error gutter and the instructions come out unreadable. The throw that follows
        # is a single line for that reason.
        Write-Host ''
        Write-Host 'No Admin API key, so there is nothing to publish with.' -ForegroundColor Red
        Write-Host ''
        Write-Host '  Run with -DryRun to see exactly what would be published without a key,' -ForegroundColor Yellow
        Write-Host '  or mint a key:' -ForegroundColor Yellow
        Write-Host ''
        Write-Host "  1. Sign in to $SiteUrl/ghost/ as an Administrator or Owner."
        Write-Host '  2. Settings -> Integrations -> Add custom integration. Name it "Site publisher".'
        Write-Host '  3. Copy the ADMIN API KEY: <26 hex chars>:<64 hex chars>, exactly one colon.'
        Write-Host '     The Content API key on the same screen is a different, read-only key'
        Write-Host '     and will not authenticate a write.'
        Write-Host '  4. Put it in the environment, not in a file:'
        Write-Host '       bash/zsh    export GHOST_ADMIN_KEY=''<id>:<secret>'''
        Write-Host '       PowerShell  $env:GHOST_ADMIN_KEY = ''<id>:<secret>'''
        Write-Host ''
        Write-Host '  The key is a write credential for the whole site. It does not belong in this' -ForegroundColor Yellow
        Write-Host '  repository - not in a config file, not in a sample, not in a comment.' -ForegroundColor Yellow
        Write-Host ''
        Stop-WithMessage 'GHOST_ADMIN_KEY is not set.'
    }
    Write-Warn2 'No GHOST_ADMIN_KEY set. Dry run continues; existence of each page is checked read-only where a Content API key is available.'
} else {
    Write-Note "Admin key : loaded from GHOST_ADMIN_KEY (id $($adminKey.Id.Substring(0, [Math]::Min(6, $adminKey.Id.Length)))…)"
}

if ($OutputDir) {
    $null = New-Item -ItemType Directory -Force -Path $OutputDir
    $OutputDir = (Resolve-Path $OutputDir).Path
    Write-Note "Rendered output -> $OutputDir"
}

# ── Regenerate generated sources ───────────────────────────────────────────────────────

$selected = @($manifest.pages)
if ($Slug) { $selected = @($selected | Where-Object { $Slug -contains $_.slug }) }
if (-not $selected) { Stop-WithMessage "No pages selected. Known slugs: $(($manifest.pages.slug) -join ', ')." }

foreach ($page in $selected) {
    $regen = if ($page.PSObject.Properties.Name -contains 'regenerate') { $page.regenerate } else { $null }
    if (-not $regen) { continue }

    $regenPath = (Join-Path $scriptDir $regen)
    if ($Regenerate) {
        Write-Step "Regenerating source for '$($page.slug)'"
        if (-not (Test-Path $regenPath)) { Stop-WithMessage "Generator not found: $regenPath" }
        if ($DryRun) {
            Write-Note "would run: pwsh -File $((Resolve-Path $regenPath).Path)"
        } else {
            & $regenPath
            Write-Good "Regenerated via $(Split-Path $regenPath -Leaf)"
        }
    } else {
        Write-Warn2 "'$($page.slug)' is generated by $(Split-Path $regenPath -Leaf); publishing whatever is committed. Pass -Regenerate (after 'dotnet restore') to rebuild it first."
    }
}

# ── Render ─────────────────────────────────────────────────────────────────────────────

Write-Step "Rendering $($selected.Count) page(s)"

$rendered = @()
foreach ($page in $selected) {
    $sourcePath = Join-Path $scriptDir $page.source
    if (-not (Test-Path $sourcePath)) { Stop-WithMessage "Source not found for '$($page.slug)': $sourcePath" }
    $sourcePath = (Resolve-Path $sourcePath).Path

    $markdown = Get-Content -Raw -LiteralPath $sourcePath
    $markdown = Expand-Token -Text $markdown -Tokens $tokens

    $html    = ConvertTo-GhostHtml -Markdown $markdown
    $lexical = ConvertTo-LexicalDocument -Html $html

    $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash

    $rendered += [pscustomobject]@{
        Slug            = $page.slug
        Title           = $page.title
        SourcePath      = $sourcePath
        SourceRelative  = [IO.Path]::GetRelativePath($repoRoot, $sourcePath)
        SourceHash      = $sourceHash
        GeneratedFrom   = if ($page.PSObject.Properties.Name -contains 'generatedFrom') { $page.generatedFrom } else { $null }
        MetaTitle       = if ($page.PSObject.Properties.Name -contains 'metaTitle') { $page.metaTitle } else { $null }
        MetaDescription = if ($page.PSObject.Properties.Name -contains 'metaDescription') { $page.metaDescription } else { $null }
        Html            = $html
        Lexical         = $lexical
        HeadingCount    = ([regex]::Matches($html, '<h[1-6][\s>]')).Count
    }

    if ($OutputDir) {
        [IO.File]::WriteAllText((Join-Path $OutputDir "$($page.slug).html"), $html, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $OutputDir "$($page.slug).lexical.json"), $lexical, [Text.UTF8Encoding]::new($false))
    }
}

# ── Publish pages ──────────────────────────────────────────────────────────────────────

Write-Step 'Pages'

foreach ($r in $rendered) {
    Write-Host ''
    Write-Host "  /$($r.Slug)/  -  $($r.Title)" -ForegroundColor White

    $existing = $null
    $action   = 'UNKNOWN'

    if ($adminKey) {
        # A GET, never a write - safe in either mode, and it is what makes a dry run able to
        # say CREATE or UPDATE for certain rather than guessing.
        try {
            $response = Invoke-GhostAdmin -Method GET -Path "pages/slug/$($r.Slug)/?formats=lexical" `
                                          -Key $adminKey -BaseUrl $SiteUrl -AllowNotFound
            if ($response -and $response.pages -and $response.pages.Count -gt 0) {
                $existing = $response.pages[0]
                $action = 'UPDATE'
            } else {
                $action = 'CREATE'
            }
        } catch {
            # A dry run must still render and report when the site is unreachable or the key
            # is wrong - that is often exactly what you are trying to find out.
            if (-not $DryRun) { throw }
            Write-Warn2 "could not reach Ghost to check for an existing page: $(Protect-Text $_.Exception.Message)"
            $action = 'CREATE or UPDATE'
        }
    } else {
        $exists = Test-PageExistsViaContentApi -BaseUrl $SiteUrl -PageSlug $r.Slug -Key $ContentKey
        $action = if ($exists -eq $true) { 'UPDATE' } elseif ($exists -eq $false) { 'CREATE' } else { 'CREATE or UPDATE' }
    }

    Write-Note "action        : $action$(if (-not $adminKey) { ' (read-only Content API hint; drafts read as absent)' })"
    Write-Note "source        : $($r.SourceRelative)"
    Write-Note "source sha256 : $($r.SourceHash)"
    if ($r.GeneratedFrom) {
        Write-Note "generated     : read from the repository at publish time - never hand-edited in Ghost"
    }
    Write-Note "rendered html : $($r.Html.Length) chars, $($r.HeadingCount) headings"
    Write-Note "lexical       : $($r.Lexical.Length) chars, one html card"
    if ($r.MetaDescription) { Write-Note "meta          : $($r.MetaDescription)" }

    if ($DryRun) {
        $lines = $r.Html -split "`n"
        if ($ShowFull -or $lines.Count -le 42) {
            Write-Host ''
            $lines | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
        } else {
            Write-Host ''
            $lines[0..29] | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
            Write-Host "      … $($lines.Count - 40) more lines (-ShowFull to print, -OutputDir to write) …" -ForegroundColor DarkYellow
            $lines[-10..-1] | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
        }
        continue
    }

    $payload = [ordered]@{
        title   = $r.Title
        slug    = $r.Slug
        lexical = $r.Lexical
        status  = 'published'
    }
    if ($r.MetaTitle)       { $payload['meta_title']       = $r.MetaTitle }
    if ($r.MetaDescription) { $payload['meta_description'] = $r.MetaDescription }

    if ($action -eq 'UPDATE') {
        # Ghost requires updated_at on an edit and rejects the write if the stored page is
        # newer, which is what stops this script from silently clobbering someone's change
        # made in the admin UI since we read it.
        $payload['updated_at'] = $existing.updated_at
        $null = Invoke-GhostAdmin -Method PUT -Path "pages/$($existing.id)/" `
                                  -Body @{ pages = @($payload) } -Key $adminKey -BaseUrl $SiteUrl
        Write-Good "updated  $SiteUrl/$($r.Slug)/"
    } else {
        $null = Invoke-GhostAdmin -Method POST -Path 'pages/' `
                                  -Body @{ pages = @($payload) } -Key $adminKey -BaseUrl $SiteUrl
        Write-Good "created  $SiteUrl/$($r.Slug)/"
    }
}

# ── Theme and site settings ────────────────────────────────────────────────────────────

$settings = [System.Collections.Generic.List[hashtable]]::new()

if (-not $SkipTheme) {
    Write-Host ''
    Write-Step 'Theme (code injection)'
    Write-Note 'Code injection rather than a forked Ghost theme: a fork means owning a theme''s'
    Write-Note 'markup and upgrade path forever to restyle five static pages. See theme/*.html.'

    foreach ($pair in @(
        @{ File = 'code-injection-head.html'; Key = 'codeinjection_head' },
        @{ File = 'code-injection-foot.html'; Key = 'codeinjection_foot' }
    )) {
        $path = Join-Path $scriptDir 'theme' $pair.File
        if (-not (Test-Path $path)) { continue }
        $value = (Get-Content -Raw -LiteralPath $path).Trim()
        $settings.Add(@{ key = $pair.Key; value = $value })
        Write-Note "$($pair.Key) <- theme/$($pair.File) ($($value.Length) chars)"
    }
}

if (-not $SkipSettings) {
    Write-Host ''
    Write-Step 'Site settings'
    $settings.Add(@{ key = 'title';        value = $manifest.site.title })
    $settings.Add(@{ key = 'description';  value = $manifest.site.description })
    $settings.Add(@{ key = 'accent_color'; value = $manifest.site.accentColor })
    # navigation and secondary_navigation are stored as JSON strings, not arrays.
    $settings.Add(@{ key = 'navigation';           value = ($manifest.site.navigation | ConvertTo-Json -Depth 5 -Compress -AsArray) })
    $settings.Add(@{ key = 'secondary_navigation'; value = ($manifest.site.secondaryNavigation | ConvertTo-Json -Depth 5 -Compress -AsArray) })

    foreach ($s in $settings | Where-Object { $_.key -notlike 'codeinjection*' }) {
        Write-Note "$($s.key) = $($s.value)"
    }
}

if ($settings.Count -gt 0) {
    if ($DryRun) {
        Write-Host ''
        Write-Note "would PUT $($settings.Count) setting(s) to $SiteUrl/ghost/api/admin/settings/"
    } else {
        $null = Invoke-GhostAdmin -Method PUT -Path 'settings/' `
                                  -Body @{ settings = $settings } -Key $adminKey -BaseUrl $SiteUrl
        Write-Good "applied $($settings.Count) setting(s)"
    }
}

# ── Routing reminder ───────────────────────────────────────────────────────────────────

Write-Host ''
Write-Step 'Routing'
Write-Note 'Ghost serves its post index at / until routes.yaml says otherwise, so the home page'
Write-Note 'lives at /home/ until someone uploads build/ghost/theme/routes.yaml, once, via'
Write-Note 'Ghost admin -> Settings -> Advanced -> Labs -> Routes. This script does not upload it:'
Write-Note 'routing rewrites every URL on the site in a single write, and that is not something a'
Write-Note 'content publish should do as a side effect.'

Write-Host ''
if ($DryRun) {
    Write-Host "DRY RUN complete - nothing was written to $SiteUrl." -ForegroundColor Yellow
} else {
    Write-Host "Published $($rendered.Count) page(s) to $SiteUrl." -ForegroundColor Green
}
