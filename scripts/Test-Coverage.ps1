param(
    [Parameter(Mandatory)][string]$CoverageFile,
    [Parameter(Mandatory)][string]$TestResultsFile,
    [string]$ThresholdsFile = (Join-Path $PSScriptRoot '../coverage-thresholds.json'),
    [switch]$RequireAllTests
)
$ErrorActionPreference = 'Stop'
[xml]$taskReport = Get-Content -LiteralPath $CoverageFile -Raw
[xml]$taskTests = Get-Content -LiteralPath $TestResultsFile -Raw
$taskThresholds = Get-Content -LiteralPath $ThresholdsFile -Raw | ConvertFrom-Json
$taskCounters = $taskTests.TestRun.ResultSummary.Counters
if (!$taskCounters -or [int]$taskCounters.total -eq 0 -or [int]$taskCounters.failed -gt 0 -or $taskTests.TestRun.ResultSummary.outcome -ne 'Completed') {
    throw 'The test run must complete successfully and contain executed tests.'
}
if ($RequireAllTests -and ([int]$taskCounters.notExecuted -gt 0 -or [int]$taskCounters.executed -ne [int]$taskCounters.total)) {
    throw 'The full CI run must execute every test, including SQL and browser tests.'
}
$taskCulture = [System.Globalization.CultureInfo]::InvariantCulture
$taskFailures = [System.Collections.Generic.List[string]]::new()
$taskSummary = [System.Collections.Generic.List[string]]::new()
$taskSummary.Add('| Assembly | Lines | Branches | Minimum lines / branches |')
$taskSummary.Add('| --- | ---: | ---: | ---: |')
foreach ($taskSetting in $taskThresholds.PSObject.Properties) {
    $taskPackage = @($taskReport.coverage.packages.package | Where-Object name -EQ $taskSetting.Name)
    if ($taskPackage.Count -ne 1) { $taskFailures.Add("Missing or duplicate assembly: $($taskSetting.Name)"); continue }
    $taskLines = 100 * [double]::Parse($taskPackage[0].'line-rate', $taskCulture)
    $taskBranches = 100 * [double]::Parse($taskPackage[0].'branch-rate', $taskCulture)
    if ($taskLines + 0.000001 -lt $taskSetting.Value.line -or $taskBranches + 0.000001 -lt $taskSetting.Value.branch) {
        $taskFailures.Add("$($taskSetting.Name) is below its coverage minimum.")
    }
    $taskSummary.Add(('| {0} | {1:F1}% | {2:F1}% | {3}% / {4}% |' -f $taskSetting.Name, $taskLines, $taskBranches, $taskSetting.Value.line, $taskSetting.Value.branch))
}
$taskSummary | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) { $taskSummary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
if ($taskFailures.Count) { throw ($taskFailures -join [Environment]::NewLine) }
Write-Host 'Coverage requirements passed.'
