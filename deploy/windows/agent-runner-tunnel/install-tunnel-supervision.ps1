<#
.SYNOPSIS
    Guided, self-elevating entry point for the Windows control-plane host
    setup: registers the tunnel keeper and its watchdog in one step.

.DESCRIPTION
    Registering an at-startup Scheduled Task needs an elevated session once
    (see register-tunnel-keeper.ps1 / register-tunnel-watchdog.ps1). This
    script is the one command the guided host-setup flow tells an operator to
    run: it explains why elevation is needed, requests it through the normal
    Windows UAC consent prompt if the current session is not already
    elevated, then registers both Scheduled Tasks. Querying their status
    afterwards (the Execution Hosts admin panel, or `schtasks /Query`) needs
    no elevation.

    Re-running this script is safe: both underlying registrations are
    idempotent (`Register-ScheduledTask -Force`).

.PARAMETER SshTarget
    SSH alias for the runner host (default: agent-runner).
.PARAMETER RemotePort
    Runner-side reverse-forward port (default: 15031).
.PARAMETER TaskServerPort
    Local Task Server port the tunnel exposes on the runner host (default: 5031).
.PARAMETER DevspacePath
    Parent directory of the dev and stable checkouts. Also where the watchdog
    journal (.tunnel-watchdog.log) and operator alarm channel live. Defaults
    to the parent of this checkout, matching the scripts' own auto-detection.
.PARAMETER KeeperTaskName
    Scheduled Task name for the keeper (default: AgentRunner-TunnelKeeper).
.PARAMETER WatchdogTaskName
    Scheduled Task name for the watchdog (default: AgentRunner-TunnelWatchdog).
.PARAMETER IntervalMinutes
    Keeper's fallback repeating trigger interval (default: 5).
.PARAMETER ProbeIntervalSeconds
    Watchdog's functional probe cadence (default: 60).
.PARAMETER FailureThreshold
    Consecutive probe failures before the watchdog heals (default: 2).
.PARAMETER OperatorAlarmPath
    Append-only operator alarm channel (default: <DevspacePath>/.operator-alarm.log).
.PARAMETER BashExecutable
    Bash used to run tunnel-watchdog.sh (default: Git for Windows bash.exe).
.PARAMETER SkipElevationCheck
    Test-only escape hatch: assume the current session is already elevated
    instead of probing WindowsPrincipal. Never set this outside a test.

.EXAMPLE
    .\install-tunnel-supervision.ps1 -SshTarget agent-runner -RemotePort 15031
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [ValidateRange(1, 65535)]
    [int] $TaskServerPort = 5031,

    [string] $DevspacePath = (Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) -Parent),

    [string] $KeeperTaskName = 'AgentRunner-TunnelKeeper',

    [string] $WatchdogTaskName = 'AgentRunner-TunnelWatchdog',

    [ValidateRange(1, 60)]
    [int] $IntervalMinutes = 5,

    [ValidateRange(10, 3600)]
    [int] $ProbeIntervalSeconds = 60,

    [ValidateRange(1, 20)]
    [int] $FailureThreshold = 2,

    [string] $OperatorAlarmPath,

    [string] $BashExecutable = 'C:\Program Files\Git\bin\bash.exe',

    [switch] $SkipElevationCheck
)

$ErrorActionPreference = 'Stop'

function Test-Elevated {
    if ($SkipElevationCheck) { return $true }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) {
    Write-Host @"
Tunnel supervision setup needs an elevated session once.

Both Scheduled Tasks start at boot with an S4U principal so they keep the
tunnel alive without an interactive logon session. Windows only allows an
at-startup Scheduled Task to be *registered* from an elevated session; it
runs afterwards without one, and this script never asks for elevation again
once both tasks exist.

Requesting elevation now. Windows will show the standard UAC consent prompt;
approve it to continue, or cancel to stop here without changing anything.
"@

    $scriptPath = $PSCommandPath
    $forwardedArgs = @(
        '-SshTarget', $SshTarget,
        '-RemotePort', $RemotePort,
        '-TaskServerPort', $TaskServerPort,
        '-DevspacePath', $DevspacePath,
        '-KeeperTaskName', $KeeperTaskName,
        '-WatchdogTaskName', $WatchdogTaskName,
        '-IntervalMinutes', $IntervalMinutes,
        '-ProbeIntervalSeconds', $ProbeIntervalSeconds,
        '-FailureThreshold', $FailureThreshold,
        '-BashExecutable', $BashExecutable
    )
    if ($OperatorAlarmPath) { $forwardedArgs += @('-OperatorAlarmPath', $OperatorAlarmPath) }
    if ($WhatIfPreference) { $forwardedArgs += '-WhatIf' }

    $quotedArgs = ($forwardedArgs | ForEach-Object { '"{0}"' -f ($_ -replace '"', '\"') }) -join ' '
    $relaunch = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" $quotedArgs"

    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $relaunch -Wait -PassThru
    if ($elevated.ExitCode -ne 0) {
        Write-Error "Elevated setup did not complete successfully (exit code $($elevated.ExitCode))."
    }
    exit $elevated.ExitCode
}

if (-not $OperatorAlarmPath) {
    $OperatorAlarmPath = Join-Path $DevspacePath '.operator-alarm.log'
}

if ($PSCmdlet.ShouldProcess('AgentRunner-TunnelKeeper / AgentRunner-TunnelWatchdog', 'Register tunnel supervision Scheduled Tasks')) {
    Write-Host "Registering keeper: $KeeperTaskName"
    & (Join-Path $PSScriptRoot 'register-tunnel-keeper.ps1') `
        -SshTarget $SshTarget `
        -RemotePort $RemotePort `
        -TaskServerPort $TaskServerPort `
        -IntervalMinutes $IntervalMinutes `
        -TaskName $KeeperTaskName

    Write-Host "Registering watchdog: $WatchdogTaskName"
    & (Join-Path $PSScriptRoot 'register-tunnel-watchdog.ps1') `
        -SshTarget $SshTarget `
        -RemotePort $RemotePort `
        -KeeperTaskName $KeeperTaskName `
        -TaskName $WatchdogTaskName `
        -DevspacePath $DevspacePath `
        -OperatorAlarmPath $OperatorAlarmPath `
        -ProbeIntervalSeconds $ProbeIntervalSeconds `
        -FailureThreshold $FailureThreshold `
        -BashExecutable $BashExecutable

    Write-Host @"

Both Scheduled Tasks are registered and started. Studio's Execution Hosts
admin panel reads their status without further elevation; so does:
  schtasks /Query /TN $KeeperTaskName /FO LIST /V
  schtasks /Query /TN $WatchdogTaskName /FO LIST /V

To show heal history in the admin panel too, set
WindowsTunnelSupervision:WatchdogLogPath in the Task Server's appsettings to:
  $(Join-Path $DevspacePath '.tunnel-watchdog.log')
"@
}
