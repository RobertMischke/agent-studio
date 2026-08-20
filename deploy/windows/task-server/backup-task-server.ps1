<#
.SYNOPSIS
Takes one verified Task Server backup on Windows.

.DESCRIPTION
This is the action of the AgentOrchestrator-TaskServerBackup scheduled task and
the Windows counterpart of the agent-task-server-backup service and timer. It
loads the same server.env bootstrap contract as the service, runs the packaged
backup command, and stores the JSON result beside the wrapper state.

Taking a backup is not a server restart: the command applies schema migrations
idempotently, verifies the snapshot, and leaves live leases untouched.

.NOTES
The exit code is the exit code of the backup command, or 4 when this wrapper
failed before the command ran.
#>
[CmdletBinding()]
param(
    [string] $ExecutablePath = 'C:\Program Files\AgentOrchestrator\current\task-server.exe',

    [string] $EnvironmentFile = (Join-Path $env:ProgramData 'AgentOrchestrator\server.env'),

    [string] $StateDirectory = (Join-Path $env:ProgramData 'AgentOrchestrator\task-server'),

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $BackupName = 'timer',

    [ValidateRange(1, 3650)]
    [int] $LogRetentionDays = 30
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'task-server-common.ps1')

$logPath = Join-Path $StateDirectory 'events.log'

try {
    New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    Remove-ExpiredWrapperFile -Directory $StateDirectory `
        -Pattern @('backup-*.json', 'backup-*.stderr.log') `
        -RetentionDays $LogRetentionDays
    $executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
    Import-ServerEnvironmentFile -Path $EnvironmentFile | Out-Null

    # A scheduled task action starts in %SystemRoot%\System32, and STORE_PATH
    # and BACKUP_PATH are resolved against the working directory, so this
    # mirrors WorkingDirectory= on the systemd backup unit.
    $workingDirectory = if ($env:STORE_PATH) { $env:STORE_PATH } else { Split-Path -Parent $executable }
    New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
    $attemptId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $resultPath = Join-Path $StateDirectory "backup-$attemptId.json"
    $errorPath = Join-Path $StateDirectory "backup-$attemptId.stderr.log"
    # -NoNewWindow, not -WindowStyle Hidden: see start-task-server.ps1.
    $process = Start-Process `
        -FilePath $executable `
        -ArgumentList @('backup', '--name', $BackupName) `
        -WorkingDirectory $workingDirectory `
        -NoNewWindow `
        -RedirectStandardOutput $resultPath `
        -RedirectStandardError $errorPath `
        -PassThru `
        -Wait

    Write-TaskServerEvent -LogPath $logPath -Event 'backup_completed' -Data @{
        name = $BackupName
        exitCode = $process.ExitCode
        result = $resultPath
        stderr = $errorPath
    }
    exit $process.ExitCode
}
catch {
    if (-not (Test-Path -LiteralPath $StateDirectory)) {
        New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    }
    Write-TaskServerEvent -LogPath $logPath -Event 'backup_failed' -Data @{
        name = $BackupName
        message = $_.Exception.Message
    }
    exit 4
}
