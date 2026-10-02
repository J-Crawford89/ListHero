param([switch]$Smoke)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
$apiPort = if ($Smoke) { 7443 } else { 7223 }
$webPort = if ($Smoke) { 7336 } else { 7016 }
$prefix = if ($Smoke) { 'smoke' } else { 'local' }
if (@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object LocalPort -In @($apiPort, $webPort)).Count -gt 0) {
    throw 'A requested development port is already in use. Stop the existing host before starting another.'
}
$fixture = $null
if ($Smoke) {
    $fixture = Get-Content -LiteralPath (Join-Path $taskRoot '.artifacts/ui-fixture.json') -Raw | ConvertFrom-Json
    if ($fixture.Database -notmatch '^ListHero_Integration_[a-f0-9]{32}_Core$') { throw 'Unexpected smoke-test database.' }
    "https://localhost:$webPort/view/$($fixture.ListId)#$($fixture.ShareToken)" |
        Set-Content -LiteralPath (Join-Path $taskRoot '.artifacts/smoke-url.txt') -Encoding UTF8
}
$environmentNames = @('ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT', 'ASPNETCORE_URLS',
    'ConnectionStrings__ListHero', 'ListHeroApi__BaseUrl', 'Development__CookieName', 'Development__GuestStorageKey')
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$hosts = @()
try {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'
    $env:ListHeroApi__BaseUrl = "https://localhost:$apiPort/"
    if ($Smoke) {
        $env:ConnectionStrings__ListHero = "Server=(localdb)\MSSQLLocalDB;Database=$($fixture.Database);Trusted_Connection=True;TrustServerCertificate=True"
        $env:Development__CookieName = 'ListHero.Smoke.Session'
        $env:Development__GuestStorageKey = 'listhero.guest.smoke.v1'
    } else {
        [Environment]::SetEnvironmentVariable('Development__CookieName', $null, 'Process')
        [Environment]::SetEnvironmentVariable('Development__GuestStorageKey', $null, 'Process')
    }
    foreach ($hostName in @('Api', 'Web')) {
        $project = Join-Path $taskRoot "src/ListHero.$hostName"
        $dll = Join-Path $project "bin/Release/net10.0/ListHero.$hostName.dll"
        if (-not (Test-Path -LiteralPath $dll)) { throw 'Build ListHero.slnx in Release before starting the local hosts.' }
        $env:ASPNETCORE_URLS = if ($hostName -eq 'Api') { "https://localhost:$apiPort" } else { "https://localhost:$webPort" }
        $process = Start-Process -FilePath $dotnetPath -ArgumentList ('"{0}"' -f $dll) -WorkingDirectory $project -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $taskRoot ".artifacts/$prefix-$hostName.stdout.log") `
            -RedirectStandardError (Join-Path $taskRoot ".artifacts/$prefix-$hostName.stderr.log")
        $hosts += @{ Host = $hostName; ProcessId = $process.Id; StartedUtc = $process.StartTime.ToUniversalTime().ToString('o') }
    }
    $hosts | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRoot ".artifacts/$prefix-hosts.json") -Encoding UTF8
    Write-Host "Local hosts started. Web: https://localhost:$webPort/"
} catch {
    foreach ($started in $hosts) { Stop-Process -Id $started.ProcessId -ErrorAction SilentlyContinue }
    throw
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
}
