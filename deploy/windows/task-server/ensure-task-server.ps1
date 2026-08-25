[CmdletBinding()]
param(
    [string] $TaskName = 'AgentOrchestrator-TaskServer'
)

$ErrorActionPreference = 'Stop'
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
if ($task.Principal.LogonType -ne 'S4U') {
    throw "Scheduled Task '$TaskName' is not supervised under an S4U principal. Re-register it before rollout."
}
if ($task.State -ne 'Running') {
    Start-ScheduledTask -TaskName $TaskName
}
Get-ScheduledTask -TaskName $TaskName | Get-ScheduledTaskInfo
