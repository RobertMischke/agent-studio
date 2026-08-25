[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    [string] $DevspaceRoot = 'C:\Projects\agent-taskboard-devspace',
    [string] $InstallBase = 'C:\AgentOrchestrator',
    [string] $EnvFile = 'C:\ProgramData\AgentOrchestrator\server.env',
    [string] $ListenUrl = 'http://127.0.0.1:5071',
    [string] $TaskName = 'AgentOrchestrator-TaskServer',
    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$project = Join-Path $source 'task-server\TaskServer.csproj'
$version = '{0}-{1}' -f (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmssZ'), (git -C $source rev-parse --short=12 HEAD)
$release = Join-Path $InstallBase $version
$current = Join-Path $InstallBase 'current'
$state = Join-Path $DevspaceRoot 'state\task-server'
$store = Join-Path $state 'data'
$backups = Join-Path $state 'backups'
$register = Join-Path $source 'deploy\windows\task-server\register-task-server.ps1'

if ($PSCmdlet.ShouldProcess($release, 'Publish and install supervised Task Server release')) {
    New-Item -ItemType Directory -Path $release, $store, $backups, (Split-Path $EnvFile) -Force | Out-Null
    dotnet publish $project -p:PublishProfile=win-x64 -o $release
    if ($LASTEXITCODE -ne 0) { throw "Task Server publish failed with exit code $LASTEXITCODE." }

    if (-not (Test-Path -LiteralPath $EnvFile)) {
        @(
            "LISTEN_URL=$ListenUrl"
            "STORE_PATH=$store"
            "BACKUP_PATH=$backups"
            'AUTH=none'
        ) | Set-Content -LiteralPath $EnvFile -Encoding ascii
    }
    else {
        $configured = Get-Content -LiteralPath $EnvFile
        foreach ($required in 'LISTEN_URL=', 'STORE_PATH=', 'BACKUP_PATH=', 'AUTH=') {
            if (-not ($configured | Where-Object { $_.StartsWith($required, [StringComparison]::OrdinalIgnoreCase) })) {
                throw "Existing environment file '$EnvFile' is missing required key '$required'. Refusing to replace host-owned configuration."
            }
        }
    }

    if (Test-Path -LiteralPath $current) {
        $item = Get-Item -LiteralPath $current -Force
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Current path '$current' is not a junction; refusing to replace it."
        }
        cmd.exe /d /c rmdir "$current"
        if ($LASTEXITCODE -ne 0) { throw "Could not replace Task Server current junction." }
    }
    New-Item -ItemType Junction -Path $current -Target $release | Out-Null

    & $register -InstallRoot $current -EnvFile $EnvFile -TaskName $TaskName -RunAsUser $RunAsUser
    if ($LASTEXITCODE -ne 0) { throw "Task Server Scheduled Task registration failed." }
    Write-Output "Installed Task Server release $version. Proxy mode remains a separate post-migration step."
}
