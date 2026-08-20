<#
.SYNOPSIS
    Detection half of the TDPdf telemetry policy remediation.

.DESCRIPTION
    Exit 0 = compliant, nothing to do. Exit 1 = drifted, run the remediation.

    Compares against the expected values rather than merely checking the key exists, so a ROTATION
    is detected as drift and re-applied on the next cycle. A presence-only check would leave every
    device on the old token forever after a rotation, which is the whole thing this mechanism
    exists to avoid.

    Deliberately does not print the token: remediation output is retained in Intune and visible to
    anyone with reporting access.

.NOTES
    Placeholders are replaced at upload time, from the same source as the remediation script.
    The two must be rendered together or detection will report permanent drift.
#>

$ErrorActionPreference = 'Stop'

$OtlpEndpoint = '__TDPDF_OTLP_ENDPOINT__'
$OtlpToken    = '__TDPDF_OTLP_TOKEN__'

$KeyPath = 'HKLM:\SOFTWARE\Policies\TDPdf\Telemetry'

try {
    if (-not (Test-Path $KeyPath)) {
        Write-Output 'Policy key absent.'
        exit 1
    }

    $props = Get-ItemProperty -Path $KeyPath -ErrorAction Stop

    if ($props.OtlpEndpoint -ne $OtlpEndpoint) {
        Write-Output 'OtlpEndpoint missing or does not match expected.'
        exit 1
    }
    if ($props.OtlpToken -ne $OtlpToken) {
        Write-Output 'OtlpToken missing or does not match expected (rotation pending).'
        exit 1
    }

    Write-Output 'TDPdf telemetry policy is current.'
    exit 0
}
catch {
    # An unreadable key is drift, not success — fail toward remediation.
    Write-Output "Policy key unreadable: $($_.Exception.Message)"
    exit 1
}
