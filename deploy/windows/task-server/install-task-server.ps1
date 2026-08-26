[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $SourceRoot,
    [Parameter(Mandatory)] [string] $Version,
    [string] $InstallBase = 'C:\AgentOrchestrator',
    [string] $EnvFile = 'C:\ProgramData\AgentOrchestrator\server.env',
    [Parameter(Mandatory)] [string] $DataDirectory,
    [Parameter(Mandatory)] [string] $StudioConfig,
    [string] $ListenUrl = 'http://127.0.0.1:5071',
    [string] $TaskName = 'AgentOrchestrator-TaskServer',
    [ValidateRange(10, 300)] [int] $ReadyTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'

function Set-EnvironmentValue {
    param(
        [Parameter(Mandatory)] [string[]] $Lines,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Value
    )
    $replacement = "$Name=$Value"
    $found = $false
    $updated = foreach ($line in $Lines) {
        if ($line -match "^$([regex]::Escape($Name))=") {
            $found = $true
            $replacement
        } else {
            $line
        }
    }
    if (-not $found) { $updated += $replacement }
    return @($updated)
}

function Wait-Ready {
    param([Parameter(Mandatory)] [string] $BaseUrl)
    $deadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
    $lastFailure = 'no response'
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 -Uri "$($BaseUrl.TrimEnd('/'))/readyz"
            if ($response.StatusCode -eq 200) { return }
            $lastFailure = "HTTP $($response.StatusCode)"
        } catch {
            $lastFailure = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Task Server did not become ready at $BaseUrl within $ReadyTimeoutSeconds seconds: $lastFailure"
}

$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$project = Join-Path $source 'task-server\TaskServer.csproj'
if (-not (Test-Path -LiteralPath $project)) {
    throw "Task Server project not found: $project"
}
try {
    $listenUri = [Uri] $ListenUrl
} catch {
    throw "ListenUrl must be an absolute URL: $ListenUrl"
}
if (-not $listenUri.IsAbsoluteUri -or $listenUri.Scheme -notin @('http', 'https')) {
    throw "ListenUrl must be an absolute HTTP or HTTPS URL: $ListenUrl"
}

$installBaseFull = [IO.Path]::GetFullPath($InstallBase)
$versionName = $Version -replace '[^A-Za-z0-9._-]', '_'
$versionRoot = [IO.Path]::GetFullPath((Join-Path $installBaseFull $versionName))
if (-not $versionRoot.StartsWith($installBaseFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Version directory escaped the install base: $versionRoot"
}
$current = Join-Path $installBaseFull 'current'
$supervisorRoot = Join-Path $installBaseFull 'supervisor'
$publishRoot = Join-Path ([IO.Path]::GetTempPath()) "agent-task-server-$([Guid]::NewGuid().ToString('N'))"

if ($PSCmdlet.ShouldProcess($versionRoot, 'Publish and install Task Server release')) {
    New-Item -ItemType Directory -Force -Path $installBaseFull, $supervisorRoot, $DataDirectory | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $versionRoot 'task-server.exe'))) {
        try {
            & dotnet publish $project -p:PublishProfile=win-x64 -o $publishRoot
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
            New-Item -ItemType Directory -Force -Path $versionRoot | Out-Null
            Copy-Item -Path (Join-Path $publishRoot '*') -Destination $versionRoot -Recurse -Force
        } finally {
            if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
        }
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'start-task-server.ps1') -Destination $supervisorRoot -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'register-task-server.ps1') -Destination $supervisorRoot -Force

    $envDirectory = Split-Path -Parent $EnvFile
    New-Item -ItemType Directory -Force -Path $envDirectory | Out-Null
    $lines = if (Test-Path -LiteralPath $EnvFile) { @(Get-Content -LiteralPath $EnvFile) } else { @() }
    $lines = Set-EnvironmentValue $lines 'LISTEN_URL' $ListenUrl
    $lines = Set-EnvironmentValue $lines 'STORE_PATH' ([IO.Path]::GetFullPath($DataDirectory))
    $lines = Set-EnvironmentValue $lines 'BACKUP_PATH' ([IO.Path]::GetFullPath((Join-Path $DataDirectory 'backups')))
    if (-not ($lines | Where-Object { $_ -match '^AUTH=' })) {
        $lines += 'AUTH=none'
    }
    Set-Content -LiteralPath $EnvFile -Value $lines -Encoding utf8

    if (-not (Test-Path -LiteralPath $StudioConfig)) {
        $example = Join-Path $source 'backend\appsettings.Local.json.example'
        Copy-Item -LiteralPath $example -Destination $StudioConfig
    }
    $configuration = Get-Content -LiteralPath $StudioConfig -Raw | ConvertFrom-Json
    if ($null -eq $configuration.TaskServer) {
        $configuration | Add-Member -NotePropertyName TaskServer -NotePropertyValue ([pscustomobject]@{})
    }
    if ($null -eq $configuration.TaskServer.PSObject.Properties['BaseUrl']) {
        $configuration.TaskServer | Add-Member -NotePropertyName BaseUrl -NotePropertyValue $ListenUrl
    } else {
        $configuration.TaskServer.BaseUrl = $ListenUrl
    }
    $configuration | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $StudioConfig -Encoding utf8

    $existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -ne $existingTask) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $current) {
        $currentItem = Get-Item -LiteralPath $current -Force
        if (($currentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            throw "Refusing to replace non-junction current path: $current"
        }
        Remove-Item -LiteralPath $current -Force
    }
    New-Item -ItemType Junction -Path $current -Target $versionRoot | Out-Null

    & (Join-Path $supervisorRoot 'register-task-server.ps1') `
        -InstallRoot $current `
        -EnvFile $EnvFile `
        -TaskName $TaskName `
        -StartScriptPath (Join-Path $supervisorRoot 'start-task-server.ps1')
    Wait-Ready -BaseUrl $ListenUrl
    Write-Host "Task Server release $Version is supervised and ready at $ListenUrl."
}
