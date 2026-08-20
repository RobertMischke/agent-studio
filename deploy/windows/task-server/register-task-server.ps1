<#
.SYNOPSIS
Registers the Windows scheduled tasks that supervise the Task Server.

.DESCRIPTION
Registers two idempotent tasks, the Windows counterpart of enabling the
agent-task-server unit and the agent-task-server-backup timer:

- AgentOrchestrator-TaskServer starts the service at boot and re-checks it on a
  periodic fallback trigger, so an exited process is started again.
- AgentOrchestrator-TaskServerBackup takes one verified backup per day.

Both tasks use an S4U principal and an at-startup or time trigger. A durable
service must never depend on an interactive logon session, so neither task is
registered with a logon trigger or an interactive principal.

Run this from an elevated PowerShell session: registering an at-startup task
can require that authority.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $ExecutablePath = 'C:\Program Files\AgentOrchestrator\current\task-server.exe',

    [string] $EnvironmentFile = (Join-Path $env:ProgramData 'AgentOrchestrator\server.env'),

    [string] $StateDirectory = (Join-Path $env:ProgramData 'AgentOrchestrator\task-server'),

    [ValidateRange(1, 60)]
    [int] $IntervalMinutes = 5,

    [ValidatePattern('^([01][0-9]|2[0-3]):[0-5][0-9]$')]
    [string] $BackupAt = '03:30',

    [switch] $SkipBackupTask,

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $TaskName = 'AgentOrchestrator-TaskServer',

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $BackupTaskName = 'AgentOrchestrator-TaskServerBackup',

    [string] $StarterPath = (Join-Path $PSScriptRoot 'start-task-server.ps1'),

    [string] $BackupScriptPath = (Join-Path $PSScriptRoot 'backup-task-server.ps1'),

    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
)

$ErrorActionPreference = 'Stop'
$starter = (Resolve-Path -LiteralPath $StarterPath).Path
$backupScript = (Resolve-Path -LiteralPath $BackupScriptPath).Path
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$powerShell = (Get-Command 'powershell.exe' -ErrorAction Stop).Source

# A task action starts in %SystemRoot%\System32, so a relative path would
# register cleanly and then fail on every run. The state directory need not
# exist yet, so it cannot go through Resolve-Path.
$EnvironmentFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EnvironmentFile)
$StateDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($StateDirectory)

function Format-TaskArgument {
    param([Parameter(Mandatory)] [string] $Value)
    # A trailing backslash run would escape the closing quote for
    # CommandLineToArgvW and swallow the next argument, which is what a
    # tab-completed directory path looks like.
    $escaped = $Value -replace '(\\+)$', '$1$1'
    return '"{0}"' -f ($escaped -replace '"', '""')
}

function New-WrapperArgumentString {
    param([Parameter(Mandatory)] [string] $ScriptPath)
    return @(
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Format-TaskArgument $ScriptPath),
        '-ExecutablePath', (Format-TaskArgument $executable),
        '-EnvironmentFile', (Format-TaskArgument $EnvironmentFile),
        '-StateDirectory', (Format-TaskArgument $StateDirectory)
    ) -join ' '
}

$principal = New-ScheduledTaskPrincipal `
    -UserId $RunAsUser `
    -LogonType S4U `
    -RunLevel Limited

$serviceAction = New-ScheduledTaskAction -Execute $powerShell -Argument (New-WrapperArgumentString $starter)
$startupTrigger = New-ScheduledTaskTrigger -AtStartup
$periodicTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At ([DateTime]::Now.AddMinutes(1)) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) `
    -RepetitionDuration (New-TimeSpan -Days 3650)
# Parallel, not IgnoreNew: Task Scheduler keeps the instance in Running state
# while the detached server stays in the task's job object, and IgnoreNew would
# then drop every repetition, so the state file would never be refreshed while
# the service runs. Single-instance safety comes from the starter's mutex and
# its adoption check, not from the scheduler.
$serviceSettings = New-ScheduledTaskSettingsSet `
    -MultipleInstances Parallel `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

if ($PSCmdlet.ShouldProcess($TaskName, 'Register or update the Task Server scheduled task')) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Description 'Starts and supervises the Agent Orchestrator Task Server.' `
        -Action $serviceAction `
        -Trigger @($startupTrigger, $periodicTrigger) `
        -Principal $principal `
        -Settings $serviceSettings `
        -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Get-ScheduledTask -TaskName $TaskName
}

if ($SkipBackupTask) { return }

$backupAction = New-ScheduledTaskAction -Execute $powerShell -Argument (New-WrapperArgumentString $backupScript)
$backupTrigger = New-ScheduledTaskTrigger `
    -Daily `
    -At ([DateTime]::ParseExact($BackupAt, 'HH:mm', [Globalization.CultureInfo]::InvariantCulture)) `
    -RandomDelay (New-TimeSpan -Minutes 10)
$backupSettings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Hours 1) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

if ($PSCmdlet.ShouldProcess($BackupTaskName, 'Register or update the Task Server backup scheduled task')) {
    Register-ScheduledTask `
        -TaskName $BackupTaskName `
        -Description 'Takes one verified Agent Orchestrator Task Server backup per day.' `
        -Action $backupAction `
        -Trigger $backupTrigger `
        -Principal $principal `
        -Settings $backupSettings `
        -Force | Out-Null
    Get-ScheduledTask -TaskName $BackupTaskName
}
