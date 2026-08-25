[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SourceRoot,

    [Parameter(Mandatory)]
    [string] $LegacyRoot,

    [Parameter(Mandatory)]
    [string] $ResultsDirectory,

    [string] $DevspaceRoot = (Split-Path -Parent $SourceRoot),

    [ValidateRange(1025, 65535)]
    [int] $Port = 15071,

    [ValidateRange(10, 300)]
    [int] $ReadyTimeoutSeconds = 120
)

# Rehearses the exact first-start migration against a frozen copy. The live
# OrchestratorApi store remains untouched and the child PID is the only process
# this script owns or stops.

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$legacy = (Resolve-Path -LiteralPath $LegacyRoot).Path
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$rehearsal = Join-Path $DevspaceRoot ('.task-server-cutover-rehearsal-' + [Guid]::NewGuid().ToString('N'))
$frozen = Join-Path $rehearsal 'legacy-copy'
$package = Join-Path $rehearsal 'package'
$store = Join-Path $rehearsal 'store'
$stdout = Join-Path $rehearsal 'task-server.stdout.log'
$stderr = Join-Path $rehearsal 'task-server.stderr.log'
$reportPath = Join-Path $ResultsDirectory 'task-server-migration-dry-run.json'
$process = $null

try {
    New-Item -ItemType Directory -Path $frozen, $package, $store -Force | Out-Null
    robocopy $legacy $frozen /E /COPY:DAT /DCOPY:DAT /R:2 /W:1 /XJ | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "Frozen legacy copy failed with robocopy exit code $LASTEXITCODE." }

    dotnet publish (Join-Path $source 'task-server\TaskServer.csproj') -p:PublishProfile=win-x64 -o $package
    if ($LASTEXITCODE -ne 0) { throw "Task Server publish failed with exit code $LASTEXITCODE." }

    $authorityPath = Join-Path $frozen '.metadata\attempt-authority.json'
    if (-not (Test-Path -LiteralPath $authorityPath)) {
        throw "Legacy authority store not found in rehearsal copy: $authorityPath"
    }
    $legacyAuthority = Get-Content -LiteralPath $authorityPath -Raw | ConvertFrom-Json
    $legacyRuns = @($legacyAuthority.runAttempts)
    $legacyReviews = @($legacyAuthority.reviewAttempts)
    foreach ($archivePath in Get-ChildItem -LiteralPath (Split-Path -Parent $authorityPath) `
        -Filter 'attempt-authority.archive-*.json' -File -ErrorAction SilentlyContinue) {
        $archive = Get-Content -LiteralPath $archivePath.FullName -Raw | ConvertFrom-Json
        $legacyRuns += @($archive.runAttempts)
        $legacyReviews += @($archive.reviewAttempts)
    }
    $legacyRuns = @($legacyRuns | Sort-Object -Property attemptId -Unique)
    $legacyReviews = @($legacyReviews | Sort-Object -Property attemptId -Unique)
    $expectedRuns = $legacyRuns.Count
    $expectedReviews = $legacyReviews.Count
    $expectedLeases = @($legacyRuns | Where-Object { $null -ne $_.lease }).Count
    $expectedReports = @($legacyReviews | ForEach-Object { @($_.reports) }).Count
    $expectedFenceMaximum = @($legacyAuthority.lastFenceByTask.PSObject.Properties.Value |
        ForEach-Object { [long]$_ } | Measure-Object -Maximum).Maximum
    $expectedReviewFenceMaximum = @($legacyReviews | ForEach-Object { [long]$_.lastFence } |
        Measure-Object -Maximum).Maximum
    if ($null -eq $expectedFenceMaximum) { $expectedFenceMaximum = 0 }
    if ($null -eq $expectedReviewFenceMaximum) { $expectedReviewFenceMaximum = 0 }

    $saved = @{}
    foreach ($name in @(
        'LISTEN_URL', 'STORE_PATH', 'BACKUP_PATH', 'AUTH',
        'LEGACY_MIGRATION_ROOT', 'LEGACY_MIGRATION_WORKSPACE',
        'LEGACY_MIGRATION_FREEZE_CONFIRMED')) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    [Environment]::SetEnvironmentVariable('LISTEN_URL', "http://127.0.0.1:$Port", 'Process')
    [Environment]::SetEnvironmentVariable('STORE_PATH', $store, 'Process')
    [Environment]::SetEnvironmentVariable('BACKUP_PATH', (Join-Path $store 'backups'), 'Process')
    [Environment]::SetEnvironmentVariable('AUTH', 'none', 'Process')
    [Environment]::SetEnvironmentVariable('LEGACY_MIGRATION_ROOT', $frozen, 'Process')
    [Environment]::SetEnvironmentVariable('LEGACY_MIGRATION_WORKSPACE', 'Agent Studio', 'Process')
    [Environment]::SetEnvironmentVariable('LEGACY_MIGRATION_FREEZE_CONFIRMED', 'true', 'Process')
    try {
        $process = Start-Process -FilePath (Join-Path $package 'task-server.exe') `
            -WorkingDirectory $package `
            -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr `
            -PassThru
    }
    finally {
        foreach ($name in $saved.Keys) {
            [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
        }
    }

    $origin = "http://127.0.0.1:$Port"
    $deadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
    do {
        if ($process.HasExited) {
            throw "Dry-run Task Server exited with code $($process.ExitCode): $(Get-Content -LiteralPath $stderr -Raw)"
        }
        try {
            $ready = Invoke-WebRequest -UseBasicParsing -Uri "$origin/readyz" -TimeoutSec 3
            if ($ready.StatusCode -eq 200) { break }
        }
        catch { Start-Sleep -Milliseconds 500 }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ([DateTime]::UtcNow -ge $deadline) { throw "Dry-run Task Server did not become ready." }

    $headers = @{ 'X-Task-Protocol-Version' = '1' }
    $status = Invoke-RestMethod -Uri "$origin/api/v1/management/status" -Headers $headers
    $migration = Invoke-RestMethod -Uri "$origin/api/v1/management/migrations/legacy/status" -Headers $headers
    $projects = @(Invoke-RestMethod -Uri "$origin/api/v1/projects" -Headers $headers)
    $tasks = @()
    $histories = @()
    foreach ($project in $projects) {
        $projectTasks = @(Invoke-RestMethod -Uri "$origin/api/v1/projects/$($project.projectId)/tasks" -Headers $headers)
        $tasks += $projectTasks
        foreach ($task in $projectTasks) {
            $histories += Invoke-RestMethod `
                -Uri "$origin/api/v1/projects/$($project.projectId)/tasks/$($task.taskKey)/history" `
                -Headers $headers
        }
    }
    $actualRuns = @($histories | ForEach-Object { @($_.runs) }).Count
    if ($actualRuns -ne $expectedRuns `
        -or $migration.runs -ne $expectedRuns `
        -or $migration.leases -ne $expectedLeases `
        -or $migration.reviews -ne $expectedReviews `
        -or $migration.reports -ne $expectedReports `
        -or $migration.maximumTaskFence -ne $expectedFenceMaximum `
        -or $migration.maximumReviewFence -ne $expectedReviewFenceMaximum) {
        throw "Authority mismatch: runs $actualRuns/$expectedRuns, leases $($migration.leases)/$expectedLeases, reviews $($migration.reviews)/$expectedReviews, reports $($migration.reports)/$expectedReports, maximum fences $($migration.maximumTaskFence)/$expectedFenceMaximum and $($migration.maximumReviewFence)/$expectedReviewFenceMaximum."
    }

    $database = Join-Path $store 'task-server.db'
    $report = [ordered]@{
        outcome = 'passed'
        executedAt = [DateTime]::UtcNow.ToString('o')
        sourceRoot = $legacy
        rehearsalCopy = $frozen
        serverId = $status.serverId
        schemaVersion = $status.schemaVersion
        ready = $status.authorityReady
        projects = $projects.Count
        tasks = $tasks.Count
        runs = $actualRuns
        leasesImported = $migration.leases
        reviewsExpected = $expectedReviews
        reviewsImported = $migration.reviews
        reportsImported = $migration.reports
        migrationId = $migration.migrationId
        legacyAuthorityEpoch = $migration.authorityEpoch
        legacyMaximumFence = $migration.maximumTaskFence
        databaseSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $database).Hash.ToLowerInvariant()
        sourceUntouched = $true
        authorityForked = $false
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output "task-server-migration-dry-run passed report=$reportPath"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    if (Test-Path -LiteralPath $rehearsal) {
        Remove-Item -LiteralPath $rehearsal -Recurse -Force
    }
}
