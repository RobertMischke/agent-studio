[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $SourceRoot,

    [string] $InstallBase = 'C:\AgentOrchestrator',

    [string] $EnvFile = 'C:\ProgramData\AgentOrchestrator\server.env',

    [string] $DataDirectory = (Join-Path (Split-Path -Parent $SourceRoot) 'task-server-data'),

    [string] $ListenUrl = 'http://127.0.0.1:5071',

    [string] $TaskName = 'AgentOrchestrator-TaskServer',

    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$project = Join-Path $source 'task-server\TaskServer.csproj'
if (-not (Test-Path -LiteralPath $project)) {
    throw "Task Server project not found: $project"
}

$sha = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[0-9a-f]{40}$') {
    throw 'Could not resolve the release Git SHA.'
}
$version = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssZ') + '-' + $sha.Substring(0, 12)
$releaseRoot = Join-Path $InstallBase $version
$publishRoot = Join-Path $releaseRoot 'task-server'
$current = Join-Path $InstallBase 'current'
$backupDirectory = Join-Path $DataDirectory 'backups'

if ($PSCmdlet.ShouldProcess($releaseRoot, 'Publish and install the Task Server release')) {
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
    & dotnet publish $project -p:PublishProfile=win-x64 -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw "Task Server publish failed with exit code $LASTEXITCODE." }

    Copy-Item -LiteralPath (Join-Path $source 'deploy\windows\task-server\start-task-server.ps1') `
        -Destination (Join-Path $publishRoot 'start-task-server.ps1') -Force
    New-Item -ItemType Directory -Path $DataDirectory, $backupDirectory, (Split-Path -Parent $EnvFile) -Force | Out-Null

    if (-not (Test-Path -LiteralPath $EnvFile)) {
        @(
            "LISTEN_URL=$ListenUrl"
            "STORE_PATH=$DataDirectory"
            "BACKUP_PATH=$backupDirectory"
            'AUTH=none'
        ) | Set-Content -LiteralPath $EnvFile -Encoding ascii
    }
    & icacls.exe $EnvFile /inheritance:r /grant:r `
        "${RunAsUser}:(R)" '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict $EnvFile to the service identity." }
    & icacls.exe $DataDirectory /inheritance:r /grant:r `
        "${RunAsUser}:(OI)(CI)(M)" '*S-1-5-18:(OI)(CI)(F)' '*S-1-5-32-544:(OI)(CI)(F)' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict $DataDirectory to the service identity." }

    $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -ne $existing) { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $current) {
        $item = Get-Item -LiteralPath $current -Force
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to replace non-junction install path: $current"
        }
        Remove-Item -LiteralPath $current -Force
    }
    New-Item -ItemType Junction -Path $current -Target $publishRoot | Out-Null

    & (Join-Path $source 'deploy\windows\task-server\register-task-server.ps1') `
        -InstallRoot $current `
        -EnvFile $EnvFile `
        -TaskName $TaskName `
        -RunAsUser $RunAsUser `
        -StartScriptPath (Join-Path $current 'start-task-server.ps1')
    if ($LASTEXITCODE -ne 0) { throw "Task Server registration failed with exit code $LASTEXITCODE." }
}

[pscustomobject]@{
    Version = $version
    GitSha = $sha
    InstallRoot = $current
    DataDirectory = $DataDirectory
    EnvFile = $EnvFile
    ListenUrl = $ListenUrl
    TaskName = $TaskName
}
