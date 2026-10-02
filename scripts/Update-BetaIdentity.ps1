. (Join-Path $PSScriptRoot 'AzureHosting.Common.ps1')
$statePath = Join-Path $hostingRoot '.artifacts/azure-hosting/deployment-state.json'
if (!(Test-Path -LiteralPath $statePath)) { throw 'Deploy the free beta first.' }
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
$webUri = [Uri]$state.webUrl.value
if ($webUri.Scheme -ne 'https' -or $webUri.Host -notmatch '^listhero-[a-z0-9]+\.azurewebsites\.net$') {
    throw 'Unexpected hosted web URL.'
}
$accounts = @(Invoke-HostingAzure -Arguments @('account', 'list') -Json)
$customerAccount = $accounts | Where-Object tenantId -EQ $hostingTenant | Select-Object -First 1
if (!$customerAccount) { throw 'Customer-tenant sign-in is needed. Run scripts/Connect-Azure.ps1 in your regular browser, then rerun this script.' }
$previousAccount = Invoke-HostingAzure -Arguments @('account', 'show', '--query', 'id') -Json
# Graph's token selection uses the active directory, even when --subscription is passed to az rest.
Invoke-HostingAzure -Arguments @('account', 'set', '--subscription', $customerAccount.id)
$patchPath = Join-Path $hostingRoot '.artifacts/azure-hosting/identity-callbacks.json'
try {
    $applicationUri = 'https://graph.microsoft.com/v1.0/applications/694de6f7-d023-4da9-b3bf-0afb2ed05f0e'
    $application = Invoke-HostingAzure -Arguments @('rest', '--method', 'get', '--uri', "$applicationUri`?`$select=id,appId,web",
        '--subscription', $customerAccount.id) -Json
    if ($application.appId -ne 'e9f6c3be-abaf-41a7-950b-6cf638107b37') { throw 'The expected web registration was not returned.' }
    $redirects = @($application.web.redirectUris) + @(
        "$($webUri.AbsoluteUri)signin-oidc", "$($webUri.AbsoluteUri)signout-callback-oidc")
    $webSettings = @{
        redirectUris = @($redirects | Sort-Object -Unique)
        homePageUrl = $application.web.homePageUrl
        logoutUrl = $application.web.logoutUrl
        implicitGrantSettings = $application.web.implicitGrantSettings
    }
    @{ web = $webSettings } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $patchPath -Encoding UTF8
    Invoke-HostingAzure -Arguments @('rest', '--method', 'patch', '--uri', $applicationUri,
        '--body', "@$patchPath", '--subscription', $customerAccount.id)
    $updated = Invoke-HostingAzure -Arguments @('rest', '--method', 'get', '--uri', "$applicationUri`?`$select=web",
        '--subscription', $customerAccount.id) -Json
    foreach ($redirect in @("$($webUri.AbsoluteUri)signin-oidc", "$($webUri.AbsoluteUri)signout-callback-oidc")) {
        if ($redirect -notin $updated.web.redirectUris) { throw 'The hosted callback URL was not saved.' }
    }
    Write-Host 'Hosted sign-in/sign-out callbacks added. Existing local callbacks were retained.'
} finally {
    if (Test-Path -LiteralPath $patchPath) { Remove-Item -LiteralPath $patchPath -Force }
    Invoke-HostingAzure -Arguments @('account', 'set', '--subscription', $previousAccount)
}
