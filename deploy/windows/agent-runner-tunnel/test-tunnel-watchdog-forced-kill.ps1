[CmdletBinding()]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [ValidateRange(60, 240)]
    [int] $RecoveryDeadlineSeconds = 130,

    [string] $WatchdogTaskName = 'AgentRunner-TunnelWatchdog',

    [string] $KeeperTaskName = 'AgentRunner-TunnelKeeper',

    [string] $SshExecutable = 'ssh.exe',

    [string] $DevspaceDirectory = '',

    [string] $EvidenceDirectory = ''
)

$ErrorActionPreference = 'Stop'
$healthUrl = "http://127.0.0.1:$RemotePort/healthz"
$sshPath = (Get-Command $SshExecutable -ErrorAction Stop).Source
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if ([string]::IsNullOrWhiteSpace($DevspaceDirectory)) {
    $DevspaceDirectory = Split-Path -Parent $repositoryRoot
}
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path $env:TEMP 'agent-taskboard-tunnel-watchdog-results'
}
$watchdogLog = Join-Path $DevspaceDirectory '.tunnel-watchdog.log'
$evidencePath = Join-Path $EvidenceDirectory 'forced-kill-test.md'
$testStarted = [DateTime]::UtcNow
$recovered = $false
$injected = $false

function Invoke-RemoteCommand {
    param([Parameter(Mandatory)] [string] $Command)

    $output = & $sshPath -T -o BatchMode=yes -o ConnectTimeout=10 $SshTarget $Command 2>&1
    $exitCode = $LASTEXITCODE
    return [pscustomobject]@{
        Succeeded = $exitCode -eq 0
        ExitCode = $exitCode
        Output = (@($output) -join ' ') -replace "[\r\n]+", ' '
    }
}

function Test-TunnelHealth {
    $result = Invoke-RemoteCommand -Command "curl -sf --max-time 6 '$healthUrl' >/dev/null"
    return $result.Succeeded
}

function Wait-ScheduledTaskIdle {
    param(
        [Parameter(Mandatory)] [string] $TaskName,
        [Parameter(Mandatory)] [DateTime] $PreviousRunTime
    )

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $task = Get-ScheduledTask -TaskName $TaskName
        $info = Get-ScheduledTaskInfo -TaskName $TaskName
        if ($task.State -ne 'Running' -and $info.LastRunTime -gt $PreviousRunTime) { return }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Scheduled task '$TaskName' did not complete a new run within 30 seconds."
}

function Clear-TestListener {
    $command = @'
port='__REMOTE_PORT__'
pids="$(ss -H -ltnp "sport = :$port" 2>/dev/null | awk -v endpoint="127.0.0.1:$port" '$4 == endpoint { print }' | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u)"
[ -z "$pids" ] || kill $pids 2>/dev/null || true
'@.Replace('__REMOTE_PORT__', [string] $RemotePort)
    [void] (Invoke-RemoteCommand -Command $command)
}

New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null

try {
    Get-ScheduledTask -TaskName $WatchdogTaskName -ErrorAction Stop | Out-Null
    Get-ScheduledTask -TaskName $KeeperTaskName -ErrorAction Stop | Out-Null
    if (-not (Test-TunnelHealth)) {
        throw 'The tunnel must be healthy before the forced-kill test starts.'
    }

    $previousRunTime = (Get-ScheduledTaskInfo -TaskName $WatchdogTaskName).LastRunTime
    Start-ScheduledTask -TaskName $WatchdogTaskName
    Wait-ScheduledTaskIdle -TaskName $WatchdogTaskName -PreviousRunTime $previousRunTime
    Disable-ScheduledTask -TaskName $WatchdogTaskName | Out-Null

    $injectCommand = @'
port='__REMOTE_PORT__'
pids="$(ss -H -ltnp "sport = :$port" 2>/dev/null | awk -v endpoint="127.0.0.1:$port" '$4 == endpoint { print }' | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u)"
[ -z "$pids" ] || kill $pids 2>/dev/null || true
for wait_attempt in 1 2 3 4 5; do
  ss -H -ltn "sport = :$port" 2>/dev/null | awk -v endpoint="127.0.0.1:$port" '$4 == endpoint { found=1 } END { exit found ? 0 : 1 }' || break
  sleep 1
done
cd /tmp
nohup python3 -m http.server "$port" --bind 127.0.0.1 >/tmp/agent-taskboard-tunnel-watchdog-blocker.log 2>&1 </dev/null &
printf 'blocker_pid=%s\n' "$!"
'@.Replace('__REMOTE_PORT__', [string] $RemotePort)
    $injection = Invoke-RemoteCommand -Command $injectCommand
    if (-not $injection.Succeeded) {
        throw "Could not install the forced runner-side listener: $($injection.Output)"
    }
    $injected = $true
    Start-Sleep -Seconds 2
    if (Test-TunnelHealth) {
        throw 'The forced listener did not make the tunnel health probe fail.'
    }

    $recoveryStarted = [DateTime]::UtcNow
    Enable-ScheduledTask -TaskName $WatchdogTaskName | Out-Null
    Start-ScheduledTask -TaskName $WatchdogTaskName
    $deadline = $recoveryStarted.AddSeconds($RecoveryDeadlineSeconds)
    do {
        Start-Sleep -Seconds 3
        if (Test-TunnelHealth) {
            $recovered = $true
            break
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    if (-not $recovered) {
        throw "The tunnel did not recover within $RecoveryDeadlineSeconds seconds."
    }

    $elapsed = [Math]::Round(([DateTime]::UtcNow - $recoveryStarted).TotalSeconds, 1)
    $journal = if (Test-Path -LiteralPath $watchdogLog) {
        @(Get-Content -LiteralPath $watchdogLog | Select-Object -Last 30)
    } else { @('watchdog journal was not found') }
    $report = @(
        '# Tunnel watchdog forced-kill test',
        '',
        "- Started (UTC): $($testStarted.ToString('o'))",
        "- Fault: killed the real runner-side listener and bound a dummy HTTP listener to 127.0.0.1:$RemotePort.",
        "- Recovery: PASS in $elapsed seconds (deadline: $RecoveryDeadlineSeconds seconds).",
        '- Verification: the runner-side curl returned success through the replacement reverse tunnel.',
        '',
        '## Watchdog journal tail',
        '',
        '```text'
    ) + $journal + @('```', '')
    Set-Content -LiteralPath $evidencePath -Value $report -Encoding utf8
    Write-Output "PASS: tunnel recovered in $elapsed seconds; evidence: $evidencePath"
}
finally {
    Enable-ScheduledTask -TaskName $WatchdogTaskName -ErrorAction SilentlyContinue | Out-Null
    if ($injected -and -not $recovered) {
        Clear-TestListener
        Start-ScheduledTask -TaskName $KeeperTaskName -ErrorAction SilentlyContinue
    }
    Start-ScheduledTask -TaskName $WatchdogTaskName -ErrorAction SilentlyContinue
}
