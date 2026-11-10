#Requires -Version 5.1
<#
.SYNOPSIS
    Local Windows build / dry-run for TDPdf. Does NOT sign and does NOT publish.
.DESCRIPTION
    1. Publishes net10.0-windows/win-x64 as a single-file self-contained EXE (also runs
       build/bundle-source.ps1 to zip the GPLv3 corresponding source).
    2. Computes the SHA256 of the EXE and prints the artifact paths.

    Signed releases are produced only by pushing a version tag: .github/workflows/release.yml
    signs the EXE with Jsign against Azure Artifact Signing, attaches it to the GitHub Release,
    updates the Microsoft Store listing and tells tdpdf-fleet to deploy to Intune.
    See CLAUDE.md, "Release checklist" and "Automated release pipeline".

.PARAMETER SkipSign
    Accepted for compatibility with older invocations. A no-op: this script never signs.
.EXAMPLE
    .\release.ps1
#>
param(
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$proj       = Join-Path $PSScriptRoot "TDPdf.csproj"
$publishDir = Join-Path $PSScriptRoot "bin\Release\net10.0-windows\win-x64\publish"
$exe        = Join-Path $publishDir "TDPdf.exe"

$projVersion = "unknown"
try {
    [xml]$projXml = Get-Content -Raw $proj
    $verNode = $projXml.SelectSingleNode('//Project/PropertyGroup/Version')
    if ($verNode) { $projVersion = $verNode.InnerText.Trim() }
} catch {
    Write-Host "    (Could not parse <Version> from ${proj}: $($_.Exception.Message))" -ForegroundColor Yellow
}

try {
    Write-Host "`n==> Building (Release, net10.0-windows, win-x64)..." -ForegroundColor Cyan
    & dotnet publish $proj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    if (-not (Test-Path $exe)) { throw "EXE not found at: $exe" }
    Write-Host "    EXE: $exe" -ForegroundColor Green

    Write-Host "`n==> Computing SHA256..." -ForegroundColor Cyan
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
    Write-Host "    SHA256: $hash" -ForegroundColor Green

    $srcZip = Get-ChildItem $publishDir -Filter "*-src.zip" -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($srcZip) {
        Write-Host "`n==> Source zip: $($srcZip.FullName)" -ForegroundColor Green
    } else {
        Write-Host "`n    (No source zip found - did bundle-source.ps1 run?)" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "  TDPdf $projVersion UNSIGNED local build" -ForegroundColor White
    Write-Host "  EXE   : $exe"
    if ($srcZip) { Write-Host "  SRC   : $($srcZip.FullName)" }
    Write-Host "  SHA256: $hash"
    Write-Host ""
    Write-Host "  This build is not signed and is not a release. Signed releases are produced by" -ForegroundColor Yellow
    Write-Host "  pushing a version tag: release.yml signs via Azure Artifact Signing (Jsign)." -ForegroundColor Yellow
    Write-Host "  Before tagging, bump the three version files and add .github/release-notes/v<version>.md."
} catch {
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
