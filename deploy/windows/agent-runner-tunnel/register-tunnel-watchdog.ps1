[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $KeeperTaskName = 'AgentRunner-TunnelKeeper',

    [ValidateRange(10, 3600)]
    [int] $ProbeIntervalSeconds = 60,

    [ValidateRange(1, 20)]
    [int] $FailureThreshold = 2,

    [string] $TaskName = 'AgentRunner-TunnelWatchdog',

    [string] $WatchdogPath = (Join-Path $PSScriptRoot 'tunnel-watchdog.sh'),

    [string] $DevspacePath = (Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) -Parent),

    [string] $OperatorAlarmPath,

    [string] $BashExecutable = 'C:\Program Files\Git\bin\bash.exe',

    [string] $StatusRefreshScript,

    [string] $SupervisionStatusPath,

    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
)

$ErrorActionPreference = 'Stop'
$watchdog = (Resolve-Path -LiteralPath $WatchdogPath).Path
$devspace = (Resolve-Path -LiteralPath $DevspacePath).Path
$bash = (Resolve-Path -LiteralPath $BashExecutable).Path
if ([string]::IsNullOrWhiteSpace($OperatorAlarmPath)) {
    $OperatorAlarmPath = Join-Path $devspace '.operator-alarm.log'
}
$watchdogBashPath = $watchdog -replace '\\', '/'
$devspaceBashPath = $devspace -replace '\\', '/'
$operatorAlarmBashPath = $OperatorAlarmPath -replace '\\', '/'
$statusRefreshBashPath = ''
$supervisionStatusBashPath = ''
if ($StatusRefreshScript -or $SupervisionStatusPath) {
    if (-not $StatusRefreshScript -or -not $SupervisionStatusPath) {
        throw 'StatusRefreshScript and SupervisionStatusPath must be provided together.'
    }
    $statusRefreshBashPath = (Resolve-Path -LiteralPath $StatusRefreshScript).Path -replace '\\', '/'
    $supervisionStatusBashPath = $SupervisionStatusPath -replace '\\', '/'
}

function Quote-TaskArgument {
    param([Parameter(Mandatory)] [string] $Value)
    return '"{0}"' -f ($Value -replace '"', '\"')
}

$argumentList = @(
    (Quote-TaskArgument $watchdogBashPath),
    '--devspace', (Quote-TaskArgument $devspaceBashPath),
    '--ssh-target', (Quote-TaskArgument $SshTarget),
    '--remote-port', $RemotePort,
    '--keeper-task', (Quote-TaskArgument $KeeperTaskName),
    '--operator-alarm', (Quote-TaskArgument $operatorAlarmBashPath),
    '--probe-interval-seconds', $ProbeIntervalSeconds,
    '--failure-threshold', $FailureThreshold
)
if ($statusRefreshBashPath) {
    $argumentList += @(
        '--watchdog-task', (Quote-TaskArgument $TaskName),
        '--status-refresh-script', (Quote-TaskArgument $statusRefreshBashPath),
        '--supervision-status-path', (Quote-TaskArgument $supervisionStatusBashPath)
    )
}
$action = New-ScheduledTaskAction -Execute $bash -Argument ($argumentList -join ' ')
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal `
    -UserId $RunAsUser `
    -LogonType S4U `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

if ($PSCmdlet.ShouldProcess($TaskName, 'Register or update the tunnel watchdog scheduled task')) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Description 'Probes the Agent Host reverse tunnel every minute and applies bounded self-healing.' `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Get-ScheduledTask -TaskName $TaskName
}
