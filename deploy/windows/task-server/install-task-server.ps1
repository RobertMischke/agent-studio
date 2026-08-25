[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $SourceRoot,

    [string] $Version = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmssZ'),

    [string] $InstallBase = 'C:\AgentOrchestrator',

    [string] $DevspaceDirectory = 'C:\Projects\agent-taskboard-devspace',

    [string] $ProgramDataDirectory = 'C:\ProgramData\AgentOrchestrator',

    [string] $ListenUrl = 'http://127.0.0.1:5071',

    [string] $TaskName = 'AgentOrchestrator-TaskServer'
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$project = Join-Path $source 'task-server\TaskServer.csproj'
$releaseDirectory = Join-Path $InstallBase $Version
$current = Join-Path $InstallBase 'current'
$dataDirectory = Join-Path $DevspaceDirectory 'task-server-data'
$backupDirectory = Join-Path $dataDirectory 'backups'
$envFile = Join-Path $ProgramDataDirectory 'server.env'
$serviceScripts = Join-Path $ProgramDataDirectory 'task-server-service'

if (-not (Test-Path -LiteralPath $project)) {
    throw "Task Server project not found: $project"
}
if ($Version -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'Version may contain only letters, numbers, dot, underscore, and hyphen.'
}

if ($PSCmdlet.ShouldProcess($releaseDirectory, 'Publish and install the Task Server release')) {
    New-Item -ItemType Directory -Path $releaseDirectory, $dataDirectory, $backupDirectory, $ProgramDataDirectory, $serviceScripts -Force | Out-Null
    dotnet publish $project -p:PublishProfile=win-x64 -o $releaseDirectory
    if ($LASTEXITCODE -ne 0) { throw "Task Server publish failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath (Join-Path $releaseDirectory 'task-server.exe'))) {
        throw "Published Task Server executable is missing from $releaseDirectory."
    }

    Copy-Item -LiteralPath (Join-Path $source 'deploy\windows\task-server\start-task-server.ps1') -Destination $serviceScripts -Force
    Copy-Item -LiteralPath (Join-Path $source 'deploy\windows\task-server\register-task-server.ps1') -Destination $serviceScripts -Force

    @(
        "LISTEN_URL=$ListenUrl"
        "STORE_PATH=$dataDirectory"
        "BACKUP_PATH=$backupDirectory"
        'AUTH=none'
    ) | Set-Content -LiteralPath $envFile -Encoding ascii

    if (Test-Path -LiteralPath $current) {
        $currentItem = Get-Item -LiteralPath $current -Force
        if (-not ($currentItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to replace non-junction current path: $current"
        }
        cmd.exe /d /c rmdir "$current"
        if ($LASTEXITCODE -ne 0) { throw "Could not remove old current junction: $current" }
    }
    cmd.exe /d /c mklink /J "$current" "$releaseDirectory" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create current junction: $current" }

    & (Join-Path $serviceScripts 'register-task-server.ps1') `
        -InstallRoot $current `
        -EnvFile $envFile `
        -TaskName $TaskName `
        -StartScriptPath (Join-Path $serviceScripts 'start-task-server.ps1')

    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        try {
            $ready = Invoke-WebRequest -UseBasicParsing -Uri "$ListenUrl/readyz" -TimeoutSec 3
            if ($ready.StatusCode -eq 200) { break }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $ready -or $ready.StatusCode -ne 200) {
        throw "Task Server did not become ready at $ListenUrl/readyz."
    }

    [pscustomobject]@{
        Version = $Version
        ReleaseDirectory = $releaseDirectory
        Current = $current
        DataDirectory = $dataDirectory
        EnvironmentFile = $envFile
        ReadyUrl = "$ListenUrl/readyz"
        ScheduledTask = $TaskName
    }
}
