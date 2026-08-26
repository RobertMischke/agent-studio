[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Checkout = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    [string] $DevspaceRoot = 'C:\Projects\agent-orchestrator-devspace',
    [string] $InstallBase = 'C:\AgentOrchestrator',
    [string] $ProgramDataRoot = 'C:\ProgramData\AgentOrchestrator',
    [string] $ListenUrl = 'http://127.0.0.1:5071',
    [string] $TaskName = 'AgentOrchestrator-TaskServer',
    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
)

$ErrorActionPreference = 'Stop'
$sha = (& git -C $Checkout rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[0-9a-f]{40}$') {
    throw "Could not resolve the release Git SHA from $Checkout."
}
$version = '{0:yyyyMMdd-HHmmssZ}-{1}' -f [DateTime]::UtcNow, $sha.Substring(0, 12)
$releaseRoot = Join-Path $InstallBase $version
$publishRoot = Join-Path $releaseRoot 'task-server'
$current = Join-Path $InstallBase 'current'
$dataRoot = Join-Path $DevspaceRoot 'task-server-data'
$backupRoot = Join-Path $dataRoot 'backups'
$envFile = Join-Path $ProgramDataRoot 'server.env'

if (-not $PSCmdlet.ShouldProcess($releaseRoot, "Publish and install Task Server release $sha")) {
    return
}

New-Item -ItemType Directory -Force -Path $publishRoot, $dataRoot, $backupRoot, $ProgramDataRoot | Out-Null
& dotnet publish (Join-Path $Checkout 'task-server\TaskServer.csproj') `
    -p:PublishProfile=win-x64 `
    -p:SourceRevisionId=$sha `
    -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Task Server publish failed.' }

$environment = @(
    "LISTEN_URL=$ListenUrl"
    "STORE_PATH=$dataRoot"
    "BACKUP_PATH=$backupRoot"
    'AUTH=none'
) -join [Environment]::NewLine
if (-not (Test-Path -LiteralPath $envFile)) {
    [IO.File]::WriteAllText($envFile, $environment + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
& icacls.exe $ProgramDataRoot /inheritance:r /grant:r `
    "${RunAsUser}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not restrict $ProgramDataRoot." }
& icacls.exe $dataRoot /inheritance:r /grant:r `
    "${RunAsUser}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not restrict $dataRoot." }

if (Test-Path -LiteralPath $current) {
    $item = Get-Item -LiteralPath $current -Force
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to replace non-junction path: $current"
    }
    & cmd.exe /d /c rmdir $current
    if ($LASTEXITCODE -ne 0) { throw "Could not remove the old current junction: $current" }
}
& cmd.exe /d /c mklink /J $current $publishRoot | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not create the current junction: $current" }

& (Join-Path $PSScriptRoot 'register-task-server.ps1') `
    -InstallRoot $current `
    -EnvFile $envFile `
    -TaskName $TaskName `
    -RunAsUser $RunAsUser

[pscustomobject]@{
    GitSha = $sha
    ReleaseRoot = $releaseRoot
    Current = $current
    DataRoot = $dataRoot
    EnvFile = $envFile
    ListenUrl = $ListenUrl
    TaskName = $TaskName
} | ConvertTo-Json
