param([Parameter(Mandatory)][string]$OutputDirectory, [switch]$WritersQuiesced, [switch]$UseSystemAzureCli)
$ErrorActionPreference = 'Stop'
$taskNames = @('LISTHERO_BACKUP_ARM_TOKEN', 'AZURE_CONFIG_DIR')
$taskPrevious = @{}
foreach ($taskName in $taskNames) { $taskPrevious[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process') }
try {
    if (!$WritersQuiesced -or !$env:LISTHERO_BACKUP_PASSPHRASE -or !$env:LISTHERO_MAINTENANCE_CONNECTION) { throw 'preflight' }
    $taskRoot = Split-Path -Parent $PSScriptRoot
    $taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
    $taskRepository = [IO.Path]::GetFullPath($taskRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $taskArtifacts = [IO.Path]::GetFullPath((Join-Path $taskRoot '.artifacts')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($taskOutput.StartsWith($taskRepository, [StringComparison]::OrdinalIgnoreCase) -and !$taskOutput.StartsWith($taskArtifacts, [StringComparison]::OrdinalIgnoreCase)) { throw 'preflight' }
    $taskTool = Join-Path $taskRoot 'scripts/LiveBackup/bin/Release/net10.0/ListHero.LiveBackup.dll'
    if (!(Test-Path -LiteralPath $taskTool) -or (Test-Path -LiteralPath $taskOutput)) { throw 'preflight' }
    . (Join-Path $PSScriptRoot 'AzureHosting.Common.ps1') -UseSystemAzureCli:$UseSystemAzureCli
    $null = Assert-HostingAccount
    $taskToken = Invoke-HostingAzure -Arguments @('account', 'get-access-token', '--subscription', $hostingSubscription, '--resource', 'https://management.azure.com/') -Json
    if (!$taskToken.accessToken -or $taskToken.subscription -ne $hostingSubscription) { throw 'preflight' }
    $env:LISTHERO_BACKUP_ARM_TOKEN = $taskToken.accessToken
    # Suppress even unexpected native stderr; the console returns only allowlisted report fields.
    $taskReport = & dotnet $taskTool capture-beta --output $taskOutput --writers-quiesced 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'capture' }
    $taskResult = $taskReport | ConvertFrom-Json
    if ($taskResult.Outcome -ne 'captured' -or $taskResult.ArchiveVerified -ne $true -or $taskResult.ReleaseCommit -cnotmatch '^[0-9a-f]{40}$' -or $taskResult.Files -ne 11) { throw 'report' }
    @{ Outcome = 'captured'; ArchiveVerified = $true; ReleaseCommit = $taskResult.ReleaseCommit; Files = 11 } | ConvertTo-Json -Compress
    exit 0
} catch {
    Write-Output '{"Outcome":"failed","Reason":"backup-capture-failed; check prerequisites and private output directory"}'
    exit 1
} finally {
    foreach ($taskName in $taskNames) { [Environment]::SetEnvironmentVariable($taskName, $taskPrevious[$taskName], 'Process') }
    $taskToken = $null
}
