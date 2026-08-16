[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Restart', 'Status')]
    [string] $Action = 'Status',

    [string] $TaskName = 'AgentStudio-TaskServer',

    [string] $ReadyUrl = 'http://127.0.0.1:5071/readyz',

    [ValidateRange(1, 300)]
    [int] $TimeoutSeconds = 60,

    [switch] $AllowMissing
)

$ErrorActionPreference = 'Stop'
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -eq $task) {
    if ($AllowMissing -or $Action -eq 'Stop') { return }
    throw "Task Server scheduled task is not registered: $TaskName"
}

function Wait-TaskState {
    param([Parameter(Mandatory)] [string] $Expected)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $state = (Get-ScheduledTask -TaskName $TaskName).State.ToString()
        if ($state -eq $Expected) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Scheduled task '$TaskName' did not reach state '$Expected' within $TimeoutSeconds seconds."
}

function Wait-Ready {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $ReadyUrl -TimeoutSec 5
            if ($response.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Task Server did not become ready at $ReadyUrl within $TimeoutSeconds seconds."
}

switch ($Action) {
    'Start' {
        if ($task.State -ne 'Running') { Start-ScheduledTask -TaskName $TaskName }
        Wait-Ready
    }
    'Stop' {
        if ($task.State -eq 'Running') { Stop-ScheduledTask -TaskName $TaskName }
        Wait-TaskState -Expected 'Ready'
    }
    'Restart' {
        if ($task.State -eq 'Running') {
            Stop-ScheduledTask -TaskName $TaskName
            Wait-TaskState -Expected 'Ready'
        }
        Start-ScheduledTask -TaskName $TaskName
        Wait-Ready
    }
    'Status' {
        [pscustomobject]@{
            TaskName = $TaskName
            State = (Get-ScheduledTask -TaskName $TaskName).State.ToString()
            ReadyUrl = $ReadyUrl
        }
    }
}
