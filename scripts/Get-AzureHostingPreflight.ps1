. (Join-Path $PSScriptRoot 'AzureHosting.Common.ps1')
Assert-FreeHostingTemplate
$account = Assert-HostingAccount
Write-Host "Hosting subscription: $($account.name) ($($account.id))"
$locations = @(Invoke-HostingAzure -Arguments @('appservice', 'list-locations', '--sku', 'F1', '--subscription', $hostingSubscription) -Json)
if (!@($locations | Where-Object { $_.name -eq 'Central US' -or $_.name -eq 'centralus' -or $_.displayName -eq 'Central US' }).Count) {
    throw 'F1 hosting is not offered in Central US for this subscription. No paid fallback will be selected.'
}
Write-Host 'Central US is listed for F1. Azure template validation will check actual quota and SQL eligibility.'
$quota = @(Invoke-HostingAzure -Arguments @('rest', '--method', 'get', '--uri',
    "https://management.azure.com/subscriptions/$hostingSubscription/providers/Microsoft.Web/locations/centralus/usages?api-version=2025-05-01",
    '--subscription', $hostingSubscription, '--query', "value[?name.value=='F1'].{current:currentValue,limit:limit}") -Json)
if ($quota.Count -ne 1 -or $quota[0].current -ge $quota[0].limit) { throw 'No available F1 quota was confirmed in Central US.' }
Write-Host "F1 regional quota: $($quota[0].current) used of $($quota[0].limit)."
$providers = @(Invoke-HostingAzure -Arguments @('provider', 'list', '--subscription', $hostingSubscription,
    '--query', "[?namespace=='Microsoft.Web' || namespace=='Microsoft.Sql'].{provider:namespace,state:registrationState}") -Json)
$providers | Format-Table provider, state
Write-Host 'Read-only preflight complete. No resources were created.'
