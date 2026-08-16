[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $SourceCheckout,

    [Parameter(Mandatory)]
    [string] $DevspacePath,

    [string] $TaskName = 'AgentStudio-TaskServer',

    [string] $ListenUrl = 'http://127.0.0.1:5071',

    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name,

    [switch] $NoStart
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceCheckout).Path
$devspace = (Resolve-Path -LiteralPath $DevspacePath).Path
$project = Join-Path $source 'task-server\TaskServer.csproj'
$profile = Join-Path $source 'task-server\Properties\PublishProfiles\win-x64.pubxml'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Task Server project is missing: $project" }
if (-not (Test-Path -LiteralPath $profile -PathType Leaf)) { throw "Windows publish profile is missing: $profile" }

$serviceRoot = Join-Path $devspace 'services\task-server'
$releasesRoot = Join-Path $serviceRoot 'releases'
$dataRoot = Join-Path $devspace 'task-server-data'
$backupRoot = Join-Path $dataRoot 'backups'
$stagingRoot = Join-Path $serviceRoot ("staging\" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $releasesRoot, $dataRoot, $backupRoot, $stagingRoot -Force | Out-Null

$sha = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[0-9a-f]{40}$') {
    throw "Could not resolve the source checkout commit."
}
$releasePath = Join-Path $releasesRoot $sha

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -ne $existing -and $existing.State -eq 'Running') {
    throw "Scheduled task '$TaskName' is running. Stop it through task-server-control.ps1 before installing a release."
}

try {
    if (-not (Test-Path -LiteralPath $releasePath -PathType Container)) {
        & dotnet publish $project '-p:PublishProfile=win-x64' '-o' $stagingRoot
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
        $published = Join-Path $stagingRoot 'task-server.exe'
        if (-not (Test-Path -LiteralPath $published -PathType Leaf)) {
            throw "Published Task Server executable is missing: $published"
        }
        $version = (& $published '--version').Trim()
        if ($LASTEXITCODE -ne 0 -or $version -notmatch [Regex]::Escape($sha)) {
            throw "Published Task Server identity does not contain commit $sha. Observed: $version"
        }
        Move-Item -LiteralPath $stagingRoot -Destination $releasePath
    }

    $runnerSource = Join-Path $source 'deploy\windows\task-server\run-task-server.ps1'
    if (-not (Test-Path -LiteralPath $runnerSource -PathType Leaf)) {
        throw "Task Server service runner is missing from the source checkout: $runnerSource"
    }
    $runnerInstalled = Join-Path $serviceRoot 'run-task-server.ps1'
    Copy-Item -LiteralPath $runnerSource -Destination $runnerInstalled -Force

    $environmentPath = Join-Path $serviceRoot 'server.env'
    $managedEnvironment = [ordered]@{
        LISTEN_URL = $ListenUrl
        STORE_PATH = $dataRoot
        BACKUP_PATH = $backupRoot
        AUTH = 'none'
    }
    if (Test-Path -LiteralPath $environmentPath -PathType Leaf) {
        foreach ($line in Get-Content -LiteralPath $environmentPath) {
            $separator = $line.IndexOf('=')
            if ($separator -lt 1) { continue }
            $name = $line.Substring(0, $separator).Trim()
            if (-not $managedEnvironment.Contains($name)) {
                $managedEnvironment[$name] = $line.Substring($separator + 1)
            }
        }
    }
    $environmentTemp = "$environmentPath.tmp"
    $managedEnvironment.GetEnumerator() | ForEach-Object { '{0}={1}' -f $_.Key, $_.Value } |
        Set-Content -LiteralPath $environmentTemp -Encoding UTF8
    Move-Item -LiteralPath $environmentTemp -Destination $environmentPath -Force

    $pointerPath = Join-Path $serviceRoot 'current-release.txt'
    $pointerTemp = "$pointerPath.tmp"
    Set-Content -LiteralPath $pointerTemp -Value $releasePath -Encoding ASCII
    Move-Item -LiteralPath $pointerTemp -Destination $pointerPath -Force

    $powerShell = (Get-Command 'powershell.exe' -ErrorAction Stop).Source
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -ServiceRoot "{1}"' -f `
        $runnerInstalled.Replace('"', '""'), $serviceRoot.Replace('"', '""')
    $action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId $RunAsUser -LogonType S4U -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -StartWhenAvailable `
        -RestartCount 10 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries

    if ($PSCmdlet.ShouldProcess($TaskName, "Install Task Server $sha and register an S4U scheduled task")) {
        Register-ScheduledTask `
            -TaskName $TaskName `
            -Description 'Supervises the standalone Agent Studio Task Server independently of interactive sessions.' `
            -Action $action `
            -Trigger $trigger `
            -Principal $principal `
            -Settings $settings `
            -Force | Out-Null
        if (-not $NoStart) {
            & (Join-Path $PSScriptRoot 'task-server-control.ps1') `
                -Action Start `
                -TaskName $TaskName `
                -ReadyUrl ($ListenUrl.TrimEnd('/') + '/readyz')
        }
    }

    [pscustomobject]@{
        Commit = $sha
        ReleasePath = $releasePath
        DataPath = $dataRoot
        BackupPath = $backupRoot
        ListenUrl = $ListenUrl
        TaskName = $TaskName
        Principal = $RunAsUser
        LogonType = 'S4U'
        Started = -not $NoStart
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
