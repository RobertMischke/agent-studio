[CmdletBinding()]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [string] $SshExecutable = 'ssh.exe',

    [string] $WatchdogTaskName = 'AgentRunner-TunnelWatchdog',

    [string] $KeeperTaskName = 'AgentRunner-TunnelKeeper',

    [string] $DevspaceDirectory = 'C:\Projects\agent-taskboard-devspace',

    [ValidateRange(120, 240)]
    [int] $RecoveryDeadlineSeconds = 150,

    [string] $EvidenceDirectory = $env:JOB_RESULTS_DIR
)

$ErrorActionPreference = 'Stop'
$healthUrl = "http://127.0.0.1:$RemotePort/healthz"
$watchdogLog = Join-Path $DevspaceDirectory '.tunnel-watchdog.log'
$keeperExitLog = Join-Path $env:LOCALAPPDATA 'AgentTaskboard\tunnel-keeper\ssh-exit.log'
$sshPath = (Get-Command $SshExecutable -ErrorAction Stop).Source
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    throw 'EvidenceDirectory is required. Pass it explicitly or set JOB_RESULTS_DIR.'
}
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$evidencePath = Join-Path $EvidenceDirectory 'forced-kill-test.md'

function Invoke-RemoteCommand {
    param([Parameter(Mandatory)] [string] $Command)

    $output = & $sshPath `
        -T -o BatchMode=yes -o ConnectTimeout=10 -o ConnectionAttempts=1 `
        $SshTarget $Command 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ((@($output) -join ' ') -replace '[\r\n]+', ' ').Trim()
    }
}

function Test-TunnelHealth {
    $result = Invoke-RemoteCommand -Command "curl -sf --max-time 6 '$healthUrl' >/dev/null"
    return $result.ExitCode -eq 0
}

function Read-NewJournal {
    param([Parameter(Mandatory)] [long] $Offset)

    if (-not (Test-Path -LiteralPath $watchdogLog)) { return '' }
    $stream = [IO.File]::Open($watchdogLog, 'Open', 'Read', 'ReadWrite')
    try {
        [void] $stream.Seek($Offset, 'Begin')
        $reader = [IO.StreamReader]::new($stream)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-JournalLength {
    if (Test-Path -LiteralPath $watchdogLog) {
        return (Get-Item -LiteralPath $watchdogLog).Length
    }
    return 0
}

function Reset-WatchdogCounters {
    $offset = Get-JournalLength
    Stop-ScheduledTask -TaskName $WatchdogTaskName -ErrorAction SilentlyContinue
    $stopDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Get-ScheduledTask -TaskName $WatchdogTaskName).State -eq 'Running' -and
        [DateTime]::UtcNow -lt $stopDeadline) {
        Start-Sleep -Milliseconds 250
    }
    if ((Get-ScheduledTask -TaskName $WatchdogTaskName).State -eq 'Running') {
        throw 'The watchdog task did not stop within 10 seconds.'
    }
    Start-ScheduledTask -TaskName $WatchdogTaskName -ErrorAction Stop
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Seconds 1
        $journal = Read-NewJournal -Offset $offset
        if ($journal -match 'event=watchdog-start status=running' -and
            $journal -match 'event=probe status=healthy') {
            return
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The watchdog did not restart with a healthy probe within 30 seconds.'
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

$startedAt = [DateTime]::UtcNow
$recoveredAt = $null
$injected = $false
$injectionOutput = ''
$failure = ''
$watchdogTask = $null
$keeperTask = $null
$journalOffset = Get-JournalLength

try {
    $watchdogTask = Get-ScheduledTask -TaskName $WatchdogTaskName -ErrorAction Stop
    $keeperTask = Get-ScheduledTask -TaskName $KeeperTaskName -ErrorAction Stop
    if ($watchdogTask.Principal.LogonType -ne 'S4U') {
        throw "Scheduled task '$WatchdogTaskName' must use the S4U logon type."
    }
    if ($keeperTask.Principal.LogonType -ne 'S4U') {
        throw "Scheduled task '$KeeperTaskName' must use the S4U logon type."
    }
    if ($watchdogTask.State -ne 'Running') {
        throw "Scheduled task '$WatchdogTaskName' must be running before the forced-kill test."
    }
    if (-not (Test-TunnelHealth)) {
        throw "Precondition failed: $healthUrl is not healthy through $SshTarget."
    }

    Reset-WatchdogCounters
    $journalOffset = Get-JournalLength
    $startedAt = [DateTime]::UtcNow
    Stop-ScheduledTask -TaskName $KeeperTaskName -ErrorAction SilentlyContinue
    $injected = $true
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
    $injectionOutput = $injection.Output
    if ($injection.ExitCode -ne 0) {
        throw "Forced-kill injection failed with exit $($injection.ExitCode): $($injection.Output)"
    }
    if (Test-TunnelHealth) {
        throw 'The forced listener did not make the tunnel health probe fail.'
    }

    $deadline = $startedAt.AddSeconds($RecoveryDeadlineSeconds)
    do {
        Start-Sleep -Seconds 3
        if (Test-TunnelHealth) {
            $recoveredAt = [DateTime]::UtcNow
            break
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($null -eq $recoveredAt) {
        throw "The tunnel did not recover within $RecoveryDeadlineSeconds seconds."
    }

    $newJournal = Read-NewJournal -Offset $journalOffset
    $failedProbeCount = ([regex]::Matches($newJournal, 'event=probe status=failed')).Count
    if ($failedProbeCount -lt 2) {
        throw "Recovery occurred without two journalled failed probes; observed $failedProbeCount."
    }
    if ($newJournal -notmatch 'event=remote-listener-cleanup status=ok' -or
        $newJournal -notmatch 'event=keeper-restart status=ok' -or
        $newJournal -notmatch 'event=heal-result status=healthy') {
        throw 'Recovery journal is missing cleanup, keeper restart, or healthy heal proof.'
    }
}
catch {
    $failure = $_.Exception.Message
}
finally {
    if ($injected -and $null -eq $recoveredAt) {
        Clear-TestListener
        Start-ScheduledTask -TaskName $KeeperTaskName -ErrorAction SilentlyContinue
    }
    Start-ScheduledTask -TaskName $WatchdogTaskName -ErrorAction SilentlyContinue
}

$newJournal = Read-NewJournal -Offset $journalOffset
$sshExit = if (Test-Path -LiteralPath $keeperExitLog) {
    @(Get-Content -LiteralPath $keeperExitLog | Select-Object -Last 20) -join [Environment]::NewLine
} else { '(keeper SSH exit log was not found)' }
$elapsed = if ($null -ne $recoveredAt) {
    [Math]::Round(($recoveredAt - $startedAt).TotalSeconds, 1)
} else { $null }
$status = if ([string]::IsNullOrWhiteSpace($failure)) { 'PASS' } else { 'FAIL' }
$elapsedText = if ($null -eq $elapsed) { 'not recovered' } else { "$elapsed seconds" }
$journalBlock = if ([string]::IsNullOrWhiteSpace($newJournal)) {
    '(no new watchdog journal lines)'
} else { $newJournal.Trim() }

$report = @"
# Tunnel watchdog forced-kill test

- Status: **$status**
- Started (UTC): $($startedAt.ToString('o'))
- Target: $SshTarget
- Fault: killed the real runner-side listener and bound a failing dummy listener to 127.0.0.1:$RemotePort.
- Injection: $injectionOutput
- Recovery time: $elapsedText
- Recovery deadline: $RecoveryDeadlineSeconds seconds
- Watchdog task logon type: $(if ($watchdogTask) { $watchdogTask.Principal.LogonType } else { 'unavailable' })
- Keeper task logon type: $(if ($keeperTask) { $keeperTask.Principal.LogonType } else { 'unavailable' })
- Failure: $(if ($failure) { $failure } else { 'none' })

## Watchdog journal excerpt

~~~text
$journalBlock
~~~

## Keeper SSH exit log tail

~~~text
$sshExit
~~~
"@
$report | Set-Content -LiteralPath $evidencePath -Encoding utf8
Write-Output $report

if ($status -ne 'PASS') { exit 4 }
exit 0
