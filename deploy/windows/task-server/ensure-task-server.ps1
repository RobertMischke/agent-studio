[CmdletBinding()]
param(
    [string] $TaskName = 'AgentOrchestrator-TaskServer',
    [string] $ReadyUrl = 'http://127.0.0.1:5071/readyz',
    [ValidateRange(1, 300)]
    [int] $TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
try {
    $response = Invoke-WebRequest -UseBasicParsing -Uri $ReadyUrl -TimeoutSec 3
    if ($response.StatusCode -eq 200) { exit 0 }
} catch { }

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
if ($task.State -eq 'Running') {
    Stop-ScheduledTask -TaskName $TaskName
    do {
        Start-Sleep -Milliseconds 250
        $task = Get-ScheduledTask -TaskName $TaskName
    } while ($task.State -eq 'Running')
}
Start-ScheduledTask -TaskName $TaskName

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
do {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $ReadyUrl -TimeoutSec 3
        if ($response.StatusCode -eq 200) { exit 0 }
    } catch { }
    Start-Sleep -Seconds 2
} while ([DateTime]::UtcNow -lt $deadline)

throw "Task Server did not become ready at $ReadyUrl within $TimeoutSeconds seconds."
