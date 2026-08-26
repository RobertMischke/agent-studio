[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $LegacyRoot,
    [string] $TaskServerExecutable = 'C:\AgentOrchestrator\current\task-server.exe',
    [string] $ScratchRoot = (Join-Path $env:TEMP 'AgentOrchestrator\migration-rehearsal'),
    [string] $WorkspaceName = 'Agent Studio',
    [string] $ReportPath
)

$ErrorActionPreference = 'Stop'
$runId = '{0:yyyyMMdd-HHmmssZ}' -f [DateTime]::UtcNow
$runRoot = Join-Path $ScratchRoot $runId
$sourceCopy = Join-Path $runRoot 'legacy-copy'
$store = Join-Path $runRoot 'task-server-store'
$backups = Join-Path $store 'backups'
$stdout = Join-Path $runRoot 'task-server.stdout.log'
$stderr = Join-Path $runRoot 'task-server.stderr.log'
$baseUrl = 'http://127.0.0.1:15071'
New-Item -ItemType Directory -Force -Path $sourceCopy, $store, $backups | Out-Null

& robocopy $LegacyRoot $sourceCopy /MIR /COPY:DAT /DCOPY:DAT /R:2 /W:1 /XJ /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Legacy source copy failed with robocopy exit code $LASTEXITCODE." }

$start = [Diagnostics.ProcessStartInfo]::new($TaskServerExecutable)
$start.UseShellExecute = $false
$start.Environment['LISTEN_URL'] = $baseUrl
$start.Environment['STORE_PATH'] = $store
$start.Environment['BACKUP_PATH'] = $backups
$start.Environment['AUTH'] = 'none'
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()

try {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        try { Invoke-WebRequest "$baseUrl/readyz" -UseBasicParsing -TimeoutSec 2 | Out-Null; break }
        catch { Start-Sleep -Seconds 1 }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ([DateTime]::UtcNow -ge $deadline) { throw 'Rehearsal Task Server did not become ready.' }

    $headers = @{
        'X-Task-Protocol-Version' = '2'
        'X-Client-Version' = 'cutover-rehearsal'
        'X-Actor-Id' = 'planned-cutover'
    }
    $request = @{ legacyRoot = $sourceCopy; workspaceName = $WorkspaceName; freezeConfirmed = $true; preserveEvidenceGit = $true }
    $inventory = Invoke-RestMethod "$baseUrl/api/v1/management/migrations/legacy/inventory" `
        -Method Post -Headers $headers -ContentType 'application/json' -Body ($request | ConvertTo-Json)
    Invoke-RestMethod "$baseUrl/api/v1/management/mode" -Method Put -Headers $headers `
        -ContentType 'application/json' -Body (@{ mode = 'maintenance'; reason = 'copy-only migration rehearsal' } | ConvertTo-Json) | Out-Null
    $request.expectedMigrationId = $inventory.migrationId
    $result = Invoke-RestMethod "$baseUrl/api/v1/management/migrations/legacy/import" `
        -Method Post -Headers $headers -ContentType 'application/json' -Body ($request | ConvertTo-Json)

    if ($inventory.tasks -ne $result.tasks -or
        $inventory.runnerIdentities -ne $result.runnerIdentities -or
        $inventory.runAttempts -ne $result.runAttempts -or
        $inventory.codingLeases -ne $result.codingLeases -or
        $inventory.reviewAttempts -ne $result.reviewAttempts -or
        $inventory.reviewLeases -ne $result.reviewLeases -or
        $inventory.sourceAuthorityEpoch -ne $result.sourceAuthorityEpoch) {
        throw 'Migration rehearsal count comparison failed.'
    }
    $report = [ordered]@{
        rehearsedAtUtc = [DateTime]::UtcNow.ToString('o')
        source = (Resolve-Path $LegacyRoot).Path
        sourceCopy = $sourceCopy
        migrationId = $result.migrationId
        inventory = $inventory
        import = $result
        sourceWasModified = $false
    }
    $json = $report | ConvertTo-Json -Depth 20
    if ($ReportPath) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ReportPath) | Out-Null
        [IO.File]::WriteAllText($ReportPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    }
    $json
}
finally {
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    [IO.File]::WriteAllText($stdout, $stdoutTask.GetAwaiter().GetResult())
    [IO.File]::WriteAllText($stderr, $stderrTask.GetAwaiter().GetResult())
}
