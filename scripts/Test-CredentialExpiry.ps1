param(
    [string]$InventoryPath = (Join-Path $PSScriptRoot '../ops/credential-expiry.json'),
    [string]$AsOfUtc
)
$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'CredentialExpiry.Common.ps1')
    $now = [DateTimeOffset]::UtcNow
    if ($AsOfUtc -and ![DateTimeOffset]::TryParseExact($AsOfUtc, [string[]]@('o', "yyyy-MM-dd'T'HH:mm:ss'Z'"), [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$now)) { throw 'invalid clock' }
    $inventory = Read-CredentialInventory $InventoryPath
    $report = Get-CredentialExpiryReport -Inventory $inventory -Now $now
    Write-CredentialExpiryReport $report
    if ($report.HasAlerts) { exit 1 }
    exit 0
} catch {
    $message = 'List Hero credential expiry check failed: missing or invalid inventory, clock, or output configuration. Raw details were withheld.'
    Write-Host "::error title=List Hero credential expiry::$message"
    if ($env:GITHUB_STEP_SUMMARY) { try { $message | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 } catch { } }
    exit 2
}
