[CmdletBinding()]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [ValidateRange(1, 65535)]
    [int] $TaskServerPort = 5031,

    [string] $InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Agent Studio\Tunnel'),

    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name,

    [switch] $Elevated
)

$ErrorActionPreference = 'Stop'
$isAdministrator = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Quote-NativeArgument {
    param([Parameter(Mandatory)] [string] $Value)
    return '"{0}"' -f ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1')
}

if (-not $isAdministrator) {
    if ($Elevated) {
        throw 'Windows denied administrator access. Tunnel Scheduled Tasks were not changed.'
    }

    Write-Host ''
    Write-Host 'Agent Studio needs one administrator approval to register two Scheduled Tasks.' -ForegroundColor Yellow
    Write-Host 'The tasks keep the private Task Server tunnel available before sign-in and repair it after a failed functional probe.'
    Write-Host 'Approve the Windows User Account Control prompt to continue, or choose No to leave the machine unchanged.'
    Write-Host ''

    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Quote-NativeArgument $PSCommandPath),
        '-SshTarget', (Quote-NativeArgument $SshTarget),
        '-RemotePort', $RemotePort,
        '-TaskServerPort', $TaskServerPort,
        '-InstallDirectory', (Quote-NativeArgument $InstallDirectory),
        '-RunAsUser', (Quote-NativeArgument $RunAsUser),
        '-Elevated'
    )
    $process = Start-Process powershell.exe -Verb RunAs -ArgumentList ($arguments -join ' ') -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Tunnel supervision setup did not complete (elevated process exit code $($process.ExitCode))."
    }
    Write-Host 'Agent Studio tunnel supervision setup completed.'
    exit 0
}

$stateDirectory = Join-Path $InstallDirectory 'state'
New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null

$assets = @(
    'tunnel-keeper.ps1',
    'tunnel-watchdog.sh',
    'register-tunnel-keeper.ps1',
    'register-tunnel-watchdog.ps1',
    'test-tunnel-watchdog-forced-kill.ps1'
)
foreach ($asset in $assets) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $asset) -Destination (Join-Path $InstallDirectory $asset) -Force
}

$keeperRegistration = Join-Path $InstallDirectory 'register-tunnel-keeper.ps1'
$watchdogRegistration = Join-Path $InstallDirectory 'register-tunnel-watchdog.ps1'

& $keeperRegistration `
    -SshTarget $SshTarget `
    -RemotePort $RemotePort `
    -TaskServerPort $TaskServerPort `
    -KeeperPath (Join-Path $InstallDirectory 'tunnel-keeper.ps1') `
    -StateDirectory $stateDirectory `
    -RunAsUser $RunAsUser | Out-Null

& $watchdogRegistration `
    -SshTarget $SshTarget `
    -RemotePort $RemotePort `
    -KeeperTaskName 'AgentRunner-TunnelKeeper' `
    -WatchdogPath (Join-Path $InstallDirectory 'tunnel-watchdog.sh') `
    -StateDirectory $stateDirectory `
    -RunAsUser $RunAsUser | Out-Null

$keeperTask = Get-ScheduledTask -TaskName 'AgentRunner-TunnelKeeper' -ErrorAction Stop
$watchdogTask = Get-ScheduledTask -TaskName 'AgentRunner-TunnelWatchdog' -ErrorAction Stop
$registration = [ordered]@{
    schemaVersion = 1
    sshTarget = $SshTarget
    remotePort = $RemotePort
    taskServerPort = $TaskServerPort
    registeredAt = [DateTime]::UtcNow.ToString('o')
    keeperTaskName = $keeperTask.TaskName
    keeperRegistered = $true
    watchdogTaskName = $watchdogTask.TaskName
    watchdogRegistered = $true
    installDirectory = $InstallDirectory
    stateDirectory = $stateDirectory
}
$temporaryRegistration = Join-Path $stateDirectory 'registration.json.tmp'
$registrationPath = Join-Path $stateDirectory 'registration.json'
$registration | ConvertTo-Json | Set-Content -LiteralPath $temporaryRegistration -Encoding utf8
Move-Item -LiteralPath $temporaryRegistration -Destination $registrationPath -Force

Write-Host 'Agent Studio tunnel supervision is installed.' -ForegroundColor Green
Write-Host "Keeper:  $($keeperTask.TaskName) [$($keeperTask.State)]"
Write-Host "Watchdog: $($watchdogTask.TaskName) [$($watchdogTask.State)]"
Write-Host "Status:   $registrationPath"
