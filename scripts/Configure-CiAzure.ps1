. (Join-Path $PSScriptRoot 'AzureHosting.Common.ps1')
$account = Assert-HostingAccount
if ($account.tenantId -ne '9c7e1e73-a0b2-48e9-a616-4747fd800348') { throw 'Use the subscription hosting tenant.' }
$name = 'ListHero GitHub beta deployment'
$applications = @(Invoke-HostingAzure -Arguments @('ad', 'app', 'list', '--display-name', $name,
    '--query', '[].{id:id,appId:appId}') -Json)
if ($applications.Count -gt 1) { throw 'More than one matching deployment registration exists.' }
if ($applications.Count -eq 1) {
    $application = $applications[0]
} else {
    $application = Invoke-HostingAzure -Arguments @('ad', 'app', 'create', '--display-name', $name,
        '--sign-in-audience', 'AzureADMyOrg', '--query', '{id:id,appId:appId}') -Json
}
$principals = @(Invoke-HostingAzure -Arguments @('ad', 'sp', 'list', '--filter', "appId eq '$($application.appId)'",
    '--query', '[].id') -Json)
if ($principals.Count -eq 1) {
    $principalId = $principals[0]
} elseif ($principals.Count -eq 0) {
    $principalId = Invoke-HostingAzure -Arguments @('ad', 'sp', 'create', '--id', $application.appId, '--query', 'id') -Json
} else { throw 'More than one matching deployment service principal exists.' }
$federation = Get-Content -LiteralPath (Join-Path $hostingRoot 'infra/azure/github-beta-federation.json') -Raw | ConvertFrom-Json
$existingFederation = @(Invoke-HostingAzure -Arguments @('ad', 'app', 'federated-credential', 'list', '--id', $application.id) -Json)
$matchingFederation = @($existingFederation | Where-Object name -EQ $federation.name)
if ($matchingFederation.Count) {
    if ($matchingFederation[0].issuer -ne $federation.issuer -or $matchingFederation[0].subject -ne $federation.subject -or
        @($matchingFederation[0].audiences).Count -ne 1 -or $matchingFederation[0].audiences[0] -ne $federation.audiences[0]) {
        throw 'The existing trust differs from the reviewed beta federation.'
    }
} else {
    Invoke-HostingAzure -Arguments @('ad', 'app', 'federated-credential', 'create', '--id', $application.id,
        '--parameters', ('@' + (Join-Path $hostingRoot 'infra/azure/github-beta-federation.json')))
}
$roleName = 'ListHero beta CI SQL firewall'
$role = @(Invoke-HostingAzure -Arguments @('role', 'definition', 'list', '--name', $roleName, '--query', '[].name') -Json)
if (!$role.Count) {
    $roleId = Invoke-HostingAzure -Arguments @('role', 'definition', 'create', '--role-definition',
        (Join-Path $hostingRoot 'infra/azure/github-beta-firewall-role.json'), '--query', 'name') -Json
} else { $roleId = $role[0] }
$groupScope = "/subscriptions/$hostingSubscription/resourceGroups/$hostingGroup"
$grants = @(
    @{ Role = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'; Scope = $groupScope }, # Reader
    @{ Role = 'de139f84-1756-47ae-9be6-808fbbe84772'; Scope = "$groupScope/providers/Microsoft.Web/sites/listhero-yj3pk6oimchdi" }, # Website Contributor
    @{ Role = 'de139f84-1756-47ae-9be6-808fbbe84772'; Scope = "$groupScope/providers/Microsoft.Web/sites/listhero-api-yj3pk6oimchdi" },
    @{ Role = $roleId; Scope = "$groupScope/providers/Microsoft.Sql/servers/sql-listhero-yj3pk6oimchdi" }
)
foreach ($grant in $grants) {
    Invoke-HostingAzure -Arguments @('role', 'assignment', 'create', '--assignee-object-id', $principalId,
        '--assignee-principal-type', 'ServicePrincipal', '--role', $grant.Role, '--scope', $grant.Scope,
        '--subscription', $hostingSubscription)
}
$configuration = @{ ClientId = $application.appId; TenantId = $account.tenantId; SubscriptionId = $hostingSubscription }
$outputDirectory = Join-Path $hostingRoot '.artifacts/ci-config'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$configuration | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDirectory 'azure.json') -Encoding UTF8
$configuration | ConvertTo-Json
Write-Host 'Beta GitHub federation and scoped deployment roles configured. No Azure client secret was created.'
