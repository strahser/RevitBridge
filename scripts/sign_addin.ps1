# Signs RevitBridge assemblies with a code-signing certificate.
# Authenticode is invalidated by every rebuild, so run this after each build
# and before install_addin.ps1.
#
# Local dev:
#   $cert = New-SelfSignedCertificate -Type CodeSigningCert `
#       -Subject "CN=RevitBridge Local Dev" -CertStoreLocation Cert:\CurrentUser\My `
#       -KeyExportPolicy Exportable
#   .\scripts\sign_addin.ps1 -Thumbprint $cert.Thumbprint -Configuration Debug.R24
#
# Production: use a CA-issued code-signing certificate (PFX in a secret store)
# and a timestamp URL.

param(
    [Parameter(Mandatory = $true)]
    [string]$Thumbprint,
    [string]$Configuration = 'Debug.R24',
    [string]$TimestampUrl = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
if ($null -eq $cert) {
    throw "Code-signing certificate not found in Cert:\CurrentUser\My (thumbprint $Thumbprint)"
}
if (-not $cert.HasPrivateKey) {
    throw "Certificate has no private key (import the PFX into Cert:\CurrentUser\My)"
}

$dir = Join-Path $root "RevitBridge.Revit\bin\$Configuration"
$files = @('RevitBridge.Revit.dll', 'RevitBridge.Contracts.dll', 'RevitBridge.Core.dll')
$missing = @()
foreach ($file in $files) {
    $path = Join-Path $dir $file
    if (-not (Test-Path -LiteralPath $path)) { $missing += $path; continue }

    $arguments = @{
        FilePath = $path
        Certificate = $cert
        HashAlgorithm = 'SHA256'
    }
    if ($TimestampUrl) { $arguments.TimestampServer = $TimestampUrl }

    $signature = Set-AuthenticodeSignature @arguments
    Write-Output ("{0}: {1}" -f $file, $signature.Status)
    if ($signature.Status -ne 'Valid') {
        Write-Output ("  " + $signature.StatusMessage)
    }
}
if ($missing.Count -gt 0) {
    throw ("Build output not found: " + ($missing -join '; '))
}
