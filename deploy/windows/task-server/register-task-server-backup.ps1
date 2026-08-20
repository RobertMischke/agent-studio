[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallRoot = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentOrchestrator\current'),

    [string] $EnvironmentFile = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentOrchestrator\server.env'),

    [string] $StateDirectory = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentOrchestrator\state\task-server'),

    [DateTime] $At = [DateTime]::Today.AddHours(3),

    [ValidateRange(0, 720)]
    [int] $RandomDelayMinutes = 10,

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $TaskName = 'AgentOrchestrator-TaskServerBackup',

    [string] $BackupPath = (Join-Path $PSScriptRoot 'backup-task-server.ps1'),

    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
)

$ErrorActionPreference = 'Stop'
$backup = (Resolve-Path -LiteralPath $BackupPath).Path
$powerShell = (Get-Command 'powershell.exe' -ErrorAction Stop).Source

function Quote-TaskArgument {
    param([Parameter(Mandatory)] [string] $Value)
    return '"{0}"' -f ($Value -replace '"', '""')
}

$arguments = @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy', 'Bypass',
    '-File', (Quote-TaskArgument $backup),
    '-InstallRoot', (Quote-TaskArgument $InstallRoot),
    '-EnvironmentFile', (Quote-TaskArgument $EnvironmentFile),
    '-StateDirectory', (Quote-TaskArgument $StateDirectory),
    '-Name', 'timer'
) -join ' '

$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
$trigger = New-ScheduledTaskTrigger `
    -Daily `
    -At $At `
    -RandomDelay (New-TimeSpan -Minutes $RandomDelayMinutes)
$principal = New-ScheduledTaskPrincipal `
    -UserId $RunAsUser `
    -LogonType S4U `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Hours 1) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

if ($PSCmdlet.ShouldProcess($TaskName, 'Register or update the Task Server backup scheduled task')) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Description 'Creates a verified Agent Orchestrator Task Server backup on a daily schedule.' `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    Get-ScheduledTask -TaskName $TaskName
}
