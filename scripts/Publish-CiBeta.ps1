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
        --runtime win-x86 --self-contained true --output $hostDirectory -p:PlaywrightPlatform=win
    if ($LASTEXITCODE -ne 0) { throw "$hostName publication failed." }
    if ($hostName -eq 'Api') {
        # Build-time download only. Bundle the pinned Windows headless shell; never install at startup.
        $taskOldBrowserPath = $env:PLAYWRIGHT_BROWSERS_PATH
        $taskOldPlatform = $env:PLAYWRIGHT_HOST_PLATFORM_OVERRIDE
        $taskBrowserCache = Join-Path $destination 'browser-download'
        try {
            $env:PLAYWRIGHT_BROWSERS_PATH = $taskBrowserCache
            $env:PLAYWRIGHT_HOST_PLATFORM_OVERRIDE = 'win64'
            # This workflow runs on Linux but publishes the Windows API and its Windows browser.
            & node (Join-Path $hostDirectory '.playwright/package/cli.js') install chromium-headless-shell --only-shell
            if ($LASTEXITCODE -ne 0) { throw 'Browser download failed.' }
            $taskExecutables = @(Get-ChildItem -LiteralPath $taskBrowserCache -Filter 'chrome-headless-shell.exe' -File -Recurse)
            if ($taskExecutables.Count -ne 1) { throw 'Expected one pinned Windows headless browser.' }
            Copy-Item -LiteralPath $taskExecutables[0].Directory.FullName -Destination (Join-Path $hostDirectory 'product-browser') -Recurse
        } finally { $env:PLAYWRIGHT_BROWSERS_PATH = $taskOldBrowserPath; $env:PLAYWRIGHT_HOST_PLATFORM_OVERRIDE = $taskOldPlatform }
    }
    # Compress-Archive omits dot-directories on Linux, including the Playwright driver.
    $taskZip = Join-Path $destination "$hostName.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($hostDirectory, $taskZip)
    if ($hostName -eq 'Api') {
        $taskArchive = [IO.Compression.ZipFile]::OpenRead($taskZip)
        try {
            $taskEntryNames = @($taskArchive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
            foreach ($taskRequired in @('.playwright/node/win32_x64/node.exe', '.playwright/package/cli.js', 'product-browser/chrome-headless-shell.exe')) {
                if ($taskRequired -notin $taskEntryNames) { throw "API package is missing $taskRequired." }
            }
        } finally { $taskArchive.Dispose() }
    }
}
& dotnet publish (Join-Path $PSScriptRoot 'HostingDatabase/HostingDatabase.csproj') --configuration Release `
    --no-self-contained --output (Join-Path $destination 'Migrations')
if ($LASTEXITCODE -ne 0) { throw 'Database migration tool publication failed.' }
$commit = & git -C $hostingRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not identify the release commit.' }
@{ Commit = $commit; PreparedUtc = [DateTimeOffset]::UtcNow.ToString('o') } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $destination 'release.json') -Encoding UTF8
Write-Host "Beta packages prepared for $commit. Azure configuration was not changed."
