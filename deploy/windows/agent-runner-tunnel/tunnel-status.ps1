[CmdletBinding()]
param(
    [string] $KeeperTaskName = 'AgentRunner-TunnelKeeper',
    [string] $WatchdogTaskName = 'AgentRunner-TunnelWatchdog',
    [string] $KeeperStateDirectory = (Join-Path $env:LOCALAPPDATA 'AgentTaskboard\tunnel-keeper'),
    [string] $DevspacePath,
    [string] $OperatorAlarmPath
)

# Read-only status probe. Registers no task and requires no elevation: this is
# the script the product backend polls to render "registered / running / last
# heal" without an administrator prompt.
$ErrorActionPreference = 'Stop'

if (-not $DevspacePath) {
    $repositoryRoot = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
    $DevspacePath = Split-Path $repositoryRoot -Parent
}
if (-not $OperatorAlarmPath) {
    $OperatorAlarmPath = Join-Path $DevspacePath '.operator-alarm.log'
}
$watchdogLogPath = Join-Path $DevspacePath '.tunnel-watchdog.log'
$keeperStatePath = Join-Path $KeeperStateDirectory 'state.json'

function Get-TaskSummary {
    param([Parameter(Mandatory)] [string] $TaskName)

    $summary = [ordered]@{
        taskName = $TaskName
        registered = $false
        state = $null
        lastRunTime = $null
        lastTaskResult = $null
        nextRunTime = $null
    }
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $task) { return $summary }
    $summary.registered = $true
    $summary.state = $task.State.ToString()
    $info = $task | Get-ScheduledTaskInfo -ErrorAction SilentlyContinue
    if ($info) {
        $summary.lastRunTime = if ($info.LastRunTime) { $info.LastRunTime.ToUniversalTime().ToString('o') } else { $null }
        $summary.lastTaskResult = $info.LastTaskResult
        $summary.nextRunTime = if ($info.NextRunTime) { $info.NextRunTime.ToUniversalTime().ToString('o') } else { $null }
    }
    return $summary
}

function Get-KeeperHealth {
    $health = [ordered]@{
        status = $null
        message = $null
        observedAt = $null
        repairAttempts = $null
    }
    if (-not (Test-Path -LiteralPath $keeperStatePath)) { return $health }
    try {
        $state = Get-Content -LiteralPath $keeperStatePath -Raw | ConvertFrom-Json
        $health.status = $state.status
        $health.message = $state.message
        $health.observedAt = $state.observedAt
        $health.repairAttempts = $state.repairAttempts
    }
    catch {
        $health.message = "keeper state.json could not be parsed: $($_.Exception.Message)"
    }
    return $health
}

function ConvertFrom-JournalLine {
    param([Parameter(Mandatory)] [string] $Line)

    # Lines look like: 2026-08-18T09:00:01Z event=heal_succeeded health_url=...
    if ($Line -notmatch '^(?<ts>\S+)\s+event=(?<event>\S+)') { return $null }
    return [ordered]@{ timestamp = $Matches.ts; event = $Matches.event; line = $Line.Trim() }
}

function Get-WatchdogHealth {
    $health = [ordered]@{
        lastHealSucceededAt = $null
        lastHealFailedAt = $null
        lastProbeFailedAt = $null
        lastEvent = $null
        lastEventAt = $null
    }
    if (-not (Test-Path -LiteralPath $watchdogLogPath)) { return $health }
    $tail = Get-Content -LiteralPath $watchdogLogPath -Tail 200 -ErrorAction SilentlyContinue
    foreach ($rawLine in $tail) {
        $parsed = ConvertFrom-JournalLine -Line $rawLine
        if (-not $parsed) { continue }
        $health.lastEvent = $parsed.event
        $health.lastEventAt = $parsed.timestamp
        switch ($parsed.event) {
            'heal_succeeded' { $health.lastHealSucceededAt = $parsed.timestamp }
            'heal_failed' { $health.lastHealFailedAt = $parsed.timestamp }
            'probe_failed' { $health.lastProbeFailedAt = $parsed.timestamp }
        }
    }
    return $health
}

function Get-AlarmActive {
    param([Parameter(Mandatory)] $WatchdogHealth)

    if (-not (Test-Path -LiteralPath $OperatorAlarmPath)) { return $false }
    $tail = Get-Content -LiteralPath $OperatorAlarmPath -Tail 200 -ErrorAction SilentlyContinue
    $lastAlarmAt = $null
    foreach ($rawLine in $tail) {
        if ($rawLine -notmatch '^(?<ts>\S+)\s+.*source=tunnel-watchdog\s+severity=alarm') { continue }
        $lastAlarmAt = $Matches.ts
    }
    if (-not $lastAlarmAt) { return $false }
    if (-not $WatchdogHealth.lastHealSucceededAt) { return $true }
    return ([DateTime]::Parse($lastAlarmAt) -gt [DateTime]::Parse($WatchdogHealth.lastHealSucceededAt))
}

$keeperTask = Get-TaskSummary -TaskName $KeeperTaskName
$watchdogTask = Get-TaskSummary -TaskName $WatchdogTaskName
$keeperHealth = Get-KeeperHealth
$watchdogHealth = Get-WatchdogHealth

$result = [ordered]@{
    observedAt = [DateTime]::UtcNow.ToString('o')
    keeper = [ordered]@{
        task = $keeperTask
        health = $keeperHealth
    }
    watchdog = [ordered]@{
        task = $watchdogTask
        health = $watchdogHealth
        alarmActive = (Get-AlarmActive -WatchdogHealth $watchdogHealth)
    }
}
($result | ConvertTo-Json -Depth 8)
