param([string]$WebUrl, [string]$ApiUrl, [switch]$UseSystemAzureCli)
. (Join-Path $PSScriptRoot 'AzureHosting.Common.ps1') -UseSystemAzureCli:$UseSystemAzureCli
if (!$WebUrl -and !$ApiUrl) {
    $state = Get-Content -LiteralPath (Join-Path $hostingRoot '.artifacts/azure-hosting/deployment-state.json') -Raw | ConvertFrom-Json
} else {
    if (!$WebUrl -or !$ApiUrl) { throw 'Both hosted URLs are required.' }
    $state = [pscustomobject]@{ webUrl = @{ value = $WebUrl }; apiUrl = @{ value = $ApiUrl } }
}
foreach ($hostName in @('webUrl', 'apiUrl')) {
    $uri = [Uri]$state.$hostName.value
    if ($uri.Scheme -ne 'https' -or $uri.Host -notmatch '^listhero-(api-)?[a-z0-9]+\.azurewebsites\.net$') { throw 'Unexpected beta address.' }
}
$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(120)
try {
    $checks = @(
        @{ Url = $state.webUrl.value; Status = 200 },
        @{ Url = $state.apiUrl.value + 'health/live'; Status = 200 },
        @{ Url = $state.apiUrl.value + 'api/status'; Status = 200 },
        @{ Url = $state.apiUrl.value + 'api/lists/mine'; Status = 401 },
        @{ Url = $state.apiUrl.value + 'api/lists/' + [guid]::NewGuid().ToString() + '/view'; Status = 404 },
        @{ Url = $state.webUrl.value + 'account/sign-in'; Status = 302 }
    )
    foreach ($check in $checks) {
        $response = $client.GetAsync($check.Url).GetAwaiter().GetResult()
        try {
            $status = [int]$response.StatusCode
            if ($status -ne $check.Status) { throw "Hosted check failed: $($check.Url) expected $($check.Status), received $status." }
            Write-Host "$($check.Url) $status"
            if ($check.Url.EndsWith('account/sign-in')) {
                $location = $response.Headers.Location
                $parameters = [Web.HttpUtility]::ParseQueryString($location.Query)
                if ($location.Host -ne 'listheroidentity.ciamlogin.com' -or
                    $parameters['redirect_uri'] -ne ($state.webUrl.value + 'signin-oidc') -or
                    $parameters['client_id'] -ne 'e9f6c3be-abaf-41a7-950b-6cf638107b37') {
                    throw 'Hosted login challenge does not match the intended identity registration.'
                }
            }
        } finally { $response.Dispose() }
    }
    Write-Host 'Hosted HTTP, database-read, anonymous authorization, and sign-in challenge checks passed.'
} finally { $client.Dispose(); $handler.Dispose() }
