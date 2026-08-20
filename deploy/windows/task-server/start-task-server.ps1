<#
.SYNOPSIS
Starts the Task Server as a detached background process on Windows.

.DESCRIPTION
This is the action of the AgentOrchestrator-TaskServer scheduled task and the
Windows counterpart of the agent-task-server systemd unit. It loads the
host-owned server.env bootstrap contract, starts the published executable
detached so the scheduled task action can complete, and waits a bounded time
for /readyz.

The script never stops a running Task Server. A live process owns durable lease
and fence authority, so recovery from a hung process is an operator decision
documented in docs/operations/setup/task-server.md, not an automatic one. When
the process is already running, the script only records its observed state.

.NOTES
Exit code 0 means a Task Server from this installation is running. Exit code 4
means it is not running, or could not be observed, and this attempt did not
repair it. Task Scheduler reports the code as LastTaskResult.
#>
[CmdletBinding()]
param(
    [string] $ExecutablePath = 'C:\Program Files\AgentOrchestrator\current\task-server.exe',

    [string] $EnvironmentFile = (Join-Path $env:ProgramData 'AgentOrchestrator\server.env'),

    [string] $StateDirectory = (Join-Path $env:ProgramData 'AgentOrchestrator\task-server'),

    [ValidateRange(10, 600)]
    [int] $ReadyWaitSeconds = 120,

    [ValidateRange(1, 3650)]
    [int] $LogRetentionDays = 30
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'task-server-common.ps1')

$statePath = Join-Path $StateDirectory 'state.json'
$logPath = Join-Path $StateDirectory 'events.log'
$mutex = [Threading.Mutex]::new($false, 'Local\AgentOrchestratorTaskServerStarter')
$ownsMutex = $false

function Read-ServiceState {
    if (-not (Test-Path -LiteralPath $statePath)) { return $null }
    try { return Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json }
    catch { return $null }
}

function Write-ServiceState {
    param(
        [Parameter(Mandatory)] [string] $Status,
        [Parameter(Mandatory)] [string] $Message,
        [int] $ProcessId = 0,
        [string] $ProcessPath = '',
        [string] $ProcessStartedAt = '',
        [string] $ReadyUrl = '',
        [string] $HealthUrl = ''
    )

    $now = [DateTime]::UtcNow
    $previous = Read-ServiceState
    $lastLoggedAt = if ($previous -and $previous.lastLoggedAt) {
        [DateTime]::Parse($previous.lastLoggedAt).ToUniversalTime()
    } else { [DateTime]::MinValue }
    $shouldLog = -not $previous -or $previous.status -ne $Status -or
        ($now - $lastLoggedAt) -ge [TimeSpan]::FromHours(1)
    if ($shouldLog) {
        Write-TaskServerEvent -LogPath $logPath -Event 'state' -Data @{
            status = $Status
            processId = $ProcessId
            executable = $ExecutablePath
            message = $Message
        }
        $lastLoggedAt = $now
    }

    $state = [ordered]@{
        status = $Status
        observedAt = $now.ToString('o')
        lastLoggedAt = $lastLoggedAt.ToString('o')
        processId = $ProcessId
        processPath = $ProcessPath
        processStartedAt = $ProcessStartedAt
        executablePath = $ExecutablePath
        environmentFile = $EnvironmentFile
        healthUrl = $HealthUrl
        readyUrl = $ReadyUrl
        message = $Message
    }
    $temporary = "$statePath.tmp"
    $state | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $statePath -Force
}

try {
    try {
        $ownsMutex = $mutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        # The previous starter was killed while holding the mutex, for example
        # by Stop-ScheduledTask during an upgrade. The wait still succeeded.
        $ownsMutex = $true
    }
    if (-not $ownsMutex) { exit 0 }

    New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    Remove-ExpiredWrapperFile -Directory $StateDirectory `
        -Pattern @('task-server-*.stdout.log', 'task-server-*.stderr.log') `
        -RetentionDays $LogRetentionDays
    $executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
    $ExecutablePath = $executable
    $loadedNames = Import-ServerEnvironmentFile -Path $EnvironmentFile
    Write-TaskServerEvent -LogPath $logPath -Event 'environment_loaded' -Data @{
        file = $EnvironmentFile
        keys = ($loadedNames -join ',')
    }

    $probeUri = Get-TaskServerProbeUri
    $healthUrl = [Uri]::new($probeUri, '/healthz').AbsoluteUri
    $readyUrl = [Uri]::new($probeUri, '/readyz').AbsoluteUri

    $observation = Get-TaskServerObservation -ExecutablePath $executable
    if ($observation.Process) {
        $status = if (Test-TaskServerEndpoint -Url $readyUrl) { 'ready' } else { 'running-not-ready' }
        $message = if ($status -eq 'ready') {
            'Task Server was already running and answered /readyz.'
        } else {
            'Task Server was already running but did not answer /readyz. This script never stops a live durable authority; see the upgrade and recovery procedure in the runbook.'
        }
        Write-ServiceState -Status $status -Message $message `
            -ProcessId $observation.ProcessId -ProcessPath $observation.ExecutablePath `
            -ProcessStartedAt $observation.StartedAt `
            -ReadyUrl $readyUrl -HealthUrl $healthUrl
        exit 0
    }

    if ($observation.UnidentifiedCount -gt 0) {
        # Never resolve "cannot tell" as "not running": a second writer against
        # one SQLite store would break the single-owner contract.
        Write-ServiceState -Status 'unknown' `
            -Message "$($observation.UnidentifiedCount) task-server process(es) are running whose image path this identity cannot read, so no start was attempted. Run the wrapper as the service identity, or stop the foreign process." `
            -ReadyUrl $readyUrl -HealthUrl $healthUrl
        exit 4
    }

    $workingDirectory = if ($env:STORE_PATH) { $env:STORE_PATH } else { Split-Path -Parent $executable }
    New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
    $attemptId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $stdoutPath = Join-Path $StateDirectory "task-server-$attemptId.stdout.log"
    $stderrPath = Join-Path $StateDirectory "task-server-$attemptId.stderr.log"
    # -NoNewWindow, not -WindowStyle Hidden: -WindowStyle belongs to the
    # UseShellExecute parameter set in Windows PowerShell 5.1 and cannot be
    # combined with output redirection. Redirection already implies
    # UseShellExecute=false, and -NoNewWindow suppresses the console window.
    $process = Start-Process `
        -FilePath $executable `
        -WorkingDirectory $workingDirectory `
        -NoNewWindow `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru
    Write-TaskServerEvent -LogPath $logPath -Event 'process_started' -Data @{
        processId = $process.Id
        workingDirectory = $workingDirectory
        stdout = $stdoutPath
        stderr = $stderrPath
    }

    $startedAt = ''
    $processPath = $executable
    try {
        $startedAt = $process.StartTime.ToUniversalTime().ToString('o')
        if ($process.Path) { $processPath = $process.Path }
    } catch { }
    $deadline = [DateTime]::UtcNow.AddSeconds($ReadyWaitSeconds)
    do {
        Start-Sleep -Seconds 3
        if ($process.HasExited) {
            Write-TaskServerEvent -LogPath $logPath -Event 'process_exited' -Data @{
                processId = $process.Id
                exitCode = $process.ExitCode
            }
            Write-ServiceState -Status 'stopped' `
                -Message "Task Server exited with code $($process.ExitCode) before answering /readyz; captured output is beside state.json." `
                -ReadyUrl $readyUrl -HealthUrl $healthUrl
            exit 4
        }
        if (Test-TaskServerEndpoint -Url $readyUrl) {
            Write-ServiceState -Status 'ready' `
                -Message 'Task Server started and answered /readyz.' `
                -ProcessId $process.Id -ProcessPath $processPath -ProcessStartedAt $startedAt `
                -ReadyUrl $readyUrl -HealthUrl $healthUrl
            exit 0
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    $live = Test-TaskServerEndpoint -Url $healthUrl
    $message = if ($live) {
        'Task Server is live but did not answer /readyz before the deadline; schema migration or lease recovery may still be running.'
    } else {
        'Task Server did not answer /healthz or /readyz before the deadline; captured output is beside state.json.'
    }
    Write-ServiceState -Status 'running-not-ready' -Message $message `
        -ProcessId $process.Id -ProcessPath $processPath -ProcessStartedAt $startedAt `
        -ReadyUrl $readyUrl -HealthUrl $healthUrl
    exit 4
}
catch {
    if (-not (Test-Path -LiteralPath $StateDirectory)) {
        New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    }
    Write-ServiceState -Status 'unknown' -Message $_.Exception.Message
    exit 4
}
finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
