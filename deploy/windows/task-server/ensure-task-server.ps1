[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SourceRoot,

    [Parameter(Mandatory)]
    [string] $ReleaseId,

    [string] $DevspaceRoot = (Split-Path -Parent $SourceRoot),

    [string] $ListenUrl = 'http://127.0.0.1:5071',

    [string] $TaskName = 'AgentOrchestrator-TaskServer',

    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name,

    [string] $AppSettingsPath = (Join-Path $SourceRoot 'backend\appsettings.Local.json'),

    [ValidateRange(5, 300)]
    [int] $ReadyTimeoutSeconds = 120
)

# Idempotent Stable rollout boundary for the local Windows topology. This is
# intentionally invoked before OrchestratorApi: the API must never enter proxy
# mode until the independently supervised authority answers /readyz.

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$installBase = Join-Path $DevspaceRoot 'task-server-releases'
$installRoot = Join-Path $installBase $ReleaseId
$dataRoot = Join-Path $DevspaceRoot 'task-server-data'
$envRoot = Join-Path $DevspaceRoot 'task-server-config'
$envFile = Join-Path $envRoot 'server.env'
$current = Join-Path $installBase 'current'
$project = Join-Path $source 'task-server\TaskServer.csproj'
$register = Join-Path $source 'deploy\windows\task-server\register-task-server.ps1'

New-Item -ItemType Directory -Path $installBase, $dataRoot, $envRoot -Force | Out-Null
foreach ($protectedPath in @($dataRoot, $envRoot)) {
    icacls.exe $protectedPath /inheritance:r /grant:r `
        "${RunAsUser}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict Task Server ACLs for $protectedPath." }
}

if (-not (Test-Path -LiteralPath (Join-Path $installRoot 'task-server.exe'))) {
    $staging = "$installRoot.staging-$([Guid]::NewGuid().ToString('N'))"
    try {
        dotnet publish $project -p:PublishProfile=win-x64 -o $staging
        if ($LASTEXITCODE -ne 0) { throw "Task Server publish failed with exit code $LASTEXITCODE." }
        Move-Item -LiteralPath $staging -Destination $installRoot
    }
    finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
}

if (-not (Test-Path -LiteralPath $envFile)) {
    @(
        "LISTEN_URL=$ListenUrl"
        "STORE_PATH=$(Join-Path $dataRoot 'store')"
        "BACKUP_PATH=$(Join-Path $dataRoot 'backups')"
        'AUTH=none'
    ) | Set-Content -LiteralPath $envFile -Encoding ascii
}

if (-not (Test-Path -LiteralPath $AppSettingsPath)) {
    throw "Stable local configuration not found: $AppSettingsPath"
}
$localSettings = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json
$legacyRoot = [string]$localSettings.TaskRepository
if ([string]::IsNullOrWhiteSpace($legacyRoot)) {
    throw "Stable local configuration does not define TaskRepository for the first-start migration."
}
$storeDatabase = Join-Path (Join-Path $dataRoot 'store') 'task-server.db'
if (-not (Test-Path -LiteralPath $storeDatabase)) {
    $envText = Get-Content -LiteralPath $envFile -Raw
    if ($envText -notmatch '(?m)^LEGACY_MIGRATION_ROOT=') {
        Add-Content -LiteralPath $envFile -Encoding ascii -Value @(
            "LEGACY_MIGRATION_ROOT=$legacyRoot"
            'LEGACY_MIGRATION_WORKSPACE=Agent Studio'
            'LEGACY_MIGRATION_FREEZE_CONFIRMED=true'
        )
    }
}
$taskServerSettings = $localSettings.PSObject.Properties['TaskServer']
if ($null -eq $taskServerSettings) {
    $localSettings | Add-Member -MemberType NoteProperty -Name TaskServer -Value ([pscustomobject]@{})
}
if ($null -eq $localSettings.TaskServer.PSObject.Properties['BaseUrl']) {
    $localSettings.TaskServer | Add-Member -MemberType NoteProperty -Name BaseUrl -Value $ListenUrl
}
else {
    $localSettings.TaskServer.BaseUrl = $ListenUrl
}
$settingsStaging = "$AppSettingsPath.staging-$([Guid]::NewGuid().ToString('N'))"
$localSettings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $settingsStaging -Encoding utf8
Move-Item -LiteralPath $settingsStaging -Destination $AppSettingsPath -Force

if (Test-Path -LiteralPath $current) {
    $item = Get-Item -LiteralPath $current -Force
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to replace non-junction Task Server current path: $current"
    }
    cmd.exe /d /c rmdir "$current" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not remove Task Server current junction: $current" }
}
cmd.exe /d /c mklink /J "$current" "$installRoot" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not create Task Server current junction." }

& $register -InstallRoot $current -EnvFile $envFile -TaskName $TaskName -RunAsUser $RunAsUser
if ($LASTEXITCODE -ne 0) { throw "Task Server Scheduled Task registration failed." }

$deadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
do {
    try {
        $ready = Invoke-WebRequest -UseBasicParsing -Uri "$($ListenUrl.TrimEnd('/'))/readyz" -TimeoutSec 3
        if ($ready.StatusCode -eq 200) {
            Write-Output "task-server-ready release=$ReleaseId url=$ListenUrl install=$installRoot data=$dataRoot"
            exit 0
        }
    }
    catch {
        Start-Sleep -Seconds 1
    }
} while ([DateTime]::UtcNow -lt $deadline)

throw "Task Server did not become ready at $ListenUrl within $ReadyTimeoutSeconds seconds."
