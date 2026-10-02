param(
    [guid]$TenantId = '59a74a72-2c96-4981-9475-6b968a271e4a',
    [switch]$UseDeviceCode
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$azureCliPath = Join-Path $repositoryRoot '.artifacts/azure-cli/bin/az.cmd'
if (-not (Test-Path -LiteralPath $azureCliPath)) {
    throw 'The project-local Azure CLI is missing. See docs/azure-setup.md for the official ZIP installation link.'
}
if ($TenantId -eq [guid]::Empty) { throw 'A valid tenant ID is required.' }

# Keep this project's login state separate from other Azure CLI sessions and out of Git.
$env:AZURE_CONFIG_DIR = Join-Path $repositoryRoot '.artifacts/azure-config'
New-Item -ItemType Directory -Path $env:AZURE_CONFIG_DIR -Force | Out-Null

# Use Microsoft's browser-based OAuth flow in the normal default browser.
& $azureCliPath config set core.enable_broker_on_windows=false core.login_experience_v2=off --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Could not configure Azure CLI browser sign-in.' }

$loginArguments = @('login', '--tenant', $TenantId.ToString(), '--allow-no-subscriptions', '--output', 'none')
if ($UseDeviceCode) { $loginArguments += '--use-device-code' }
Write-Host 'Complete Microsoft sign-in in your regular browser. Enter passwords and verification codes there.'
& $azureCliPath @loginArguments
if ($LASTEXITCODE -ne 0) { throw 'Azure sign-in did not complete. Retry with -UseDeviceCode if needed.' }

$signedInTenant = & $azureCliPath account show --query tenantId --output tsv --only-show-errors
if ($LASTEXITCODE -ne 0 -or $signedInTenant.Trim() -ne $TenantId.ToString()) {
    throw 'The active Azure CLI tenant does not match the requested tenant.'
}
Write-Host "Signed in to tenant $signedInTenant. No Azure resources were created."
