param([guid]$TenantId = '59a74a72-2c96-4981-9475-6b968a271e4a')

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$azureCliPath = Join-Path $repositoryRoot '.artifacts/azure-cli/bin/az.cmd'
$env:AZURE_CONFIG_DIR = Join-Path $repositoryRoot '.artifacts/azure-config'
$tenant = & $azureCliPath account show --query tenantId --output tsv --only-show-errors
if ($LASTEXITCODE -ne 0 -or $tenant.Trim() -ne $TenantId.ToString()) {
    throw 'Sign in to the List Hero tenant with Connect-Azure.ps1 first.'
}
$payloadPath = Join-Path $repositoryRoot '.artifacts/entra-request.json'
function Invoke-ListHeroGraph([string]$Method, [string]$Path, $Body = $null) {
    $arguments = @('rest', '--method', $Method, '--url', "https://graph.microsoft.com/v1.0/$Path", '--output', 'json', '--only-show-errors')
    if ($null -ne $Body) {
        $Body | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $payloadPath -Encoding UTF8
        $arguments += @('--headers', 'Content-Type=application/json', '--body', "@$payloadPath")
    }
    # Capture responses: addPassword returns a secret that must never reach terminal output.
    $response = & $azureCliPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "Microsoft Graph failed: $Method $Path" }
    if ($response) { return ($response | ConvertFrom-Json) }
}
function Get-OrCreateApp([string]$Name) {
    $matches = @((Invoke-ListHeroGraph GET "applications?`$filter=displayName eq '$Name'").value)
    if ($matches.Count -gt 1) { throw "Multiple registrations named $Name; resolve the duplicate before continuing." }
    if ($matches.Count -eq 1) { return $matches[0] }
    return Invoke-ListHeroGraph POST 'applications' @{ displayName = $Name; signInAudience = 'AzureADMyOrg' }
}
function Get-OrCreatePrincipal([string]$AppId) {
    $matches = @((Invoke-ListHeroGraph GET "servicePrincipals?`$filter=appId eq '$AppId'").value)
    if ($matches.Count -eq 1) { return $matches[0] }
    return Invoke-ListHeroGraph POST 'servicePrincipals' @{ appId = $AppId }
}

$api = Get-OrCreateApp 'List Hero API'
$web = Get-OrCreateApp 'List Hero Web'
if ($api.signInAudience -ne 'AzureADMyOrg' -or $web.signInAudience -ne 'AzureADMyOrg') {
    throw 'These registrations must belong only to the selected external tenant.'
}
$scope = @($api.api.oauth2PermissionScopes | Where-Object value -eq 'access_as_user')
$scopeId = if ($scope.Count -eq 1) { $scope[0].id } else { [guid]::NewGuid().ToString() }
$role = @($api.appRoles | Where-Object value -eq 'Admin')
$roleId = if ($role.Count -eq 1) { $role[0].id } else { [guid]::NewGuid().ToString() }
if (@($api.api.oauth2PermissionScopes | Where-Object value -ne 'access_as_user').Count -gt 0 -or
    @($api.appRoles | Where-Object value -ne 'Admin').Count -gt 0) {
    throw 'Unexpected existing API scopes/roles; review before changing registration settings.'
}
$null = Invoke-ListHeroGraph PATCH "applications/$($api.id)" @{
    identifierUris = @("api://$($api.appId)")
    api = @{
        requestedAccessTokenVersion = 2
        oauth2PermissionScopes = @(@{
            id = $scopeId; isEnabled = $true; type = 'Admin'; value = 'access_as_user'
            adminConsentDisplayName = 'Use List Hero as the signed-in user'
            adminConsentDescription = 'Allow List Hero Web to access List Hero API on behalf of the signed-in customer.'
            userConsentDisplayName = 'Use List Hero'
            userConsentDescription = 'Access your List Hero lists through the List Hero web app.'
        })
    }
    appRoles = @(@{
        id = $roleId; allowedMemberTypes = @('User'); displayName = 'Admin'; isEnabled = $true; value = 'Admin'
        description = 'Operational administration placeholder. Does not grant access to other users private lists.'
    })
}
if (@($web.requiredResourceAccess | Where-Object resourceAppId -ne $api.appId).Count -gt 0) {
    throw 'Unexpected existing web permissions; review before granting consent.'
}
$redirects = @(@($web.web.redirectUris) + @('https://localhost:7016/signin-oidc', 'https://localhost:7016/signout-callback-oidc') | Select-Object -Unique)
$null = Invoke-ListHeroGraph PATCH "applications/$($web.id)" @{
    web = @{ redirectUris = $redirects; logoutUrl = 'https://localhost:7016/signout-oidc' }
    requiredResourceAccess = @(@{
        resourceAppId = $api.appId
        resourceAccess = @(@{ id = $scopeId; type = 'Scope' })
    })
}
Write-Host 'Web/API registrations and API scope configured.'
$apiPrincipal = Get-OrCreatePrincipal $api.appId
$webPrincipal = Get-OrCreatePrincipal $web.appId
$grants = @((Invoke-ListHeroGraph GET "oauth2PermissionGrants?`$filter=clientId eq '$($webPrincipal.id)'").value |
    Where-Object { $_.resourceId -eq $apiPrincipal.id -and $_.consentType -eq 'AllPrincipals' })
if ($grants.Count -gt 1) { throw 'Multiple tenant consent grants require review.' }
if ($grants.Count -eq 0) {
    $null = Invoke-ListHeroGraph POST 'oauth2PermissionGrants' @{
        clientId = $webPrincipal.id; consentType = 'AllPrincipals'; resourceId = $apiPrincipal.id; scope = 'access_as_user'
    }
} elseif ($grants[0].scope -ne 'access_as_user') {
    throw 'An existing API consent grant has different scopes; review before changing it.'
}
Write-Host 'Tenant consent granted only for the List Hero API delegated scope.'

$flowName = 'List Hero Sign Up and Sign In'
$flows = @((Invoke-ListHeroGraph GET 'identity/authenticationEventsFlows').value | Where-Object displayName -eq $flowName)
if ($flows.Count -gt 1) { throw 'Multiple List Hero user flows require review.' }
if ($flows.Count -eq 0) {
    $flow = Invoke-ListHeroGraph POST 'identity/authenticationEventsFlows' @{
        '@odata.type' = '#microsoft.graph.externalUsersSelfServiceSignUpEventsFlow'
        displayName = $flowName
        description = 'List Hero customer email/password sign-up and sign-in.'
        conditions = @{ applications = @{ includeApplications = @(@{ appId = $web.appId }) } }
        onAuthenticationMethodLoadStart = @{
            '@odata.type' = '#microsoft.graph.onAuthenticationMethodLoadStartExternalUsersSelfServiceSignUp'
            identityProviders = @(@{ id = 'EmailPassword-OAUTH' })
        }
        onInteractiveAuthFlowStart = @{
            '@odata.type' = '#microsoft.graph.onInteractiveAuthFlowStartExternalUsersSelfServiceSignUp'
            isSignUpAllowed = $true
        }
        onAttributeCollection = @{
            '@odata.type' = '#microsoft.graph.onAttributeCollectionExternalUsersSelfServiceSignUp'
            attributes = @(
                @{ id = 'email'; displayName = 'Email Address'; description = 'Customer email address'; userFlowAttributeType = 'builtIn'; dataType = 'string' },
                @{ id = 'displayName'; displayName = 'Display Name'; description = 'Customer display name'; userFlowAttributeType = 'builtIn'; dataType = 'string' }
            )
            attributeCollectionPage = @{ views = @(@{ inputs = @(
                @{ attribute = 'email'; label = 'Email address'; inputType = 'text'; hidden = $true; editable = $false; writeToDirectory = $true; required = $true },
                @{ attribute = 'displayName'; label = 'Name'; inputType = 'text'; hidden = $false; editable = $true; writeToDirectory = $true; required = $false }
            ) }) }
        }
    }
} else {
    $flow = Invoke-ListHeroGraph GET "identity/authenticationEventsFlows/$($flows[0].id)"
    if ($web.appId -notin $flow.conditions.applications.includeApplications.appId) {
        throw 'The existing List Hero flow does not include this web registration.'
    }
}
Write-Host 'Customer sign-up/sign-in flow associated with List Hero Web.'

$webProject = Join-Path $repositoryRoot 'src/ListHero.Web/ListHero.Web.csproj'
$webSecretId = ([xml](Get-Content -LiteralPath $webProject -Raw)).Project.PropertyGroup.UserSecretsId | Where-Object { $_ }
$localSecretsPath = Join-Path $env:APPDATA "Microsoft/UserSecrets/$webSecretId/secrets.json"
$localSecrets = if (Test-Path -LiteralPath $localSecretsPath) { Get-Content -LiteralPath $localSecretsPath -Raw | ConvertFrom-Json } else { $null }
if ($localSecrets.'EntraExternalId:ClientSecret' -and $localSecrets.'EntraExternalId:ClientId' -eq $web.appId) {
    Write-Host 'Existing matching local web credential retained.'
} else {
    $expiry = [DateTime]::UtcNow.AddMonths(6).ToString('yyyy-MM-ddTHH:mm:ssZ')
    $credential = Invoke-ListHeroGraph POST "applications/$($web.id)/addPassword" @{
        passwordCredential = @{ displayName = 'List Hero local development'; endDateTime = $expiry }
    }
    @{
        'EntraExternalId:ClientSecret' = $credential.secretText
        'EntraExternalId:ClientId' = $web.appId
    } | ConvertTo-Json | & dotnet user-secrets set --project $webProject | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not persist web credential in user secrets.' }
    Write-Host "Development web credential stored in user secrets; expires $expiry."
    $credential = $null
}
$metadata = [ordered]@{
    TenantId = $TenantId.ToString(); WebClientId = $web.appId; ApiClientId = $api.appId
    WebObjectId = $web.id; ApiObjectId = $api.id; ScopeId = $scopeId; AdminRoleId = $roleId
    UserFlowId = $flow.id; ApiScope = "api://$($api.appId)/access_as_user"
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repositoryRoot '.artifacts/entra-setup.json') -Encoding UTF8
$metadata | ConvertTo-Json | Write-Host

# Public identifiers live in appsettings; credentials and local enablement stay in user secrets.
$webSettingsPath = Join-Path $repositoryRoot 'src/ListHero.Web/appsettings.json'
$apiSettingsPath = Join-Path $repositoryRoot 'src/ListHero.Api/appsettings.json'
$webSettings = Get-Content -LiteralPath $webSettingsPath -Raw | ConvertFrom-Json
$apiSettings = Get-Content -LiteralPath $apiSettingsPath -Raw | ConvertFrom-Json
if ($webSettings.EntraExternalId.TenantId -ne $TenantId.ToString() -or $apiSettings.EntraExternalId.TenantId -ne $TenantId.ToString()) {
    throw 'Host settings select another tenant; review before updating local configuration.'
}
$webSettings.EntraExternalId.ClientId = $web.appId
$webSettings.ListHeroApi.Scopes = @($metadata.ApiScope)
$apiSettings.EntraExternalId.ClientId = $api.appId
$webSettings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $webSettingsPath -Encoding UTF8
$apiSettings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $apiSettingsPath -Encoding UTF8
foreach ($project in @($webProject, (Join-Path $repositoryRoot 'src/ListHero.Api/ListHero.Api.csproj'))) {
    @{ 'Authentication:Enabled' = 'true' } | ConvertTo-Json | & dotnet user-secrets set --project $project | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable local authentication through user secrets.' }
}
Write-Host 'Local Development authentication enabled for both hosts.'
