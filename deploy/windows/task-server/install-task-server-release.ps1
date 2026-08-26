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
$supervisorScript = Join-Path $source 'deploy\windows\task-server\start-task-server.ps1'
$version = 'release-{0}' -f $ReleaseSha.ToLowerInvariant()
$releaseDirectory = Join-Path $InstallBase $version
$current = Join-Path $InstallBase 'current'
$staging = Join-Path $InstallBase ('.staging-{0}' -f $ReleaseSha.ToLowerInvariant())
$backupDirectory = Join-Path $DataDirectory 'backups'
$normalizedInstallBase = [IO.Path]::GetFullPath($InstallBase).TrimEnd('\')
$normalizedDataDirectory = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')

if ($normalizedDataDirectory.Equals(
        $normalizedInstallBase,
        [StringComparison]::OrdinalIgnoreCase) -or $normalizedDataDirectory.StartsWith(
        $normalizedInstallBase + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DataDirectory must be outside the versioned Task Server installation root.'
}

if (-not (Test-Path -LiteralPath $project)) { throw "Task Server project not found: $project" }
if (-not (Test-Path -LiteralPath $registerScript)) { throw "Registration script not found: $registerScript" }
if (-not (Test-Path -LiteralPath $supervisorScript)) { throw "Supervisor script not found: $supervisorScript" }
$sourceHead = (& git -C $source rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $sourceHead -ne $ReleaseSha.ToLowerInvariant()) {
    throw "Source checkout HEAD '$sourceHead' does not match requested release SHA '$ReleaseSha'."
}
$sourceChanges = @(& git -C $source status --porcelain)
if ($LASTEXITCODE -ne 0) { throw "Could not inspect source checkout status: $source" }
if ($sourceChanges.Count -gt 0) {
    throw "Source checkout has local changes; refusing to publish untracked release bytes: $source"
}
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
    $publishedVersion = & $executable '--version'
    if ($LASTEXITCODE -ne 0 -or $publishedVersion -notmatch [regex]::Escape($ReleaseSha)) {
        throw "Published Task Server binary does not report the requested SHA '$ReleaseSha'."
    }
    Copy-Item -LiteralPath $supervisorScript -Destination (Join-Path $staging 'start-task-server.ps1')

    if (-not (Test-Path -LiteralPath $releaseDirectory)) {
        Move-Item -LiteralPath $staging -Destination $releaseDirectory
    } else {
        Remove-Item -LiteralPath $staging -Recurse -Force
        $installedVersion = & (Join-Path $releaseDirectory 'task-server.exe') '--version'
        if ($LASTEXITCODE -ne 0 -or $installedVersion -notmatch [regex]::Escape($ReleaseSha)) {
            throw "Existing release directory does not contain the requested SHA: $releaseDirectory"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $releaseDirectory 'start-task-server.ps1'))) {
            throw "Existing release directory does not contain the packaged supervisor: $releaseDirectory"
        }
    }

    $envParent = Split-Path -Parent $EnvFile
    New-Item -ItemType Directory -Path $envParent -Force | Out-Null
    $environmentLines = if (Test-Path -LiteralPath $EnvFile) {
        @(Get-Content -LiteralPath $EnvFile)
    } else {
        @()
    }
    $requiredEnvironment = [ordered]@{
        LISTEN_URL = $ListenUrl
        STORE_PATH = $DataDirectory
        BACKUP_PATH = $backupDirectory
    }
    foreach ($entry in $requiredEnvironment.GetEnumerator()) {
        $matched = $false
        for ($index = 0; $index -lt $environmentLines.Count; $index++) {
            if ($environmentLines[$index] -match ('^\s*{0}\s*=' -f [regex]::Escape($entry.Key))) {
                $environmentLines[$index] = '{0}={1}' -f $entry.Key, $entry.Value
                $matched = $true
            }
        }
        if (-not $matched) { $environmentLines += '{0}={1}' -f $entry.Key, $entry.Value }
    }
    if (-not ($environmentLines | Where-Object { $_ -match '^\s*AUTH\s*=' })) {
        $environmentLines += 'AUTH=none'
    }
    $environmentLines | Set-Content -LiteralPath $EnvFile -Encoding ascii

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

    & $registerScript `
        -InstallRoot $current `
        -EnvFile $EnvFile `
        -StartScriptPath (Join-Path $current 'start-task-server.ps1')
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
