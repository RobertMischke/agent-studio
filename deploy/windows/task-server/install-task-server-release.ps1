[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $SourceCheckout,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F]{40}$')] [string] $ReleaseSha,
    [Parameter(Mandatory)] [string] $DataDirectory,
    [string] $InstallBase = 'C:\AgentOrchestrator',
    [string] $EnvFile = 'C:\ProgramData\AgentOrchestrator\server.env',
    [string] $StableConfigurationPath,
    [string] $ListenUrl = 'http://127.0.0.1:5071'
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceCheckout).Path
$project = Join-Path $source 'task-server\TaskServer.csproj'
$registerScript = Join-Path $source 'deploy\windows\task-server\register-task-server.ps1'
$version = 'release-{0}' -f $ReleaseSha.ToLowerInvariant()
$releaseDirectory = Join-Path $InstallBase $version
$current = Join-Path $InstallBase 'current'
$staging = Join-Path $InstallBase ('.staging-{0}' -f $ReleaseSha.ToLowerInvariant())
$backupDirectory = Join-Path $DataDirectory 'backups'

if (-not (Test-Path -LiteralPath $project)) { throw "Task Server project not found: $project" }
if (-not (Test-Path -LiteralPath $registerScript)) { throw "Registration script not found: $registerScript" }
if ([string]::IsNullOrWhiteSpace($StableConfigurationPath)) {
    $StableConfigurationPath = Join-Path $source 'backend\appsettings.Local.json'
}
if (-not (Test-Path -LiteralPath $StableConfigurationPath)) {
    throw "Stable appsettings.Local.json not found: $StableConfigurationPath"
}

if ($PSCmdlet.ShouldProcess($releaseDirectory, 'Publish and install the Task Server release')) {
    New-Item -ItemType Directory -Path $InstallBase -Force | Out-Null
    New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }

    & dotnet publish $project '-p:PublishProfile=win-x64' '-o' $staging
    if ($LASTEXITCODE -ne 0) { throw "Task Server publish failed with exit code $LASTEXITCODE." }
    $executable = Join-Path $staging 'task-server.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw "Published Task Server executable is missing: $executable" }

    if (-not (Test-Path -LiteralPath $releaseDirectory)) {
        Move-Item -LiteralPath $staging -Destination $releaseDirectory
    } else {
        Remove-Item -LiteralPath $staging -Recurse -Force
        $installedVersion = & (Join-Path $releaseDirectory 'task-server.exe') '--version'
        if ($LASTEXITCODE -ne 0 -or $installedVersion -notmatch [regex]::Escape($ReleaseSha)) {
            throw "Existing release directory does not contain the requested SHA: $releaseDirectory"
        }
    }

    $envParent = Split-Path -Parent $EnvFile
    New-Item -ItemType Directory -Path $envParent -Force | Out-Null
    if (-not (Test-Path -LiteralPath $EnvFile)) {
        @(
            "LISTEN_URL=$ListenUrl"
            "STORE_PATH=$DataDirectory"
            "BACKUP_PATH=$backupDirectory"
            'AUTH=none'
        ) | Set-Content -LiteralPath $EnvFile -Encoding ascii
    }

    $configuration = Get-Content -LiteralPath $StableConfigurationPath -Raw | ConvertFrom-Json
    if ($null -eq $configuration.TaskServer) {
        $configuration | Add-Member -MemberType NoteProperty -Name TaskServer -Value ([pscustomobject]@{})
    }
    if ($configuration.TaskServer.PSObject.Properties['BaseUrl']) {
        $configuration.TaskServer.BaseUrl = $ListenUrl
    } else {
        $configuration.TaskServer | Add-Member -MemberType NoteProperty -Name BaseUrl -Value $ListenUrl
    }
    $configuration | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $StableConfigurationPath -Encoding utf8

    if (Test-Path -LiteralPath $current) {
        $item = Get-Item -LiteralPath $current -Force
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to replace non-junction current path: $current"
        }
        & cmd.exe /d /c "rmdir `"$current`""
        if ($LASTEXITCODE -ne 0) { throw "Could not remove the previous Task Server current junction." }
    }
    & cmd.exe /d /c "mklink /J `"$current`" `"$releaseDirectory`"" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create Task Server current junction." }

    & $registerScript -InstallRoot $current -EnvFile $EnvFile
    if ($LASTEXITCODE -ne 0) { throw "Task Server Scheduled Task registration failed." }

    [pscustomobject]@{
        ReleaseSha = $ReleaseSha.ToLowerInvariant()
        ReleaseDirectory = $releaseDirectory
        Current = $current
        DataDirectory = $DataDirectory
        EnvFile = $EnvFile
        StableConfigurationPath = $StableConfigurationPath
        ListenUrl = $ListenUrl
    }
}
