[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallRoot = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentOrchestrator\current'),

    [string] $EnvironmentFile = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentOrchestrator\server.env'),

    [string] $StateDirectory = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentOrchestrator\state\task-server'),

    [ValidateRange(1, 60)]
    [int] $IntervalMinutes = 5,

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $TaskName = 'AgentOrchestrator-TaskServer',

    [string] $StartPath = (Join-Path $PSScriptRoot 'start-task-server.ps1'),

    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
)

$ErrorActionPreference = 'Stop'
$start = (Resolve-Path -LiteralPath $StartPath).Path
$powerShell = (Get-Command 'powershell.exe' -ErrorAction Stop).Source

function Quote-TaskArgument {
    param([Parameter(Mandatory)] [string] $Value)
    return '"{0}"' -f ($Value -replace '"', '""')
}

$arguments = @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy', 'Bypass',
    '-File', (Quote-TaskArgument $start),
    '-InstallRoot', (Quote-TaskArgument $InstallRoot),
    '-EnvironmentFile', (Quote-TaskArgument $EnvironmentFile),
    '-StateDirectory', (Quote-TaskArgument $StateDirectory)
) -join ' '

$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
$startupTrigger = New-ScheduledTaskTrigger -AtStartup
$periodicTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At ([DateTime]::Now.AddMinutes(1)) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) `
    -RepetitionDuration (New-TimeSpan -Days 3650)
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

if ($PSCmdlet.ShouldProcess($TaskName, 'Register or update the Task Server scheduled task')) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Description 'Runs the Agent Orchestrator Task Server as a session-independent supervised service.' `
        -Action $action `
        -Trigger @($startupTrigger, $periodicTrigger) `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Get-ScheduledTask -TaskName $TaskName
}
