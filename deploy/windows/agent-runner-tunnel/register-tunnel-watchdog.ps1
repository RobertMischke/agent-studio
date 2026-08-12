[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [ValidateRange(1, 10)]
    [int] $IntervalMinutes = 1,

    [string] $TaskName = 'AgentRunner-TunnelWatchdog',

    [string] $KeeperTaskName = 'AgentRunner-TunnelKeeper',

    [string] $DevspaceDirectory = '',

    [string] $WatchdogPath = (Join-Path $PSScriptRoot 'tunnel-watchdog.ps1')
)

$ErrorActionPreference = 'Stop'
$watchdog = (Resolve-Path -LiteralPath $WatchdogPath).Path
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if ([string]::IsNullOrWhiteSpace($DevspaceDirectory)) {
    $DevspaceDirectory = Split-Path -Parent $repositoryRoot
}
$devspace = (Resolve-Path -LiteralPath $DevspaceDirectory).Path
$powerShell = (Get-Command 'powershell.exe' -ErrorAction Stop).Source
$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name

if (-not (Get-ScheduledTask -TaskName $KeeperTaskName -ErrorAction SilentlyContinue)) {
    throw "Keeper task '$KeeperTaskName' does not exist. Register the keeper before the watchdog."
}

function Quote-TaskArgument {
    param([Parameter(Mandatory)] [string] $Value)
    return '"{0}"' -f ($Value -replace '"', '""')
}

$arguments = @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy', 'Bypass',
    '-File', (Quote-TaskArgument $watchdog),
    '-SshTarget', $SshTarget,
    '-RemotePort', $RemotePort,
    '-KeeperTaskName', (Quote-TaskArgument $KeeperTaskName),
    '-DevspaceDirectory', (Quote-TaskArgument $devspace)
) -join ' '

$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
$trigger = New-ScheduledTaskTrigger `
    -Once `
    -At ([DateTime]::Now.AddMinutes(1)) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) `
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
    -ExecutionTimeLimit (New-TimeSpan -Minutes 2)

if ($PSCmdlet.ShouldProcess($TaskName, 'Register or update the tunnel watchdog scheduled task')) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Description 'Probes the reverse tunnel each minute, clears a stale remote listener, and restarts the keeper.' `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Get-ScheduledTask -TaskName $TaskName
}
