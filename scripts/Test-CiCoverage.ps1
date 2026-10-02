param([Parameter(Mandatory)][string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter coverage.cobertura.xml)
if (!$reports.Count) { throw 'No coverage report was produced.' }
# VSTest can copy its collector attachment into the TRX results directory.
# Accept identical copies, but never choose between different measurements.
$hashes = @($reports | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } | Select-Object -Unique)
if ($hashes.Count -ne 1) { throw 'Multiple different coverage reports were produced.' }
& (Join-Path $PSScriptRoot 'Test-Coverage.ps1') -CoverageFile $reports[0].FullName `
    -TestResultsFile (Join-Path $ResultsDirectory 'coverage.trx') -RequireAllTests
