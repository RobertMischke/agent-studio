[CmdletBinding()]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [string] $SshExecutable = 'ssh.exe',

    [string] $KeeperTaskName = 'AgentRunner-TunnelKeeper',

    [ValidateRange(10, 3600)]
    [int] $ProbeIntervalSeconds = 60,

    [ValidateRange(1, 10)]
    [int] $FailureThreshold = 2,

    [ValidateRange(10, 180)]
    [int] $RecoveryWaitSeconds = 45,

    [ValidateRange(1, 10)]
    [int] $HealFailureAlarmThreshold = 2,

    [string] $DevspaceDirectory = 'C:\Projects\agent-taskboard-devspace',

    [string] $LogPath,

    [string] $OperatorAlarmPath,

    [ValidateRange(0, 1000000)]
    [int] $MaximumProbeCount = 0
)

$ErrorActionPreference = 'Stop'
$healthUrl = "http://127.0.0.1:$RemotePort/healthz"
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $DevspaceDirectory '.tunnel-watchdog.log'
}
if ([string]::IsNullOrWhiteSpace($OperatorAlarmPath)) {
    $OperatorAlarmPath = Join-Path $DevspaceDirectory '.operator-alarm'
}

$logDirectory = Split-Path -Parent $LogPath
$alarmDirectory = Split-Path -Parent $OperatorAlarmPath
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $alarmDirectory -Force | Out-Null

$mutexName = "Local\AgentTaskboardTunnelWatchdog-$RemotePort"
$mutex = [Threading.Mutex]::new($false, $mutexName)
$ownsMutex = $false

function ConvertTo-JournalText {
    param([AllowNull()] [object] $Value)

    if ($null -eq $Value) { return '' }
    return (([string] $Value) -replace '[\r\n]+', ' ' -replace '\s+', ' ').Trim()
}

function Write-WatchdogJournal {
    param(
        [Parameter(Mandatory)] [string] $Event,
        [Parameter(Mandatory)] [string] $Status,
        [AllowEmptyString()] [string] $Message = ''
    )

    $line = '{0:o} event={1} status={2} target={3} port={4} message={5}' -f `
        [DateTime]::UtcNow, $Event, $Status, $SshTarget, $RemotePort, `
        (ConvertTo-JournalText $Message)
    Add-Content -LiteralPath $LogPath -Value $line -Encoding utf8
}

function Write-OperatorAlarm {
    param([Parameter(Mandatory)] [string] $Message)

    $line = '{0:o} source=tunnel-watchdog severity=alarm target={1} port={2} message={3}' -f `
        [DateTime]::UtcNow, $SshTarget, $RemotePort, (ConvertTo-JournalText $Message)
    Add-Content -LiteralPath $OperatorAlarmPath -Value $line -Encoding utf8
    Write-WatchdogJournal -Event 'operator-alarm' -Status 'raised' -Message $Message
}

function Invoke-RemoteCommand {
    param([Parameter(Mandatory)] [string] $Command)

    $output = & $script:sshPath `
        -T -o BatchMode=yes -o ConnectTimeout=10 -o ConnectionAttempts=1 `
        $SshTarget $Command 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ConvertTo-JournalText (@($output) -join ' ')
    }
}

function Test-TunnelHealth {
    $command = "curl -sf --max-time 6 '$healthUrl'"
    $result = Invoke-RemoteCommand -Command $command
    return [pscustomobject]@{
        Healthy = $result.ExitCode -eq 0
        Detail = if ($result.ExitCode -eq 0) {
            'Remote health probe succeeded.'
        } else {
            "Remote health probe exited $($result.ExitCode): $($result.Output)"
        }
    }
}

function Stop-RemoteListener {
    # A hardened host may hide ss(8)'s pid= field even from the socket owner.
    # In that case, resolve the listener's cgroup and signal only processes in
    # the same agent-owned SSH session. Never fall back to every sshd process.
    $command = @'
port=__REMOTE_PORT__
endpoint="127.0.0.1:$port"
if ! command -v ss >/dev/null 2>&1; then
  printf 'listener-cleanup=unresolved port=%s detail=ss-unavailable\n' "$port"
  exit 5
fi
lines="$(ss -H -ltnpe "sport = :$port" 2>/dev/null || true)"
matching="$(printf '%s\n' "$lines" | awk -v endpoint="$endpoint" '$4 == endpoint { print }')"
if [ -z "$matching" ]; then
  printf 'listener-cleanup=none port=%s\n' "$port"
  exit 0
fi

pids="$(printf '%s\n' "$matching" | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u || true)"
scopes="$(printf '%s\n' "$matching" | grep -o 'cgroup:[^ ]*' | cut -d: -f2- | sort -u || true)"
if [ -n "$scopes" ]; then
  own_uid="$(id -u)"
  session_scopes="$(printf '%s\n' "$scopes" | grep -E "^/user[.]slice/user-$own_uid[.]slice/session-[^/]+[.]scope$" || true)"
  for status in /proc/[0-9]*/status; do
    [ -r "$status" ] || continue
    pid="${status#/proc/}"
    pid="${pid%/status}"
    uid="$(awk '/^Uid:/ { print $2; exit }' "$status" 2>/dev/null || true)"
    [ "$uid" = "$own_uid" ] || continue
    cgroup_file="/proc/$pid/cgroup"
    [ -r "$cgroup_file" ] || continue
    while IFS= read -r record; do
      process_scope="${record#*::}"
      if printf '%s\n' "$session_scopes" | grep -Fqx -- "$process_scope"; then
        pids="${pids}${pids:+
}$pid"
        break
      fi
    done < "$cgroup_file"
  done
fi
pids="$(printf '%s\n' "$pids" | grep -E '^[0-9]+$' | sort -u || true)"
if [ -z "$pids" ]; then
  printf 'listener-cleanup=unresolved port=%s detail=no-agent-owned-pid-or-cgroup\n' "$port"
  exit 5
fi

kill $pids 2>/dev/null || true
sleep 2
for pid in $pids; do
  kill -0 "$pid" 2>/dev/null && kill -9 "$pid" 2>/dev/null || true
done
sleep 1
remaining="$(ss -H -ltn "sport = :$port" 2>/dev/null | awk -v endpoint="$endpoint" '$4 == endpoint { print }')"
if [ -n "$remaining" ]; then
  printf 'listener-cleanup=failed port=%s pids=%s detail=listener-still-present\n' "$port" "$(printf '%s' "$pids" | tr '\n' ',')"
  exit 5
fi
printf 'listener-cleanup=killed port=%s pids=%s\n' "$port" "$(printf '%s' "$pids" | tr '\n' ',')"
'@.Replace('__REMOTE_PORT__', [string] $RemotePort)

    return Invoke-RemoteCommand -Command $command
}

function Restart-KeeperTask {
    try {
        $task = Get-ScheduledTask -TaskName $KeeperTaskName -ErrorAction Stop
        if ($task.State -ne 'Disabled') {
            Stop-ScheduledTask -TaskName $KeeperTaskName -ErrorAction SilentlyContinue
            $stopDeadline = [DateTime]::UtcNow.AddSeconds(10)
            while ((Get-ScheduledTask -TaskName $KeeperTaskName).State -eq 'Running' -and
                [DateTime]::UtcNow -lt $stopDeadline) {
                Start-Sleep -Milliseconds 250
            }
            if ((Get-ScheduledTask -TaskName $KeeperTaskName).State -eq 'Running') {
                return [pscustomobject]@{
                    Succeeded = $false
                    Detail = 'Keeper scheduled task did not stop within 10 seconds.'
                }
            }
            Start-ScheduledTask -TaskName $KeeperTaskName -ErrorAction Stop
            return [pscustomobject]@{ Succeeded = $true; Detail = 'Keeper scheduled task restarted.' }
        }
        return [pscustomobject]@{ Succeeded = $false; Detail = 'Keeper scheduled task is disabled.' }
    }
    catch {
        return [pscustomobject]@{ Succeeded = $false; Detail = $_.Exception.Message }
    }
}

function Invoke-TunnelHeal {
    Write-WatchdogJournal -Event 'heal-start' -Status 'repairing' `
        -Message "Starting operator recovery sequence after $FailureThreshold consecutive failed probes."

    $cleanup = Stop-RemoteListener
    Write-WatchdogJournal -Event 'remote-listener-cleanup' `
        -Status $(if ($cleanup.ExitCode -eq 0) { 'ok' } else { 'failed' }) `
        -Message "exit=$($cleanup.ExitCode) $($cleanup.Output)"

    $restart = Restart-KeeperTask
    Write-WatchdogJournal -Event 'keeper-restart' `
        -Status $(if ($restart.Succeeded) { 'ok' } else { 'failed' }) `
        -Message $restart.Detail

    if (-not $restart.Succeeded) {
        Write-WatchdogJournal -Event 'heal-result' -Status 'failed' `
            -Message 'The keeper task could not be restarted.'
        return $false
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($RecoveryWaitSeconds)
    do {
        Start-Sleep -Seconds 3
        $probe = Test-TunnelHealth
        if ($probe.Healthy) {
            Write-WatchdogJournal -Event 'heal-result' -Status 'healthy' `
                -Message 'The replacement tunnel passed the remote health probe.'
            return $true
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    Write-WatchdogJournal -Event 'heal-result' -Status 'failed' `
        -Message "Tunnel health did not recover within $RecoveryWaitSeconds seconds."
    return $false
}

try {
    $ownsMutex = $mutex.WaitOne(0)
    if (-not $ownsMutex) { exit 0 }

    $script:sshPath = (Get-Command $SshExecutable -ErrorAction Stop).Source
    $consecutiveProbeFailures = 0
    $consecutiveHealFailures = 0
    $operatorAlarmRaised = $false
    $probeCount = 0
    $lastProbeWasHealthy = $null

    Write-WatchdogJournal -Event 'watchdog-start' -Status 'running' `
        -Message "Probe interval is $ProbeIntervalSeconds seconds; heal threshold is $FailureThreshold."

    while ($MaximumProbeCount -eq 0 -or $probeCount -lt $MaximumProbeCount) {
        $probeCount++
        $probe = Test-TunnelHealth
        if ($probe.Healthy) {
            if ($lastProbeWasHealthy -ne $true) {
                Write-WatchdogJournal -Event 'probe' -Status 'healthy' -Message $probe.Detail
            }
            $consecutiveProbeFailures = 0
            $consecutiveHealFailures = 0
            $operatorAlarmRaised = $false
            $lastProbeWasHealthy = $true
        }
        else {
            $consecutiveProbeFailures++
            $lastProbeWasHealthy = $false
            Write-WatchdogJournal -Event 'probe' -Status 'failed' `
                -Message "consecutive=$consecutiveProbeFailures $($probe.Detail)"

            if ($consecutiveProbeFailures -ge $FailureThreshold) {
                $consecutiveProbeFailures = 0
                if (Invoke-TunnelHeal) {
                    $consecutiveHealFailures = 0
                    $operatorAlarmRaised = $false
                    $lastProbeWasHealthy = $true
                }
                else {
                    $consecutiveHealFailures++
                    if ($consecutiveHealFailures -ge $HealFailureAlarmThreshold -and
                        -not $operatorAlarmRaised) {
                        try {
                            Write-OperatorAlarm -Message `
                                "Tunnel heal failed $consecutiveHealFailures consecutive times; inspect $LogPath and the keeper ssh-exit.log."
                            $operatorAlarmRaised = $true
                        }
                        catch {
                            Write-WatchdogJournal -Event 'operator-alarm' -Status 'write-failed' `
                                -Message $_.Exception.Message
                        }
                    }
                }
            }
        }

        if ($MaximumProbeCount -eq 0 -or $probeCount -lt $MaximumProbeCount) {
            Start-Sleep -Seconds $ProbeIntervalSeconds
        }
    }

    Write-WatchdogJournal -Event 'watchdog-stop' -Status 'bounded-run-complete' `
        -Message "Completed $probeCount probe iteration(s)."
    exit 0
}
catch {
    Write-WatchdogJournal -Event 'watchdog-crash' -Status 'failed' -Message $_.Exception.Message
    exit 4
}
finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
