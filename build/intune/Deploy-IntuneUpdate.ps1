#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes a new content version of the EXISTING TDPdf Intune Win32 app.

.DESCRIPTION
    Updates an app that is already created and already targeted in Intune. It
    never creates the app and never touches assignments: in Graph, assignments
    hang off the APP, while content hangs off a mobileAppContentVersion, so
    adding a version and pointing committedContentVersion at it leaves the
    existing targeting exactly as it was.

    This deliberately does NOT use IntuneWinAppUtil.exe (Windows-only). A
    .intunewin file is only a transport container for the portal UI; the Graph
    upload path takes the encrypted payload plus a fileEncryptionInfo block
    directly, so the same result is produced here on Linux.

    What it does, in order:
      1. Zips the payload folder (the signed TDPdf.exe) — this is the archive
         Intune extracts on the device before running SetupFile.
      2. Encrypts that zip exactly the way IntuneWinAppUtil does, so the Intune
         Management Extension can decrypt it (see Protect-IntunePayload).
      3. Creates a new content version on the app, registers the file, waits
         for Azure Storage to hand back a SAS URI, uploads in blocks, and
         commits with the encryption info.
      4. Points the app at the new version and refreshes displayVersion and the
         PowerShell detection rule in one PATCH.

    Authentication is client-credentials against Microsoft Graph. The app
    registration needs the APPLICATION permission DeviceManagementApps.ReadWrite.All
    with admin consent granted.

.PARAMETER AppId
    Object id (GUID) of the existing win32LobApp in Intune.

.PARAMETER ExePath
    Path to the signed TDPdf.exe to ship.

.PARAMETER DetectionScriptPath
    Path to Detect-TDPdf.ps1. Uploaded with the package so the detection rule
    and the payload can never disagree about the version.

.PARAMETER DisplayVersion
    Version string shown in Intune (e.g. 1.21.0.0).

.PARAMETER TenantId / ClientId / ClientSecret
    Entra app registration credentials. Prefer the environment variables
    AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET so the secret never
    appears in a process listing.

.PARAMETER DryRun
    Do everything local — zip, encrypt, digest, and resolve the app — but make
    no Graph writes. Prints what would change. Use this first.

.NOTES
    Written for pwsh 7 on Linux (the self-hosted `tdpdf` runner) but is
    cross-platform.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppId,
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$DetectionScriptPath,
    [Parameter(Mandatory)][string]$DisplayVersion,
    [string]$TenantId     = $env:AZURE_TENANT_ID,
    [string]$ClientId     = $env:AZURE_CLIENT_ID,
    [string]$ClientSecret = $env:AZURE_CLIENT_SECRET,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$GraphBase = 'https://graph.microsoft.com/beta'   # win32LobApp content APIs are beta-only
$BlockSize = 6MB

function Write-Step { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Note { param([string]$Message) Write-Host "    $Message" -ForegroundColor DarkGray }

# ---------------------------------------------------------------------------
# Encryption
# ---------------------------------------------------------------------------

<#
    Reproduces IntuneWinAppUtil's encryption. The Intune Management Extension
    on the device expects, byte for byte:

        [ HMAC-SHA256 (32) ][ IV (16) ][ AES-256-CBC ciphertext ]

    where the HMAC is taken over (IV || ciphertext) with a SEPARATE 32-byte key
    from the AES key. fileDigest is the SHA-256 of the file BEFORE encryption —
    getting that wrong is the classic failure, because the upload succeeds and
    only the device-side install fails.
#>
function Protect-IntunePayload {
    param([Parameter(Mandatory)][string]$InFile, [Parameter(Mandatory)][string]$OutFile)

    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.Mode    = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.KeySize = 256
        $aes.GenerateKey()
        $aes.GenerateIV()

        $macKey = [byte[]]::new(32)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($macKey)

        # SHA-256 of the plaintext zip, for fileDigest.
        $plainSha = [System.Security.Cryptography.SHA256]::HashData([System.IO.File]::ReadAllBytes($InFile))

        # Encrypt to a temp file first: the final layout needs the HMAC written
        # at offset 0, and the HMAC is not known until the ciphertext exists.
        $cipherTmp = [System.IO.Path]::GetTempFileName()
        try {
            $inStream     = [System.IO.File]::OpenRead($InFile)
            $cipherStream = [System.IO.File]::Create($cipherTmp)
            try {
                # IV goes in front of the ciphertext; it is part of the HMAC input.
                $cipherStream.Write($aes.IV, 0, $aes.IV.Length)
                $encryptor = $aes.CreateEncryptor()
                try {
                    $crypto = [System.Security.Cryptography.CryptoStream]::new(
                        $cipherStream, $encryptor, [System.Security.Cryptography.CryptoStreamMode]::Write)
                    $inStream.CopyTo($crypto)
                    $crypto.FlushFinalBlock()
                    $crypto.Dispose()
                } finally { $encryptor.Dispose() }
            } finally {
                $inStream.Dispose()
                $cipherStream.Dispose()
            }

            # HMAC over (IV || ciphertext), then write mac || iv || ciphertext.
            $hmac = [System.Security.Cryptography.HMACSHA256]::new($macKey)
            try {
                $cipherRead = [System.IO.File]::OpenRead($cipherTmp)
                try { $mac = $hmac.ComputeHash($cipherRead) } finally { $cipherRead.Dispose() }
            } finally { $hmac.Dispose() }

            $outStream  = [System.IO.File]::Create($OutFile)
            $cipherRead = [System.IO.File]::OpenRead($cipherTmp)
            try {
                $outStream.Write($mac, 0, $mac.Length)
                $cipherRead.CopyTo($outStream)
            } finally {
                $cipherRead.Dispose()
                $outStream.Dispose()
            }
        } finally {
            if (Test-Path $cipherTmp) { Remove-Item -Force $cipherTmp }
        }

        [pscustomobject]@{
            encryptionKey        = [Convert]::ToBase64String($aes.Key)
            macKey               = [Convert]::ToBase64String($macKey)
            initializationVector = [Convert]::ToBase64String($aes.IV)
            mac                  = [Convert]::ToBase64String($mac)
            profileIdentifier    = 'ProfileVersion1'
            fileDigest           = [Convert]::ToBase64String($plainSha)
            fileDigestAlgorithm  = 'SHA256'
        }
    } finally { $aes.Dispose() }
}

# ---------------------------------------------------------------------------
# Graph plumbing
# ---------------------------------------------------------------------------

function Get-GraphToken {
    param([string]$TenantId, [string]$ClientId, [string]$ClientSecret)
    $body = @{
        client_id     = $ClientId
        client_secret = $ClientSecret
        scope         = 'https://graph.microsoft.com/.default'
        grant_type    = 'client_credentials'
    }
    try {
        (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
            -ContentType 'application/x-www-form-urlencoded' -Body $body).access_token
    } catch {
        throw "Token request failed. Check AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET and that admin consent was granted for DeviceManagementApps.ReadWrite.All. Underlying error: $($_.Exception.Message)"
    }
}

function Invoke-Graph {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        $Body,
        [Parameter(Mandatory)][string]$Token
    )
    $headers = @{ Authorization = "Bearer $Token" }
    $full    = if ($Uri -match '^https?://') { $Uri } else { "$GraphBase/$($Uri.TrimStart('/'))" }
    $args    = @{ Method = $Method; Uri = $full; Headers = $headers; ErrorAction = 'Stop' }
    if ($null -ne $Body) {
        $args.Body        = ($Body | ConvertTo-Json -Depth 10 -Compress)
        $args.ContentType = 'application/json'
    }
    Invoke-RestMethod @args
}

# Graph provisions Azure Storage asynchronously; the SAS URI is null until it is ready.
function Wait-ForFileState {
    param(
        [Parameter(Mandatory)][string]$FileUri,
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][string]$DesiredState,
        [int]$TimeoutSeconds = 300
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $file = Invoke-Graph -Method GET -Uri $FileUri -Token $Token
        switch ($file.uploadState) {
            $DesiredState { return $file }
            { $_ -like '*Failed' } { throw "Intune reported uploadState '$($file.uploadState)' while waiting for '$DesiredState'." }
        }
        Start-Sleep -Seconds 5
    }
    throw "Timed out after ${TimeoutSeconds}s waiting for uploadState '$DesiredState'."
}

function Send-BlocksToAzure {
    param([Parameter(Mandatory)][string]$SasUri, [Parameter(Mandatory)][string]$FilePath)

    $stream  = [System.IO.File]::OpenRead($FilePath)
    $blockIds = [System.Collections.Generic.List[string]]::new()
    try {
        $buffer = [byte[]]::new($BlockSize)
        $index  = 0
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $blockId = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($index.ToString('D6')))
            $blockIds.Add($blockId)

            $chunk = if ($read -eq $buffer.Length) { $buffer } else { $buffer[0..($read - 1)] }
            $uri   = "$SasUri&comp=block&blockid=$([uri]::EscapeDataString($blockId))"
            Invoke-RestMethod -Method Put -Uri $uri -Body $chunk `
                -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } -ContentType 'application/octet-stream' -ErrorAction Stop | Out-Null

            $index++
            Write-Note "block $index uploaded ($([math]::Round($read/1MB,1)) MB)"
        }
    } finally { $stream.Dispose() }

    # Commit the block list, in order.
    $xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?><BlockList>" +
           (($blockIds | ForEach-Object { "<Latest>$_</Latest>" }) -join '') + '</BlockList>'
    Invoke-RestMethod -Method Put -Uri "$SasUri&comp=blocklist" -Body $xml -ContentType 'application/xml' -ErrorAction Stop | Out-Null
    Write-Ok "committed $($blockIds.Count) block(s)"
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

foreach ($p in @($ExePath, $DetectionScriptPath)) {
    if (-not (Test-Path $p)) { throw "Not found: $p" }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "tdpdf-intune-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $work -Force | Out-Null

try {
    # 1. Payload zip — exactly what Intune extracts on the device.
    Write-Step 'Building payload'
    $payloadDir = Join-Path $work 'payload'
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    $setupFile = [System.IO.Path]::GetFileName($ExePath)
    Copy-Item $ExePath (Join-Path $payloadDir $setupFile) -Force

    $zipPath = Join-Path $work 'payload.zip'
    Compress-Archive -Path (Join-Path $payloadDir '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force
    $plainSize = (Get-Item $zipPath).Length
    Write-Ok "payload.zip $([math]::Round($plainSize/1MB,1)) MB (setup file: $setupFile)"

    # 2. Encrypt.
    Write-Step 'Encrypting payload'
    $encPath = Join-Path $work 'payload.bin'
    $encInfo = Protect-IntunePayload -InFile $zipPath -OutFile $encPath
    $encSize = (Get-Item $encPath).Length
    Write-Ok "encrypted $([math]::Round($encSize/1MB,1)) MB; fileDigest $($encInfo.fileDigest.Substring(0,16))..."

    $detectionB64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($DetectionScriptPath))

    if ($DryRun) {
        Write-Step 'DRY RUN — no Graph writes'
        Write-Note "app id            : $AppId"
        Write-Note "displayVersion    : $DisplayVersion"
        Write-Note "setup file        : $setupFile"
        Write-Note "unencrypted size  : $plainSize"
        Write-Note "encrypted size    : $encSize"
        Write-Note "detection script  : $DetectionScriptPath ($([math]::Round((Get-Item $DetectionScriptPath).Length/1KB,1)) KB base64-encoded)"
        Write-Ok 'Local packaging succeeded. Re-run without -DryRun to publish.'
        return
    }

    foreach ($n in 'TenantId','ClientId','ClientSecret') {
        if ([string]::IsNullOrWhiteSpace((Get-Variable $n -ValueOnly))) { throw "$n is required (set AZURE_$($n.ToUpper()) or pass -$n)." }
    }

    Write-Step 'Authenticating to Microsoft Graph'
    $token = Get-GraphToken -TenantId $TenantId -ClientId $ClientId -ClientSecret $ClientSecret
    Write-Ok 'token acquired'

    # Confirm the app exists and really is a win32LobApp before changing anything.
    $app = Invoke-Graph -Method GET -Uri "deviceAppManagement/mobileApps/$AppId" -Token $token
    if ($app.'@odata.type' -notmatch 'win32LobApp') {
        throw "App $AppId is '$($app.'@odata.type')', not a win32LobApp. Refusing to modify it."
    }
    Write-Ok "target app: $($app.displayName) (current version: $($app.displayVersion))"

    # 3. New content version + file.
    Write-Step 'Creating content version'
    $cv = Invoke-Graph -Method POST -Token $token `
        -Uri "deviceAppManagement/mobileApps/$AppId/microsoft.graph.win32LobApp/contentVersions" -Body @{}
    Write-Ok "content version $($cv.id)"

    # The content file MUST be called IntunePackage.intunewin. This is not
    # cosmetic, and it is NOT the setup file's name: the service validates it,
    # and anything else - including the real EXE name - is rejected with a
    # generic "An error has occurred" BadRequest that names no field. It is the
    # internal name IntuneWinAppUtil gives the encrypted payload inside a
    # .intunewin, and the upload API kept the convention. $setupFile stays
    # TDPdf.exe INSIDE the zip, which is what the app's setupFilePath points at.
    $fileBody = @{
        '@odata.type'  = '#microsoft.graph.mobileAppContentFile'
        name           = 'IntunePackage.intunewin'
        size           = $plainSize
        sizeEncrypted  = $encSize
        isDependency   = $false
    }
    $file = Invoke-Graph -Method POST -Token $token `
        -Uri "deviceAppManagement/mobileApps/$AppId/microsoft.graph.win32LobApp/contentVersions/$($cv.id)/files" -Body $fileBody

    $fileUri = "deviceAppManagement/mobileApps/$AppId/microsoft.graph.win32LobApp/contentVersions/$($cv.id)/files/$($file.id)"

    Write-Step 'Waiting for Azure Storage'
    $file = Wait-ForFileState -FileUri $fileUri -Token $token -DesiredState 'azureStorageUriRequestSuccess'
    Write-Ok 'storage URI issued'

    Write-Step 'Uploading'
    Send-BlocksToAzure -SasUri $file.azureStorageUri -FilePath $encPath

    Write-Step 'Committing'
    Invoke-Graph -Method POST -Uri "$fileUri/commit" -Token $token -Body @{ fileEncryptionInfo = $encInfo } | Out-Null
    Wait-ForFileState -FileUri $fileUri -Token $token -DesiredState 'commitFileSuccess' | Out-Null
    Write-Ok 'content committed'

    # 4. Point the app at the new version, and refresh the detection rule in the
    #    same PATCH so the package and its detection can never disagree.
    Write-Step 'Updating app'
    $patch = @{
        '@odata.type'            = '#microsoft.graph.win32LobApp'
        committedContentVersion  = $cv.id
        displayVersion           = $DisplayVersion
        detectionRules           = @(
            @{
                '@odata.type'           = '#microsoft.graph.win32LobAppPowerShellScriptDetection'
                enforceSignatureCheck   = $false
                runAs32Bit              = $false
                scriptContent           = $detectionB64
            }
        )
    }
    Invoke-Graph -Method PATCH -Uri "deviceAppManagement/mobileApps/$AppId" -Token $token -Body $patch | Out-Null
    Write-Ok "app now on content version $($cv.id), displayVersion $DisplayVersion"

    Write-Host ''
    Write-Host "TDPdf $DisplayVersion published to Intune. Existing assignments untouched." -ForegroundColor Green
}
finally {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}
