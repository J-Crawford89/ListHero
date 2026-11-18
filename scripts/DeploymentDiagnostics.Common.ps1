function Invoke-BetaMigration {
    param([Parameter(Mandatory)][string]$Assembly, [string]$Dotnet = 'dotnet')
    $PSNativeCommandUseErrorActionPreference = $false
    $taskMigrationOutput = & $Dotnet $Assembly --migrate-only 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'Database migrations failed; applications were not deployed. Raw native output was withheld.' }
}

function Write-BetaDeploymentDiagnostic {
    param(
        [ValidateSet('preflight','runner-firewall','migration','api-deploy','web-deploy','firewall-cleanup','verification','package','authentication','complete')][string]$Stage,
        [ValidateSet('success','failure')][string]$Outcome,
        [long]$ElapsedMs = 0,
        [string]$OutputDirectory = $(if ($env:LISTHERO_DIAGNOSTIC_DIRECTORY) { $env:LISTHERO_DIAGNOSTIC_DIRECTORY } else { Join-Path $PSScriptRoot '../.artifacts' })
    )
    $taskArtifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.artifacts'))
    $taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
    if ($taskOutput -ne $taskArtifactRoot -and !$taskOutput.StartsWith($taskArtifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Diagnostic output must stay inside the repository artifact directory.'
    }
    $taskCommit = if ($env:GITHUB_SHA -match '^[a-fA-F0-9]{40}$') { $env:GITHUB_SHA.ToLowerInvariant() } else { 'unknown' }
    $taskRun = if ($env:GITHUB_RUN_ID -match '^[0-9]{1,32}$') { $env:GITHUB_RUN_ID } else { 'local' }
    $taskRecord = [ordered]@{ Stage = $Stage; Outcome = $Outcome; Commit = $taskCommit; Run = $taskRun; ElapsedMs = [Math]::Max(0, $ElapsedMs); RecordedUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
    $taskRecord | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'deployment-diagnostic.json') -Encoding utf8
    $taskSummary = "List Hero beta: $Outcome at stage $Stage; commit $taskCommit; run $taskRun; elapsed $($taskRecord.ElapsedMs) ms."
    Write-Host $taskSummary
    if ($env:GITHUB_STEP_SUMMARY) { $taskSummary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
}
