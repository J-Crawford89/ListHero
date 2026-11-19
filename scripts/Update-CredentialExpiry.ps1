param(
    [string]$InventoryPath = (Join-Path $PSScriptRoot '../ops/credential-expiry.json'),
    [string]$CredentialId,
    [string]$ExpiresOn,
    [string]$CertificateBackupPath = (Join-Path $PSScriptRoot '../.artifacts/azure-hosting/beta-secrets.dpapi'),
    [switch]$ReadLocalCertificateBackup
)
$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'CredentialExpiry.Common.ps1')
    if ($ReadLocalCertificateBackup) {
        if ($CredentialId -or $ExpiresOn -or !$IsWindows) { throw 'invalid mode' }
        $entropy = [Text.Encoding]::UTF8.GetBytes('ListHero free beta deployment v1')
        $clear = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($CertificateBackupPath), $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        try {
            $backup = [Text.Encoding]::UTF8.GetString($clear) | ConvertFrom-Json -AsHashtable -ErrorAction Stop
            foreach ($pair in @(@('apiProtectionCertificate','api-key-certificate'), @('webProtectionCertificate','web-key-certificate'))) {
                $pem = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($backup[$pair[0]]))
                # Read the public certificate only. No private key/password/secret is exported or printed.
                $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem($pem)
                try { Set-CredentialExpiryRecord -Path $InventoryPath -Id $pair[1] -ExpiresOn $certificate.NotAfter.ToUniversalTime().ToString('yyyy-MM-dd') -Source local-backup }
                finally { $certificate.Dispose() }
            }
        } finally { [Array]::Clear($clear, 0, $clear.Length); $backup = $null; $pem = $null }
    } else {
        Set-CredentialExpiryRecord -Path $InventoryPath -Id $CredentialId -ExpiresOn $ExpiresOn
    }
    Write-Host 'Non-secret expiry inventory updated locally. Review and commit it after verifying the credential in use. No Azure setting was changed.'
    exit 0
} catch { Write-Host 'Could not update the expiry inventory. Check the selected mode, non-secret dates, and local backup access. Raw details were withheld.'; exit 2 }
