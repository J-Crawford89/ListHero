param([string]$ReleaseDirectory = '.artifacts/ci-beta')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DeploymentDiagnostics.Common.ps1')
$taskDeploymentClock = [Diagnostics.Stopwatch]::StartNew()
$taskDeploymentStage = 'preflight'
try {
. (Join-Path $PSScriptRoot 'AzureHosting.Common.ps1') -UseSystemAzureCli
Assert-FreeHostingTemplate
$account = Assert-HostingAccount
if ($account.tenantId -ne '9c7e1e73-a0b2-48e9-a616-4747fd800348') { throw 'Use the subscription hosting tenant.' }
$release = [IO.Path]::GetFullPath((Join-Path $hostingRoot $ReleaseDirectory))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $hostingRoot '.artifacts'))
if (!$release.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Deploy only a release inside this repository artifact directory.'
}
foreach ($file in @('Api.zip', 'Web.zip', 'Migrations/HostingDatabase.dll', 'release.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $release $file))) { throw "Missing release file: $file" }
}
$manifest = Get-Content -LiteralPath (Join-Path $release 'release.json') -Raw | ConvertFrom-Json
if ($manifest.Commit -notmatch '^[a-fA-F0-9]{40}$') { throw 'The release manifest must identify a valid tested commit.' }
if ($env:GITHUB_SHA -and $manifest.Commit -ne $env:GITHUB_SHA) { throw 'Release does not match the tested commit.' }
if (!$env:LISTHERO_DEPLOY_ADMIN_CONNECTION) { throw 'The protected beta migration connection is required.' }
$apiName = 'listhero-api-yj3pk6oimchdi'
$webName = 'listhero-yj3pk6oimchdi'
$sqlName = 'sql-listhero-yj3pk6oimchdi'
$planName = 'asp-listhero-beta'
$plan = Invoke-HostingAzure -Arguments @('appservice', 'plan', 'show', '--resource-group', $hostingGroup,
    '--name', $planName, '--subscription', $hostingSubscription, '--query', '{id:id,name:sku.name,tier:sku.tier}') -Json
if ($plan.name -ne 'F1' -or $plan.tier -ne 'Free') { throw 'The beta hosting plan must remain F1 / Free.' }
foreach ($name in @($apiName, $webName)) {
    $site = Invoke-HostingAzure -Arguments @('webapp', 'show', '--resource-group', $hostingGroup,
        '--name', $name, '--subscription', $hostingSubscription, '--query', '{plan:appServicePlanId || serverFarmId,state:state}') -Json
    if ($site.plan -ne $plan.id -or $site.state -ne 'Running') { throw 'Both beta apps must use the existing running free plan.' }
}
$database = Invoke-HostingAzure -Arguments @('sql', 'db', 'show', '--resource-group', $hostingGroup,
    '--server', $sqlName, '--name', 'ListHeroBeta', '--subscription', $hostingSubscription,
    '--query', '{free:useFreeLimit,behavior:freeLimitExhaustionBehavior}') -Json
if ($database.free -ne $true -or $database.behavior -ne 'AutoPause') { throw 'SQL must retain its free allowance and quota pausing.' }

# Only this workflow uses this prefix. Deployment concurrency prevents simultaneous jobs.
# Clean up rules left behind if a previous runner was forcibly terminated.
$oldRules = Invoke-HostingAzure -Arguments @('sql', 'server', 'firewall-rule', 'list', '--resource-group', $hostingGroup,
    '--server', $sqlName, '--subscription', $hostingSubscription, '--query', '[].name') -Json
foreach ($name in @($oldRules | Where-Object { $_ -match '^ListHeroCI-[a-f0-9]{32}$' })) {
    Invoke-HostingAzure -Arguments @('sql', 'server', 'firewall-rule', 'delete', '--resource-group', $hostingGroup,
        '--server', $sqlName, '--name', $name, '--subscription', $hostingSubscription)
}
$address = (Invoke-RestMethod -Uri 'https://api.ipify.org').Trim()
$parsedAddress = $null
if (![Net.IPAddress]::TryParse($address, [ref]$parsedAddress) -or $parsedAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'Could not obtain the runner IPv4 address.'
}
$firewallName = 'ListHeroCI-' + [guid]::NewGuid().ToString('N')
try {
    $taskDeploymentStage = 'runner-firewall'
    Invoke-HostingAzure -Arguments @('sql', 'server', 'firewall-rule', 'create', '--resource-group', $hostingGroup,
        '--server', $sqlName, '--name', $firewallName, '--start-ip-address', $address,
        '--end-ip-address', $address, '--subscription', $hostingSubscription)
    $taskDeploymentStage = 'migration'
    # A SQL/credential failure can contain connection details. Do not forward native output.
    Invoke-BetaMigration -Assembly (Join-Path $release 'Migrations/HostingDatabase.dll')
    foreach ($hostName in @('Api', 'Web')) {
        $appName = if ($hostName -eq 'Api') { $apiName } else { $webName }
        $taskDeploymentStage = if ($hostName -eq 'Api') { 'api-deploy' } else { 'web-deploy' }
        Write-Host "Deploying $hostName from tested commit $($manifest.Commit)."
        Invoke-HostingAzure -Arguments @('webapp', 'deploy', '--resource-group', $hostingGroup, '--name', $appName,
            '--src-path', (Join-Path $release "$hostName.zip"), '--type', 'zip', '--subscription', $hostingSubscription)
    }
} finally {
    $taskPreviousStage = $taskDeploymentStage
    $taskDeploymentStage = 'firewall-cleanup'
    Invoke-HostingAzure -Arguments @('sql', 'server', 'firewall-rule', 'delete', '--resource-group', $hostingGroup,
        '--server', $sqlName, '--name', $firewallName, '--subscription', $hostingSubscription)
    $taskDeploymentStage = $taskPreviousStage
}
$taskDeploymentStage = 'verification'
& (Join-Path $PSScriptRoot 'Test-FreeBeta.ps1') -WebUrl "https://$webName.azurewebsites.net/" `
    -ApiUrl "https://$apiName.azurewebsites.net/" -UseSystemAzureCli
if ($env:GITHUB_STEP_SUMMARY) {
    "Deployed commit $($manifest.Commit) to [List Hero beta](https://$webName.azurewebsites.net/). Live checks passed." |
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}
Write-BetaDeploymentDiagnostic -Stage complete -Outcome success -ElapsedMs $taskDeploymentClock.ElapsedMilliseconds
} catch {
    Write-BetaDeploymentDiagnostic -Stage $taskDeploymentStage -Outcome failure -ElapsedMs $taskDeploymentClock.ElapsedMilliseconds
    throw "Beta deployment failed at stage '$taskDeploymentStage'. Review the redacted deployment diagnostic and Azure status; raw command/exception details were withheld."
}
