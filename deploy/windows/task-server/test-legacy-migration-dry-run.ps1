[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $LegacyRoot,

    [Parameter(Mandatory)]
    [string] $TaskServerExecutable,

    [string] $WorkspaceName = 'Agent Studio',

    [string] $WorkingDirectory = (Join-Path $env:TEMP ('task-server-migration-' + [Guid]::NewGuid().ToString('N'))),

    [string] $ReportPath = (Join-Path (Get-Location) 'task-server-migration-dry-run.json'),

    [ValidateRange(1024, 65535)]
    [int] $Port = 5072
)

$ErrorActionPreference = 'Stop'
$legacy = (Resolve-Path -LiteralPath $LegacyRoot).Path
$executable = (Resolve-Path -LiteralPath $TaskServerExecutable).Path
if (Test-Path -LiteralPath $WorkingDirectory) {
    throw "Dry-run directory already exists: $WorkingDirectory"
}

$copy = Join-Path $WorkingDirectory 'legacy-copy'
$data = Join-Path $WorkingDirectory 'task-server-data'
$logs = Join-Path $WorkingDirectory 'logs'
New-Item -ItemType Directory -Path $copy, $data, $logs -Force | Out-Null
Get-ChildItem -LiteralPath $legacy -Force | Copy-Item -Destination $copy -Recurse -Force

$previousListen = $env:LISTEN_URL
$previousStore = $env:STORE_PATH
$previousBackup = $env:BACKUP_PATH
$previousAuth = $env:AUTH
$process = $null
try {
    $env:LISTEN_URL = "http://127.0.0.1:$Port"
    $env:STORE_PATH = $data
    $env:BACKUP_PATH = (Join-Path $data 'backups')
    $env:AUTH = 'none'
    $process = Start-Process -FilePath $executable `
        -WorkingDirectory (Split-Path -Parent $executable) `
        -RedirectStandardOutput (Join-Path $logs 'stdout.log') `
        -RedirectStandardError (Join-Path $logs 'stderr.log') `
        -PassThru

    $baseUrl = "http://127.0.0.1:$Port"
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        try {
            $ready = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/readyz" -TimeoutSec 2
            if ($ready.StatusCode -eq 200) { break }
        } catch { }
        if ($process.HasExited) { throw "Dry-run Task Server exited with code $($process.ExitCode)." }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)
    if ([DateTime]::UtcNow -ge $deadline) { throw 'Dry-run Task Server readiness timed out.' }

    $headers = @{
        'X-Task-Protocol-Version' = '2'
        'X-Task-Client-Version' = 'planned-cutover-dry-run'
    }
    $request = @{
        legacyRoot = $copy
        workspaceName = $WorkspaceName
        freezeConfirmed = $true
        preserveEvidenceGit = $true
    }
    $inventory = Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/api/v1/management/migrations/legacy/inventory" `
        -Headers $headers -ContentType 'application/json' `
        -Body ($request | ConvertTo-Json)

    Invoke-RestMethod -Method Put `
        -Uri "$baseUrl/api/v1/management/mode" `
        -Headers $headers -ContentType 'application/json' `
        -Body (@{ mode = 'Maintenance'; reason = 'planned migration dry run against frozen copy' } | ConvertTo-Json) | Out-Null
    $request.expectedMigrationId = $inventory.migrationId
    $import = Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/api/v1/management/migrations/legacy/import" `
        -Headers $headers -ContentType 'application/json' `
        -Body ($request | ConvertTo-Json)

    $fields = 'projects', 'tasks', 'events', 'artifacts', 'runnerIdentities', 'runAttempts', 'activeLeases', 'reviewAttempts'
    foreach ($field in $fields) {
        if ($inventory.$field -ne $import.$field) {
            throw "Migration count mismatch for $field: inventory=$($inventory.$field), import=$($import.$field)."
        }
    }
    if ([string]::IsNullOrWhiteSpace($import.integritySha256)) {
        throw 'Migration import did not return an integrity digest.'
    }

    $report = [ordered]@{
        status = 'passed'
        generatedAt = [DateTime]::UtcNow.ToString('o')
        source = $legacy
        rehearsedCopy = $copy
        disposableStore = $data
        migrationId = $inventory.migrationId
        counts = [ordered]@{}
        integritySha256 = $import.integritySha256
        rollbackBoundary = $import.rollbackBoundary
        authorityDisposition = 'Active coding and review leases imported as process-unknown; positive containment is required before requeue.'
    }
    foreach ($field in $fields) { $report.counts[$field] = $import.$field }
    New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force | Out-Null
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8
    $report
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    $env:LISTEN_URL = $previousListen
    $env:STORE_PATH = $previousStore
    $env:BACKUP_PATH = $previousBackup
    $env:AUTH = $previousAuth
}
