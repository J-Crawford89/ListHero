param([switch]$UseSystemAzureCli)
$ErrorActionPreference = 'Stop'
$hostingRoot = Split-Path -Parent $PSScriptRoot
$hostingCli = Join-Path $hostingRoot '.artifacts/azure-cli/bin/az.cmd'
$hostingPython = Join-Path $hostingRoot '.artifacts/azure-cli/python.exe'
$hostingSubscription = '656761a1-f733-44d2-85d9-44cb54a2f96d'
$hostingTenant = '59a74a72-2c96-4981-9475-6b968a271e4a'
$hostingGroup = 'rg-listhero-beta'
if ($UseSystemAzureCli) {
    $hostingSystemCli = (Get-Command az -ErrorAction Stop).Source
} else {
    $env:AZURE_CONFIG_DIR = Join-Path $hostingRoot '.artifacts/azure-config'
}

function Invoke-HostingAzure {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$Json)
    $format = if ($Json) { 'json' } else { 'none' }
    # Invoke the bundled executable directly; cmd.exe would interpret URL ampersands.
    if ($UseSystemAzureCli) {
        $result = & $hostingSystemCli @Arguments --only-show-errors --output $format 2>&1 | Out-String
    } else {
        if (!(Test-Path -LiteralPath $hostingPython)) { throw 'The project-local Azure CLI is required.' }
        $result = & $hostingPython -IBm azure.cli @Arguments --only-show-errors --output $format 2>&1 | Out-String
    }
    if ($LASTEXITCODE -ne 0) {
        # Deployment errors can contain settings. Keep credentials out of terminal output.
        throw "Azure operation failed: $($Arguments[0..([Math]::Min(2, $Arguments.Length - 1))] -join ' '). Check subscription permissions, quota, and deployment status in Azure."
    }
    if ($Json -and $result.Trim()) { return $result | ConvertFrom-Json }
}

function Assert-FreeHostingTemplate {
    $template = Get-Content -LiteralPath (Join-Path $hostingRoot 'infra/azure/free-beta.json') -Raw | ConvertFrom-Json
    $resources = @($template.resources)
    $types = @('Microsoft.Web/serverfarms', 'Microsoft.Sql/servers', 'Microsoft.Sql/servers/databases',
        'Microsoft.Web/sites', 'Microsoft.Web/sites/basicPublishingCredentialsPolicies')
    if (@($resources | Where-Object type -NotIn $types).Count -or $resources.Count -ne 9) {
        throw 'The free deployment must contain only the reviewed web, SQL, and publishing-policy resources.'
    }
    $plan = @($resources | Where-Object type -EQ 'Microsoft.Web/serverfarms')
    $db = @($resources | Where-Object type -EQ 'Microsoft.Sql/servers/databases')
    $sites = @($resources | Where-Object type -EQ 'Microsoft.Web/sites')
    if ($plan.Count -ne 1 -or $plan[0].sku.name -ne 'F1' -or $plan[0].sku.tier -ne 'Free' -or
        $db.Count -ne 1 -or $db[0].properties.useFreeLimit -ne $true -or
        $db[0].properties.freeLimitExhaustionBehavior -ne 'AutoPause' -or
        $db[0].properties.maxSizeBytes -gt 34359738368 -or
        $db[0].properties.requestedBackupStorageRedundancy -ne 'Local' -or $sites.Count -ne 2 -or
        @($sites | Where-Object { $_.properties.siteConfig.alwaysOn }).Count) {
        throw 'A free-tier safeguard changed. Review the template before deploying.'
    }
    if ($template.parameters.location.defaultValue -ne 'centralus' -or
        @($template.parameters.location.allowedValues).Count -ne 1 -or
        $template.parameters.location.allowedValues[0] -ne 'centralus') { throw 'Only Central US is approved.' }
    Write-Host 'Template safeguards verified: F1 hosting, SQL free allowance, and pause instead of paid overage.'
}

function Assert-HostingAccount {
    $account = Invoke-HostingAzure -Arguments @('account', 'show', '--subscription', $hostingSubscription) -Json
    if ($account.id -ne $hostingSubscription -or $account.state -ne 'Enabled') {
        throw 'Sign in with scripts/Connect-AzureHosting.ps1 before deploying.'
    }
    return $account
}
