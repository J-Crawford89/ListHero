$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DeploymentDiagnostics.Common.ps1')
$taskStage = if ($env:LISTHERO_PACKAGE_OUTCOME -eq 'failure') { 'package' } elseif ($env:LISTHERO_LOGIN_OUTCOME -eq 'failure') { 'authentication' } else { 'preflight' }
Write-BetaDeploymentDiagnostic -Stage $taskStage -Outcome failure
