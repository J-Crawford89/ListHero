param([switch]$Smoke)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$prefix = if ($Smoke) { 'smoke' } else { 'local' }
$recordPath = Join-Path $taskRoot ".artifacts/$prefix-hosts.json"
if (-not (Test-Path -LiteralPath $recordPath)) { Write-Host 'No recorded hosts to stop.'; return }
foreach ($record in (Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json)) {
    $process = Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) { continue }
    $info = Get-CimInstance Win32_Process -Filter "ProcessId = $($record.ProcessId)"
    $expectedDll = Join-Path $taskRoot "src\ListHero.$($record.Host)\bin\Release\net10.0\ListHero.$($record.Host).dll"
    if ($info.CommandLine -notlike "*$expectedDll*" -or
        $process.StartTime.ToUniversalTime().Ticks -ne ([DateTime]$record.StartedUtc).ToUniversalTime().Ticks) {
        throw 'The recorded process has changed. It was not stopped.'
    }
    Stop-Process -Id $record.ProcessId -ErrorAction Stop
}
Write-Host 'Recorded List Hero hosts stopped.'
