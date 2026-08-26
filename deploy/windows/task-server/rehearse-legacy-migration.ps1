[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TaskServerExecutable,
    [Parameter(Mandatory)] [string] $LegacyRoot,
    [Parameter(Mandatory)] [string] $WorkspaceName,
    [Parameter(Mandatory)] [string] $EvidenceFile,
    [string] $ListenUrl = 'http://127.0.0.1:5072',
    [ValidateRange(10, 300)] [int] $ReadyTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $TaskServerExecutable).Path
$legacy = (Resolve-Path -LiteralPath $LegacyRoot).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) "task-server-migration-rehearsal-$([Guid]::NewGuid().ToString('N'))"
$legacyCopy = Join-Path $scratch 'legacy-copy'
$data = Join-Path $scratch 'task-server-data'
$stdout = Join-Path $scratch 'task-server.stdout.log'
$stderr = Join-Path $scratch 'task-server.stderr.log'
$headers = @{
    'X-Task-Protocol-Version' = '1'
    'X-Actor-Id' = 'planned-cutover-rehearsal'
}
$process = $null

$identityFiles = @(Get-ChildItem -LiteralPath (Join-Path $legacy 'identities') -Filter '*.json' -File -ErrorAction SilentlyContinue)
$authorityPath = Join-Path $legacy '.metadata\attempt-authority.json'
$legacyAuthority = if (Test-Path -LiteralPath $authorityPath) {
    Get-Content -LiteralPath $authorityPath -Raw | ConvertFrom-Json
} else {
    $null
}
$runAttemptCount = if ($null -eq $legacyAuthority) { 0 } else { @($legacyAuthority.runAttempts).Count }
$reviewAttemptCount = if ($null -eq $legacyAuthority) { 0 } else { @($legacyAuthority.reviewAttempts).Count }
$fenceCount = if ($null -eq $legacyAuthority -or $null -eq $legacyAuthority.lastFenceByTask) {
    0
} else {
    @($legacyAuthority.lastFenceByTask.PSObject.Properties).Count
}
if ($identityFiles.Count -gt 0 -or $runAttemptCount -gt 0 -or $reviewAttemptCount -gt 0 -or $fenceCount -gt 0) {
    $evidenceDirectory = Split-Path -Parent $EvidenceFile
    if ($evidenceDirectory) { New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null }
    [pscustomobject]@{
        executedAtUtc = [DateTime]::UtcNow.ToString('o')
        status = 'blocked'
        reason = 'legacy-authority-import-not-implemented'
        identities = $identityFiles.Count
        runAttempts = $runAttemptCount
        reviewAttempts = $reviewAttemptCount
        fencedTasks = $fenceCount
        authorityPath = $authorityPath
    } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $EvidenceFile -Encoding utf8
    throw "LegacyMigrationService does not yet import identity, lease, fence, and attempt authority. Refusing a rehearsal that would prove only task content and fork authority. Evidence: $EvidenceFile"
}

function Invoke-TaskServer {
    param(
        [Parameter(Mandatory)] [string] $Method,
        [Parameter(Mandatory)] [string] $Path,
        [object] $Body
    )
    $parameters = @{
        Method = $Method
        Uri = "$($ListenUrl.TrimEnd('/'))$Path"
        Headers = $headers
        TimeoutSec = 30
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Depth 20
    }
    Invoke-RestMethod @parameters
}

function Wait-TaskServerReady {
    $deadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
    $lastFailure = 'no response'
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -ne $process -and $process.HasExited) {
            throw "Rehearsal Task Server exited with code $($process.ExitCode). See $stderr"
        }
        try {
            $response = Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 -Uri "$($ListenUrl.TrimEnd('/'))/readyz"
            if ($response.StatusCode -eq 200) { return }
            $lastFailure = "HTTP $($response.StatusCode)"
        } catch {
            $lastFailure = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Rehearsal Task Server did not become ready: $lastFailure"
}

try {
    New-Item -ItemType Directory -Force -Path $scratch, $legacyCopy, $data | Out-Null
    & robocopy $legacy $legacyCopy /MIR /COPY:DAT /DCOPY:DAT /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "robocopy failed with exit code $LASTEXITCODE." }

    $env:LISTEN_URL = $ListenUrl
    $env:STORE_PATH = $data
    $env:BACKUP_PATH = (Join-Path $data 'backups')
    $env:AUTH = 'none'
    Remove-Item Env:AUTH_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:AUTH_TOKEN_FILE -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    Wait-TaskServerReady

    $null = Invoke-TaskServer PUT '/api/v1/management/mode' @{
        mode = 'Maintenance'
        reason = 'planned migration rehearsal against an isolated source copy'
    }
    $request = @{
        legacyRoot = $legacyCopy
        workspaceName = $WorkspaceName
        freezeConfirmed = $true
        preserveEvidenceGit = $true
    }
    $inventory = Invoke-TaskServer POST '/api/v1/management/migrations/legacy/inventory' $request
    $request.expectedMigrationId = $inventory.migrationId
    $result = Invoke-TaskServer POST '/api/v1/management/migrations/legacy/import' $request
    $status = Invoke-TaskServer GET '/api/v1/management/status' $null
    $invariants = Invoke-TaskServer GET '/api/v1/management/invariants' $null

    foreach ($field in @('projects', 'tasks', 'events', 'artifacts')) {
        if ($inventory.$field -ne $result.$field) {
            throw "Migration count mismatch for $field: inventory=$($inventory.$field), import=$($result.$field)."
        }
    }
    if (-not $result.imported -or [string]::IsNullOrWhiteSpace($result.integritySha256)) {
        throw 'LegacyMigrationService did not return an imported result with an integrity digest.'
    }
    $backupFiles = @(Get-ChildItem -LiteralPath (Join-Path $data 'backups') -Filter '*.db' -File)
    if ($backupFiles.Count -eq 0) {
        throw 'LegacyMigrationService did not create the required pre-import backup.'
    }
    if (@($invariants.recentViolations).Count -ne 0) {
        throw 'The rehearsal completed with Task Server invariant violations.'
    }

    $evidenceDirectory = Split-Path -Parent $EvidenceFile
    if ($evidenceDirectory) { New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null }
    [pscustomobject]@{
        executedAtUtc = [DateTime]::UtcNow.ToString('o')
        source = $legacy
        isolatedCopy = $legacyCopy
        executable = $executable
        inventory = $inventory
        import = $result
        status = $status
        invariants = $invariants
        preImportBackups = @($backupFiles.FullName)
        sourceWasNeverPassedToTaskServer = $true
        rehearsalStore = $data
        stdout = $stdout
        stderr = $stderr
    } | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $EvidenceFile -Encoding utf8
    Write-Host "Legacy migration rehearsal passed. Evidence: $EvidenceFile"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
}
