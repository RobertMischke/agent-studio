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
$keeperExitLog = Join-Path $env:LOCALAPPDATA 'AgentTaskboard\tunnel-keeper\ssh-exit.log'
$evidencePath = Join-Path $EvidenceDirectory 'forced-kill-test.md'
$testStarted = [DateTime]::UtcNow
$recovered = $false
$injected = $false

function Invoke-RemoteCommand {
    param([Parameter(Mandatory)] [string] $Command)

    $output = & $sshPath `
        -T -o BatchMode=yes -o ConnectTimeout=10 -o ConnectionAttempts=1 `
        $SshTarget $Command 2>&1
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
pid_file='/tmp/agent-taskboard-tunnel-watchdog-blocker-__REMOTE_PORT__.pid'
if [ -r "$pid_file" ]; then
  pid="$(cat "$pid_file" 2>/dev/null || true)"
  case "$pid" in
    ''|*[!0-9]*) ;;
    *) kill "$pid" 2>/dev/null || true ;;
  esac
  rm -f "$pid_file"
fi
'@.Replace('__REMOTE_PORT__', [string] $RemotePort)
    [void] (Invoke-RemoteCommand -Command $command)
}

New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null

try {
    $watchdogTask = Get-ScheduledTask -TaskName $WatchdogTaskName -ErrorAction Stop
    $keeperTask = Get-ScheduledTask -TaskName $KeeperTaskName -ErrorAction Stop
    if ($watchdogTask.Principal.LogonType -ne 'S4U') {
        throw "Scheduled task '$WatchdogTaskName' must use the S4U logon type."
    }
    if ($keeperTask.Principal.LogonType -ne 'S4U') {
        throw "Scheduled task '$KeeperTaskName' must use the S4U logon type."
    }
    if (-not (Test-TunnelHealth)) {
        throw 'The tunnel must be healthy before the forced-kill test starts.'
    }

    # Reset the two-strike counter, then hold the task while the fault is
    # installed. The first post-fault run is manual; the second must come from
    # the registered one-minute trigger.
    $previousRunTime = (Get-ScheduledTaskInfo -TaskName $WatchdogTaskName).LastRunTime
    Start-ScheduledTask -TaskName $WatchdogTaskName
    Wait-ScheduledTaskIdle -TaskName $WatchdogTaskName -PreviousRunTime $previousRunTime
    Disable-ScheduledTask -TaskName $WatchdogTaskName | Out-Null
    Stop-ScheduledTask -TaskName $KeeperTaskName -ErrorAction SilentlyContinue

    $injectCommand = @'
port='__REMOTE_PORT__'
endpoint="127.0.0.1:$port"
lines="$(ss -H -ltnpe "sport = :$port" 2>/dev/null || true)"
matching="$(printf '%s\n' "$lines" | awk -v endpoint="$endpoint" '$4 == endpoint { print }')"
[ -n "$matching" ] || { printf 'forced-kill=no-listener\n'; exit 5; }
pids="$(printf '%s\n' "$matching" | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u || true)"
scopes="$(printf '%s\n' "$matching" | grep -o 'cgroup:[^ ]*' | cut -d: -f2- | sort -u || true)"
own_uid="$(id -u)"
session_scopes="$(printf '%s\n' "$scopes" | grep -E "^/user[.]slice/user-$own_uid[.]slice/session-[^/]+[.]scope$" || true)"
for status in /proc/[0-9]*/status; do
  [ -r "$status" ] || continue
  pid="${status#/proc/}"
  pid="${pid%/status}"
  uid="$(awk '/^Uid:/ { print $2; exit }' "$status" 2>/dev/null || true)"
  [ "$uid" = "$own_uid" ] || continue
  [ -r "/proc/$pid/cgroup" ] || continue
  while IFS= read -r record; do
    process_scope="${record#*::}"
    if printf '%s\n' "$session_scopes" | grep -Fqx -- "$process_scope"; then
      pids="${pids}${pids:+
}$pid"
      break
    fi
  done < "/proc/$pid/cgroup"
done
pids="$(printf '%s\n' "$pids" | grep -E '^[0-9]+$' | sort -u || true)"
[ -n "$pids" ] || { printf 'forced-kill=unresolved\n'; exit 5; }
kill $pids 2>/dev/null || true
sleep 2
for pid in $pids; do
  kill -0 "$pid" 2>/dev/null && kill -9 "$pid" 2>/dev/null || true
done
for wait_attempt in 1 2 3 4 5; do
  ss -H -ltn "sport = :$port" 2>/dev/null | awk -v endpoint="$endpoint" '$4 == endpoint { found=1 } END { exit found ? 0 : 1 }' || break
  sleep 1
done
ss -H -ltn "sport = :$port" 2>/dev/null | awk -v endpoint="$endpoint" '$4 == endpoint { found=1 } END { exit found ? 0 : 1 }' && { printf 'forced-kill=listener-remained\n'; exit 5; }
cd /tmp
pid_file="/tmp/agent-taskboard-tunnel-watchdog-blocker-$port.pid"
nohup python3 -m http.server "$port" --bind 127.0.0.1 >/tmp/agent-taskboard-tunnel-watchdog-blocker.log 2>&1 </dev/null &
printf '%s\n' "$!" > "$pid_file"
printf 'forced-kill=installed old_pids=%s blocker_pid=%s\n' "$(printf '%s' "$pids" | tr '\n' ',')" "$!"
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
    $preStrikeRunTime = (Get-ScheduledTaskInfo -TaskName $WatchdogTaskName).LastRunTime
    Start-ScheduledTask -TaskName $WatchdogTaskName
    Wait-ScheduledTaskIdle -TaskName $WatchdogTaskName -PreviousRunTime $preStrikeRunTime
    $firstStrikeRunTime = (Get-ScheduledTaskInfo -TaskName $WatchdogTaskName).LastRunTime
    if (Test-TunnelHealth) {
        throw 'The first watchdog strike unexpectedly restored the tunnel before the configured threshold.'
    }

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
    Wait-ScheduledTaskIdle -TaskName $WatchdogTaskName -PreviousRunTime $firstStrikeRunTime
    $healRunTime = (Get-ScheduledTaskInfo -TaskName $WatchdogTaskName).LastRunTime
    if ($healRunTime -le $firstStrikeRunTime) {
        throw 'The tunnel recovered without a second watchdog task run; scheduled-trigger proof is missing.'
    }

    $elapsed = [Math]::Round(([DateTime]::UtcNow - $recoveryStarted).TotalSeconds, 1)
    $journal = if (Test-Path -LiteralPath $watchdogLog) {
        @(Get-Content -LiteralPath $watchdogLog | Select-Object -Last 40)
    } else { @('watchdog journal was not found') }
    $sshExit = if (Test-Path -LiteralPath $keeperExitLog) {
        @(Get-Content -LiteralPath $keeperExitLog | Select-Object -Last 20)
    } else { @('keeper SSH exit log was not found') }
    $report = @(
        '# Tunnel watchdog forced-kill test',
        '',
        "- Started (UTC): $($testStarted.ToString('o'))",
        "- Fault: killed the real runner-side listener and bound a dummy HTTP listener to 127.0.0.1:$RemotePort.",
        "- Injection: $($injection.Output)",
        "- Recovery: PASS in $elapsed seconds (deadline: $RecoveryDeadlineSeconds seconds).",
        '- Trigger proof: the first failed probe was started explicitly; the registered one-minute trigger supplied the second strike and heal.',
        "- First strike task run: $($firstStrikeRunTime.ToUniversalTime().ToString('o')).",
        "- Heal task run: $($healRunTime.ToUniversalTime().ToString('o')).",
        '- Verification: the runner-side curl returned success through the replacement reverse tunnel.',
        "- Watchdog task logon type: $($watchdogTask.Principal.LogonType).",
        "- Keeper task logon type: $($keeperTask.Principal.LogonType).",
        '',
        '## Watchdog journal tail',
        '',
        '```text'
    ) + $journal + @(
        '```',
        '',
        '## Keeper SSH exit log tail',
        '',
        '```text'
    ) + $sshExit + @('```', '')
    Set-Content -LiteralPath $evidencePath -Value $report -Encoding utf8
    Write-Output "PASS: tunnel recovered in $elapsed seconds; evidence: $evidencePath"
}
catch {
    $failure = $_.Exception.Message
    $journal = if (Test-Path -LiteralPath $watchdogLog) {
        @(Get-Content -LiteralPath $watchdogLog | Select-Object -Last 40)
    } else { @('watchdog journal was not found') }
    $sshExit = if (Test-Path -LiteralPath $keeperExitLog) {
        @(Get-Content -LiteralPath $keeperExitLog | Select-Object -Last 20)
    } else { @('keeper SSH exit log was not found') }
    $report = @(
        '# Tunnel watchdog forced-kill test',
        '',
        "- Started (UTC): $($testStarted.ToString('o'))",
        '- Recovery: FAIL.',
        "- Failure: $failure",
        '',
        '## Watchdog journal tail',
        '',
        '```text'
    ) + $journal + @(
        '```',
        '',
        '## Keeper SSH exit log tail',
        '',
        '```text'
    ) + $sshExit + @('```', '')
    Set-Content -LiteralPath $evidencePath -Value $report -Encoding utf8
    throw
}
finally {
    Enable-ScheduledTask -TaskName $WatchdogTaskName -ErrorAction SilentlyContinue | Out-Null
    if ($injected -and -not $recovered) {
        Clear-TestListener
        Start-ScheduledTask -TaskName $KeeperTaskName -ErrorAction SilentlyContinue
    }
    Start-ScheduledTask -TaskName $WatchdogTaskName -ErrorAction SilentlyContinue
}
