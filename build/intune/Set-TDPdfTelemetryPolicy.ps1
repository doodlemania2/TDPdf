<#
.SYNOPSIS
    Writes the TDPdf telemetry destination to the managed policy key. Intune remediation script.

.DESCRIPTION
    Creates HKLM\SOFTWARE\Policies\TDPdf\Telemetry and sets the values TDPdf reads to decide where
    to report. Runs as SYSTEM from Intune, paired with Detect-TDPdfTelemetryPolicy.ps1.

    WHY A POLICY KEY RATHER THAN A VALUE COMPILED INTO THE EXE
    The token this writes is the thing that has to be rotatable. Compiled into the binary, rotating
    it means building, signing and shipping a new EXE to every managed device — which is why the
    Application Insights key it replaces was never rotated. Delivered here, rotation is a policy
    edit and a remediation cycle.

    WHY *Policies*\ AND NOT SOFTWARE\TDPdf\
    App.Uninstall runs DeleteSubKeyTree("Software\TDPdf") against both hives. Putting the
    destination there would have an uninstall silently destroy the organisation's configuration.
    SOFTWARE\Policies\ is also the Windows convention for administrator-pushed settings.

    ON THE ACL
    TDPdf runs in the user's context and must READ the token, so any signed-in user can read it.
    That is inherent and the ACL does not pretend otherwise. What it does buy is integrity: a
    non-administrator cannot REWRITE these values, so a standard user cannot silently redirect the
    fleet's telemetry to a collector of their choosing. Treat the token as a low-value credential
    scoped to TDPdf alone — it is deliberately NOT the shared cluster token, so a leak is rotated
    here without touching any other application.

.NOTES
    The placeholders below are replaced at upload time. Never commit real values to this file.
#>

$ErrorActionPreference = 'Stop'

# Replaced at upload time. If these still read __...__ the script was uploaded unrendered.
$OtlpEndpoint     = '__TDPDF_OTLP_ENDPOINT__'
$OtlpToken        = '__TDPDF_OTLP_TOKEN__'
$ConnectionString = '__TDPDF_APPINSIGHTS_CONN__'

$KeyPath = 'HKLM:\SOFTWARE\Policies\TDPdf\Telemetry'

function Test-Rendered {
    param([string]$Value)
    return -not ([string]::IsNullOrWhiteSpace($Value) -or $Value -like '__*__')
}

try {
    if (-not (Test-Path $KeyPath)) {
        New-Item -Path $KeyPath -Force | Out-Null
    }

    # Only write values that were actually rendered. A half-configured deployment should leave the
    # other destination alone rather than blanking it — during the migration both are live, and
    # clearing one is how Application Insights is eventually retired, deliberately, not by accident.
    if (Test-Rendered $OtlpEndpoint) {
        New-ItemProperty -Path $KeyPath -Name 'OtlpEndpoint' -Value $OtlpEndpoint -PropertyType String -Force | Out-Null
    }
    if (Test-Rendered $OtlpToken) {
        New-ItemProperty -Path $KeyPath -Name 'OtlpToken' -Value $OtlpToken -PropertyType String -Force | Out-Null
    }
    if (Test-Rendered $ConnectionString) {
        New-ItemProperty -Path $KeyPath -Name 'ConnectionString' -Value $ConnectionString -PropertyType String -Force | Out-Null
    }

    # Integrity ACL: SYSTEM and Administrators may change these; authenticated users may only read.
    # Inheritance is disabled and not copied, so a permissive ACL on SOFTWARE\Policies cannot widen
    # this key back out.
    $key = Get-Item -Path $KeyPath
    $acl = $key.GetAccessControl()
    $acl.SetAccessRuleProtection($true, $false)

    $rules = @(
        New-Object System.Security.AccessControl.RegistryAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')),
            'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'),
        New-Object System.Security.AccessControl.RegistryAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')),
            'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'),
        New-Object System.Security.AccessControl.RegistryAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')),
            'ReadKey', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    )
    foreach ($r in $rules) { $acl.AddAccessRule($r) }
    $key.SetAccessControl($acl)

    Write-Output 'TDPdf telemetry policy applied.'
    exit 0
}
catch {
    Write-Error "Failed to apply TDPdf telemetry policy: $($_.Exception.Message)"
    exit 1
}
