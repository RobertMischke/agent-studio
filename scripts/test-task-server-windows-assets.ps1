[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AssetRoot,
    [Parameter(Mandatory)] [string] $TunnelRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$library = Join-Path $AssetRoot 'task-server-lib.ps1'
$start = Join-Path $AssetRoot 'start-task-server.ps1'
$backup = Join-Path $AssetRoot 'backup-task-server.ps1'
$registration = Join-Path $AssetRoot 'register-task-server.ps1'
$backupRegistration = Join-Path $AssetRoot 'register-task-server-backup.ps1'
$keeper = Join-Path $TunnelRoot 'tunnel-keeper.ps1'
$keeperRegistration = Join-Path $TunnelRoot 'register-tunnel-keeper.ps1'

function Assert-True {
    param(
        [Parameter(Mandatory)] [bool] $Condition,
        [Parameter(Mandatory)] [string] $Message
    )
    if (-not $Condition) { throw "Dry run failed: $Message" }
}

foreach ($script in @($library, $start, $backup, $registration, $backupRegistration, $keeper, $keeperRegistration)) {
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors)
    Assert-True -Condition ($errors.Count -eq 0) -Message "$script does not parse: $($errors -join '; ')"
}
Write-Host 'parse: all Windows Task Server and tunnel scripts parse.'

$startParameters = (Get-Command $start).Parameters
foreach ($name in @('InstallRoot', 'EnvironmentFile', 'StateDirectory', 'InstanceName')) {
    Assert-True -Condition $startParameters.ContainsKey($name) -Message "start-task-server.ps1 lacks -$name"
}
$registrationText = Get-Content -LiteralPath $registration -Raw
foreach ($name in @('InstallRoot', 'EnvironmentFile', 'StateDirectory')) {
    Assert-True -Condition ($registrationText -match "'-$name'") `
        -Message "register-task-server.ps1 does not forward -$name"
}
$backupParameters = (Get-Command $backup).Parameters
$backupRegistrationText = Get-Content -LiteralPath $backupRegistration -Raw
foreach ($name in @('InstallRoot', 'EnvironmentFile', 'StateDirectory', 'Name')) {
    Assert-True -Condition $backupParameters.ContainsKey($name) -Message "backup-task-server.ps1 lacks -$name"
    Assert-True -Condition ($backupRegistrationText -match "'-$name'") `
        -Message "register-task-server-backup.ps1 does not forward -$name"
}
Write-Host 'contract: both registration scripts forward only parameters their target script declares.'

foreach ($script in @($keeper, $keeperRegistration)) {
    $parameters = (Get-Command $script).Parameters
    Assert-True -Condition $parameters.ContainsKey('OrchestratorPort') `
        -Message "$script lacks -OrchestratorPort"
    Assert-True -Condition (-not $parameters.ContainsKey('TaskServerPort')) `
        -Message "$script still declares -TaskServerPort as a parameter name"
    Assert-True -Condition ($parameters['OrchestratorPort'].Aliases -contains 'TaskServerPort') `
        -Message "$script drops the -TaskServerPort alias and breaks a registered scheduled task"
}
$keeperAst = [System.Management.Automation.Language.Parser]::ParseFile($keeper, [ref]$null, [ref]$null)
$keeperDefault = $keeperAst.ParamBlock.Parameters |
    Where-Object { $_.Name.VariablePath.UserPath -eq 'OrchestratorPort' } |
    ForEach-Object { $_.DefaultValue.Extent.Text }
Assert-True -Condition ($keeperDefault -eq '5031') `
    -Message "tunnel-keeper.ps1 changed the forwarded port default to $keeperDefault"
Write-Host 'rename: -OrchestratorPort keeps the 5031 default and the -TaskServerPort alias.'

. $library

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("task-server-assets-" + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
try {
    $environmentFile = Join-Path $sandbox 'server.env'
    @(
        '# comment line',
        '',
        'LISTEN_URL=http://127.0.0.1:5071',
        'STORE_PATH=C:\ProgramData\AgentOrchestrator\data',
        'export AUTH=bearer',
        'TaskServer__MinimumLeaseSeconds="30"'
    ) | Set-Content -LiteralPath $environmentFile -Encoding utf8

    $imported = Import-ServerEnvironmentFile -Path $environmentFile
    Assert-True -Condition ($imported.Count -eq 4) -Message "expected 4 imported settings, got $($imported.Count)"
    Assert-True -Condition ([Environment]::GetEnvironmentVariable('LISTEN_URL') -eq 'http://127.0.0.1:5071') `
        -Message 'LISTEN_URL was not imported'
    Assert-True -Condition ([Environment]::GetEnvironmentVariable('STORE_PATH') -eq 'C:\ProgramData\AgentOrchestrator\data') `
        -Message 'a Windows path value was altered during import'
    Assert-True -Condition ([Environment]::GetEnvironmentVariable('AUTH') -eq 'bearer') `
        -Message 'an export-prefixed assignment was not imported'
    Assert-True -Condition ([Environment]::GetEnvironmentVariable('TaskServer__MinimumLeaseSeconds') -eq '30') `
        -Message 'a quoted value kept its quotes'

    $malformed = Join-Path $sandbox 'malformed.env'
    'LISTEN URL http://127.0.0.1:5071' | Set-Content -LiteralPath $malformed -Encoding utf8
    $rejected = $false
    try { Import-ServerEnvironmentFile -Path $malformed | Out-Null } catch { $rejected = $true }
    Assert-True -Condition $rejected -Message 'a malformed bootstrap line was accepted'

    $missing = $false
    try { Import-ServerEnvironmentFile -Path (Join-Path $sandbox 'absent.env') | Out-Null } catch { $missing = $true }
    Assert-True -Condition $missing -Message 'a missing bootstrap file was accepted'
    Write-Host 'bootstrap: server.env import handles comments, exports, quotes, Windows paths, and rejects bad input.'

    $installRoot = Join-Path $sandbox 'current'
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    $unresolved = $false
    try { Resolve-TaskServerExecutable -InstallRoot $installRoot | Out-Null } catch { $unresolved = $true }
    Assert-True -Condition $unresolved -Message 'a missing task-server.exe was accepted'
    Set-Content -LiteralPath (Join-Path $installRoot 'task-server.exe') -Value 'stub' -Encoding utf8
    $resolved = Resolve-TaskServerExecutable -InstallRoot $installRoot
    Assert-True -Condition ([IO.Path]::GetFileName($resolved) -eq 'task-server.exe') `
        -Message 'the published executable was not resolved from the install root'

    $stateDirectory = Join-Path $sandbox 'state'
    Write-ServiceEvent -StateDirectory $stateDirectory -Line 'event=dry_run'
    $statePath = Write-ServiceState -StateDirectory $stateDirectory -Name 'state.json' -State @{
        status = 'running'
        processId = 4321
    }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Assert-True -Condition ($state.status -eq 'running' -and $state.processId -eq 4321) `
        -Message 'the service state file did not round-trip'
    Assert-True -Condition (-not (Test-Path -LiteralPath "$statePath.tmp")) `
        -Message 'the atomic state write left its temporary file behind'
    Assert-True -Condition ((Get-Content -LiteralPath (Join-Path $stateDirectory 'events.log')) -match 'event=dry_run') `
        -Message 'the event log did not record the dry-run line'
    Write-Host 'state: executable resolution, event log, and atomic state file behave as documented.'
}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Dry run passed.'
