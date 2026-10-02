param([switch]$PrepareOnly)
. (Join-Path $PSScriptRoot 'AzureHosting.Common.ps1')
Assert-FreeHostingTemplate

$artifactDirectory = Join-Path $hostingRoot '.artifacts/azure-hosting'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$releaseDirectory = Join-Path $artifactDirectory ('releases/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
foreach ($hostName in @('Api', 'Web')) {
    $destination = Join-Path $releaseDirectory $hostName
    & dotnet publish (Join-Path $hostingRoot "src/ListHero.$hostName") --configuration Release --runtime win-x86 --self-contained true --output $destination
    if ($LASTEXITCODE -ne 0) { throw "$hostName publishing failed." }
    Compress-Archive -Path (Join-Path $destination '*') -DestinationPath (Join-Path $releaseDirectory "$hostName.zip")
}
& dotnet build (Join-Path $PSScriptRoot 'HostingDatabase/HostingDatabase.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'The database deployment helper could not be built.' }
@{ ReleaseDirectory = $releaseDirectory; PreparedUtc = [DateTimeOffset]::UtcNow.ToString('o') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactDirectory 'prepared-release.json') -Encoding UTF8
if ($PrepareOnly) {
    Write-Host "Free beta packages prepared at $releaseDirectory. No Azure resources created."
    return
}
& (Join-Path $PSScriptRoot 'Deploy-FreeBeta.ps1') -ReleaseDirectory $releaseDirectory
