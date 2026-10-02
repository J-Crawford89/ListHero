param([string]$OutputDirectory = '.artifacts/ci-beta')
. (Join-Path $PSScriptRoot 'AzureHosting.Common.ps1')
Assert-FreeHostingTemplate
$destination = [IO.Path]::GetFullPath((Join-Path $hostingRoot $OutputDirectory))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $hostingRoot '.artifacts'))
if (!$destination.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish only inside this repository artifact directory.'
}
if (Test-Path -LiteralPath $destination) { throw 'Use a new empty release directory.' }
New-Item -ItemType Directory -Path $destination | Out-Null
foreach ($hostName in @('Api', 'Web')) {
    $hostDirectory = Join-Path $destination $hostName
    & dotnet publish (Join-Path $hostingRoot "src/ListHero.$hostName") --configuration Release `
        --runtime win-x86 --self-contained true --output $hostDirectory
    if ($LASTEXITCODE -ne 0) { throw "$hostName publication failed." }
    Compress-Archive -Path (Join-Path $hostDirectory '*') -DestinationPath (Join-Path $destination "$hostName.zip")
}
& dotnet publish (Join-Path $PSScriptRoot 'HostingDatabase/HostingDatabase.csproj') --configuration Release `
    --no-self-contained --output (Join-Path $destination 'Migrations')
if ($LASTEXITCODE -ne 0) { throw 'Database migration tool publication failed.' }
$commit = & git -C $hostingRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not identify the release commit.' }
@{ Commit = $commit; PreparedUtc = [DateTimeOffset]::UtcNow.ToString('o') } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $destination 'release.json') -Encoding UTF8
Write-Host "Beta packages prepared for $commit. Azure configuration was not changed."
