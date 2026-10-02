param([guid]$SubscriptionId = '656761a1-f733-44d2-85d9-44cb54a2f96d', [switch]$UseDeviceCode)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$azureCli = Join-Path $taskRoot '.artifacts/azure-cli/bin/az.cmd'
if (!(Test-Path -LiteralPath $azureCli)) { throw 'The project-local Azure CLI is required.' }
if ($SubscriptionId -eq [guid]::Empty) { throw 'A valid subscription ID is required.' }
$env:AZURE_CONFIG_DIR = Join-Path $taskRoot '.artifacts/azure-config'
& $azureCli config set core.enable_broker_on_windows=false core.login_experience_v2=off --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Could not configure browser sign-in.' }
$loginArguments = @('login', '--output', 'none')
if ($UseDeviceCode) { $loginArguments += '--use-device-code' }
Write-Host 'Sign in with the account that owns the hosting subscription, in your regular browser.'
& $azureCli @loginArguments
if ($LASTEXITCODE -ne 0) { throw 'Hosting sign-in did not complete.' }
& $azureCli account set --subscription $SubscriptionId.ToString() --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'The selected account cannot access the requested hosting subscription.' }
& $azureCli account show --query '{subscription:id,name:name,tenant:tenantId,state:state}' --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Could not verify subscription access.' }
Write-Host 'Hosting subscription selected. No Azure resources were created.'
