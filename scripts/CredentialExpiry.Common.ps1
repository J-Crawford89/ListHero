function Read-CredentialInventory {
    param([Parameter(Mandatory)][string]$Path)
    try {
        if ((Get-Item -LiteralPath $Path -ErrorAction Stop).Length -gt 16384) { throw 'invalid' }
        $inventory = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json -AsHashtable -Depth 8 -ErrorAction Stop
        if ($inventory -isnot [hashtable] -or $inventory.Count -ne 2 -or !$inventory.ContainsKey('version') -or !$inventory.ContainsKey('credentials') `
            -or ($inventory.version -isnot [long] -and $inventory.version -isnot [int]) -or $inventory.version -ne 1 `
            -or $inventory.credentials -isnot [array] -or $inventory.credentials.Count -ne 3) { throw 'invalid' }
        $expected = @('entra-web-secret', 'api-key-certificate', 'web-key-certificate')
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $inventory.credentials) {
            if ($entry -isnot [hashtable] -or $entry.Count -ne 4) { throw 'invalid' }
            foreach ($name in @('id','expiresOn','source','recordedOn')) { if (!$entry.ContainsKey($name)) { throw 'invalid' } }
            if ($entry.id -isnot [string] -or $expected -cnotcontains $entry.id -or !$seen.Add($entry.id) `
                -or $entry.source -isnot [string] -or @('runbook','local-backup','provider','operator','unknown') -cnotcontains $entry.source) { throw 'invalid' }
            foreach ($name in @('expiresOn','recordedOn')) {
                if ($name -eq 'expiresOn' -and $null -eq $entry[$name]) { continue }
                $date = [datetime]::MinValue
                if ($entry[$name] -isnot [string] -or ![datetime]::TryParseExact($entry[$name], 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$date)) { throw 'invalid' }
            }
            if (($entry.source -eq 'unknown') -ne ($null -eq $entry.expiresOn)) { throw 'invalid' }
        }
        return $inventory
    } catch { throw 'Credential inventory is missing or invalid. Raw input details were withheld.' }
}

function Get-CredentialExpiryReport {
    param([Parameter(Mandatory)][hashtable]$Inventory, [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow)
    $rows = foreach ($entry in $Inventory.credentials) {
        $status = 'unknown'; $days = $null
        if ($null -ne $entry.expiresOn) {
            # Date-only records deliberately use the earliest instant of the UTC expiry date.
            $expiry = [DateTimeOffset]::ParseExact($entry.expiresOn, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
            $days = [int][Math]::Ceiling(($expiry - $Now).TotalDays)
            $status = if ($expiry -le $Now) { 'expired' } elseif ($expiry -le $Now.AddDays(7)) { 'urgent' } elseif ($expiry -le $Now.AddDays(30)) { 'due' } else { 'healthy' }
        }
        [ordered]@{ Id = $entry.id; ExpiresOn = $entry.expiresOn; DaysRemaining = $days; Status = $status; Source = $entry.source; RecordedOn = $entry.recordedOn }
    }
    return [ordered]@{ Version = 1; CheckedUtc = $Now.ToUniversalTime().ToString('o'); HasAlerts = @($rows | Where-Object Status -ne healthy).Count -gt 0; Credentials = @($rows) }
}

function Write-CredentialExpiryReport {
    param([Parameter(Mandatory)]$Report)
    $labels = @{ 'entra-web-secret' = 'Entra web sign-in secret'; 'api-key-certificate' = 'API key-protection certificate'; 'web-key-certificate' = 'Web key-protection certificate' }
    $summary = [Collections.Generic.List[string]]::new()
    $summary.Add('## List Hero credential expiry')
    $summary.Add('Recorded expiry dates only; no Azure/Entra live query, application wake-up, or database check.')
    $summary.Add('')
    $summary.Add('| Credential | UTC expiry date | Days remaining | Status | Source |')
    $summary.Add('| --- | --- | ---: | --- | --- |')
    foreach ($entry in $Report.Credentials) {
        $label = $labels[$entry.Id]
        $summary.Add("| $label | $($entry.ExpiresOn) | $($entry.DaysRemaining) | $($entry.Status) | $($entry.Source) |")
        if ($entry.Status -ne 'healthy') {
            $kind = if ($entry.Status -eq 'due') { 'warning' } else { 'error' }
            # Every value below has been validated or computed; never echo native errors/input fields.
            Write-Host "::$kind title=List Hero credential expiry::$label status: $($entry.Status). Review docs/credential-expiry-alerts.md."
        }
    }
    $summary | ForEach-Object { Write-Host $_ }
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
    $Report | ConvertTo-Json -Depth 5 | Write-Output
}

function Set-CredentialExpiryRecord {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$ExpiresOn, [ValidateSet('local-backup','provider','operator')][string]$Source = 'operator')
    $inventory = Read-CredentialInventory $Path
    $entry = @($inventory.credentials | Where-Object id -CEQ $Id)
    $date = [datetime]::MinValue
    if ($entry.Count -ne 1 -or ![datetime]::TryParseExact($ExpiresOn, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$date)) { throw 'Choose a known credential and an ISO expiry date.' }
    $entry[0].expiresOn = $ExpiresOn; $entry[0].source = $Source; $entry[0].recordedOn = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
    $inventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Path -Encoding utf8
}
