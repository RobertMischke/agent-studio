[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [string] $KeeperTaskName = 'AgentRunner-TunnelKeeper',

    [ValidateRange(10, 3600)]
    [int] $ProbeIntervalSeconds = 60,

    [ValidateRange(1, 10)]
    [int] $FailureThreshold = 2,

    [string] $DevspaceDirectory = 'C:\Projects\agent-taskboard-devspace',

    [string] $TaskName = 'AgentRunner-TunnelWatchdog',

    [string] $WatchdogPath = (Join-Path $PSScriptRoot 'tunnel-watchdog.ps1')
)

$ErrorActionPreference = 'Stop'
$watchdog = (Resolve-Path -LiteralPath $WatchdogPath).Path
$powerShell = (Get-Command 'powershell.exe' -ErrorAction Stop).Source
$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
if (-not (Get-ScheduledTask -TaskName $KeeperTaskName -ErrorAction SilentlyContinue)) {
    throw "Keeper task '$KeeperTaskName' does not exist. Register the keeper before the watchdog."
}
$quotedWatchdog = '"{0}"' -f ($watchdog -replace '"', '""')
$quotedDevspace = '"{0}"' -f ($DevspaceDirectory -replace '"', '""')
$quotedKeeperTaskName = '"{0}"' -f ($KeeperTaskName -replace '"', '""')
$arguments = @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy', 'Bypass',
    '-File', $quotedWatchdog,
    '-SshTarget', $SshTarget,
    '-RemotePort', $RemotePort,
    '-KeeperTaskName', $quotedKeeperTaskName,
    '-ProbeIntervalSeconds', $ProbeIntervalSeconds,
    '-FailureThreshold', $FailureThreshold,
    '-DevspaceDirectory', $quotedDevspace
) -join ' '

$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
$startupTrigger = New-ScheduledTaskTrigger -AtStartup
$restartTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At ([DateTime]::Now.AddMinutes(1)) `
    -RepetitionInterval (New-TimeSpan -Minutes 1) `
    -RepetitionDuration (New-TimeSpan -Days 3650)
$principal = New-ScheduledTaskPrincipal `
    -UserId $userId `
    -LogonType S4U `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

if ($PSCmdlet.ShouldProcess($TaskName, 'Register or update the tunnel watchdog scheduled task')) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Description 'Probes and self-heals the Agent Host reverse tunnel every minute.' `
        -Action $action `
        -Trigger @($startupTrigger, $restartTrigger) `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Get-ScheduledTask -TaskName $TaskName
}
