[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [ValidateRange(1, 65535)]
    [int] $OrchestratorPort = 5031,

    [ValidateRange(1, 60)]
    [int] $IntervalMinutes = 5,

    [ValidateRange(10, 3600)]
    [int] $ProbeIntervalSeconds = 60,

    [ValidateRange(1, 20)]
    [int] $FailureThreshold = 2,

    [string] $KeeperTaskName = 'AgentRunner-TunnelKeeper',

    [string] $WatchdogTaskName = 'AgentRunner-TunnelWatchdog',

    [string] $DevspacePath = (Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) -Parent),

    [string] $OperatorAlarmPath,

    [string] $BashExecutable = 'C:\Program Files\Git\bin\bash.exe',

    [string] $StatusPath = (Join-Path $env:LOCALAPPDATA 'AgentTaskboard\tunnel-keeper\supervision-status.json'),

    # Internal: where the elevated child's console output is captured, since
    # its own window closes before an operator can read it.
    [string] $TranscriptPath,

    # Only report current registered / running / last-heal state; skip
    # elevation and registration entirely. Never needs admin: reading a
    # Scheduled Task's state and a status file are both unprivileged.
    [switch] $StatusOnly,

    # Skip the interactive "type yes to continue" gate before requesting
    # elevation. The OS-level UAC consent dialog still appears; this only
    # removes the product's own confirmation step, for unattended re-runs on
    # a host that already has an operator-approved install.
    [switch] $Force,

    # Internal: set by the self-relaunch so the elevated child does not
    # re-print the explanation or re-prompt.
    [switch] $Elevated
)

$ErrorActionPreference = 'Stop'

function Test-RunningElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-ElevatedRegistration {
    Write-Host ''
    Write-Host 'Registering the tunnel keeper and watchdog needs a one-time administrator' -ForegroundColor Yellow
    Write-Host 'elevation to create Scheduled Tasks under an S4U principal. After this run,' -ForegroundColor Yellow
    Write-Host 'both tasks execute unattended without further elevation, and this script' -ForegroundColor Yellow
    Write-Host 'can report their status (-StatusOnly) as the signed-in user at any time.' -ForegroundColor Yellow
    Write-Host ''
    if (-not $Force) {
        $answer = Read-Host 'Continue and request administrator elevation now? [y/N]'
        if ($answer -notmatch '^(y|yes)$') {
            Write-Host 'Elevation declined. No Scheduled Task was registered or changed.'
            exit 1
        }
    }

    # Start-Process -Verb RunAs opens the elevated child in its own console
    # that closes the instant the script exits, taking every Write-Host /
    # Write-Warning line with it - and -Verb RunAs is incompatible with
    # -RedirectStandardOutput/-Error (ShellExecute cannot be combined with
    # stream redirection). A transcript file survives the closed window, so a
    # failed registration is diagnosable from the unprivileged side instead
    # of just "it exited non-zero".
    $childTranscriptPath = Join-Path (Split-Path -Parent $StatusPath) 'elevated-registration.log'
    $scriptPath = $PSCommandPath
    $forwardedArgs = @(
        '-SshTarget', $SshTarget,
        '-RemotePort', $RemotePort,
        '-OrchestratorPort', $OrchestratorPort,
        '-IntervalMinutes', $IntervalMinutes,
        '-ProbeIntervalSeconds', $ProbeIntervalSeconds,
        '-FailureThreshold', $FailureThreshold,
        '-KeeperTaskName', $KeeperTaskName,
        '-WatchdogTaskName', $WatchdogTaskName,
        '-DevspacePath', $DevspacePath,
        '-BashExecutable', $BashExecutable,
        '-StatusPath', $StatusPath,
        '-TranscriptPath', $childTranscriptPath,
        '-Elevated'
    )
    if ($OperatorAlarmPath) { $forwardedArgs += @('-OperatorAlarmPath', $OperatorAlarmPath) }
    $quoted = ($forwardedArgs | ForEach-Object { if ($_ -match '\s') { '"{0}"' -f $_ } else { $_ } }) -join ' '
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $scriptPath), $quoted
    ) -join ' '

    Write-Host 'Requesting elevation (a UAC prompt will appear)...'
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Elevated registration exited with code $($process.ExitCode). Details: $childTranscriptPath"
    }
}

function Read-JsonFile {
    param([Parameter(Mandatory)] [string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch { return $null }
}

function Get-TunnelSupervisionStatus {
    $keeperTask = Get-ScheduledTask -TaskName $KeeperTaskName -ErrorAction SilentlyContinue
    $watchdogTask = Get-ScheduledTask -TaskName $WatchdogTaskName -ErrorAction SilentlyContinue
    $keeperState = Read-JsonFile (Join-Path $env:LOCALAPPDATA 'AgentTaskboard\tunnel-keeper\state.json')

    $devspace = $null
    try { $devspace = (Resolve-Path -LiteralPath $DevspacePath -ErrorAction Stop).Path } catch { $devspace = $DevspacePath }
    $watchdogStatus = Read-JsonFile (Join-Path $devspace '.tunnel-watchdog-state\status.json')

    [ordered]@{
        schemaVersion = 1
        generatedAt   = [DateTime]::UtcNow.ToString('o')
        keeper        = [ordered]@{
            taskName     = $KeeperTaskName
            registered   = [bool] $keeperTask
            state        = if ($keeperTask) { $keeperTask.State.ToString() } else { $null }
            lastStatus   = if ($keeperState) { $keeperState.status } else { $null }
            lastObservedAt = if ($keeperState) { $keeperState.observedAt } else { $null }
            lastMessage  = if ($keeperState) { $keeperState.message } else { $null }
        }
        watchdog      = [ordered]@{
            taskName                = $WatchdogTaskName
            registered               = [bool] $watchdogTask
            state                    = if ($watchdogTask) { $watchdogTask.State.ToString() } else { $null }
            lastProbeAt              = if ($watchdogStatus) { $watchdogStatus.lastProbeAt } else { $null }
            lastProbeResult          = if ($watchdogStatus) { $watchdogStatus.lastProbeResult } else { $null }
            lastHealAt               = if ($watchdogStatus) { $watchdogStatus.lastHealAt } else { $null }
            lastHealResult           = if ($watchdogStatus) { $watchdogStatus.lastHealResult } else { $null }
            consecutiveProbeFailures = if ($watchdogStatus) { $watchdogStatus.consecutiveProbeFailures } else { $null }
        }
    }
}

function Write-SupervisionStatus {
    param([Parameter(Mandatory)] $Status)
    $directory = Split-Path -Parent $StatusPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporary = "$StatusPath.tmp"
    ($Status | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $StatusPath -Force
}

function Show-SupervisionStatus {
    param([Parameter(Mandatory)] $Status)
    Write-Host ''
    Write-Host 'Tunnel supervision status' -ForegroundColor Cyan
    Write-Host ('  keeper   : registered={0} state={1} lastStatus={2} lastObservedAt={3}' -f `
        $Status.keeper.registered, $Status.keeper.state, $Status.keeper.lastStatus, $Status.keeper.lastObservedAt)
    Write-Host ('  watchdog : registered={0} state={1} lastProbe={2} ({3}) lastHeal={4} ({5})' -f `
        $Status.watchdog.registered, $Status.watchdog.state, $Status.watchdog.lastProbeAt, $Status.watchdog.lastProbeResult, `
        $Status.watchdog.lastHealAt, $Status.watchdog.lastHealResult)
    Write-Host "  snapshot : $StatusPath"
    Write-Host ''
}

$needsElevation = -not $StatusOnly -and -not $Elevated -and -not (Test-RunningElevated)

if ($needsElevation) {
    Invoke-ElevatedRegistration
}
elseif (-not $StatusOnly) {
    # Only the elevated relaunch carries a TranscriptPath; a direct call
    # (already elevated, or an operator running this from an admin prompt)
    # has its own visible console and needs no capture.
    $capturing = $Elevated -and $TranscriptPath
    if ($capturing) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $TranscriptPath) -Force | Out-Null
        Start-Transcript -Path $TranscriptPath -Force | Out-Null
    }
    try {
        if ($PSCmdlet.ShouldProcess('AgentRunner tunnel keeper and watchdog', 'Register or update Scheduled Tasks')) {
            & (Join-Path $PSScriptRoot 'register-tunnel-keeper.ps1') `
                -SshTarget $SshTarget `
                -RemotePort $RemotePort `
                -OrchestratorPort $OrchestratorPort `
                -IntervalMinutes $IntervalMinutes `
                -TaskName $KeeperTaskName

            $watchdogArgs = @{
                SshTarget            = $SshTarget
                RemotePort           = $RemotePort
                KeeperTaskName       = $KeeperTaskName
                ProbeIntervalSeconds = $ProbeIntervalSeconds
                FailureThreshold     = $FailureThreshold
                TaskName             = $WatchdogTaskName
                DevspacePath         = $DevspacePath
                BashExecutable       = $BashExecutable
            }
            if ($OperatorAlarmPath) { $watchdogArgs['OperatorAlarmPath'] = $OperatorAlarmPath }
            & (Join-Path $PSScriptRoot 'register-tunnel-watchdog.ps1') @watchdogArgs
        }
    }
    finally {
        if ($capturing) { Stop-Transcript | Out-Null }
    }
}

# Single call site: an elevated relaunch reports its own result before its
# window closes, and the unprivileged parent (or a direct/-StatusOnly call)
# reports the same way afterward - one report path instead of two copies.
$status = Get-TunnelSupervisionStatus
Write-SupervisionStatus -Status $status
Show-SupervisionStatus -Status $status
