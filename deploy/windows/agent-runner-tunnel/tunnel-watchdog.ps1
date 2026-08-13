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
    $OperatorAlarmPath = Join-Path $DevspaceDirectory '.operator-alarm.log'
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
        -T -o BatchMode=yes -o ConnectTimeout=10 `
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
    $command = @'
port=__REMOTE_PORT__
if command -v ss >/dev/null 2>&1; then
  listener_rows="$(ss -H -ltnp "sport = :$port" 2>/dev/null | awk -v endpoint="127.0.0.1:$port" '$4 == endpoint { print }')"
  pids="$(printf '%s\n' "$listener_rows" | sed -n 's/.*pid=\([0-9][0-9]*\).*/\1/p' | sort -u)"
elif command -v lsof >/dev/null 2>&1; then
  pids="$(lsof -nP -a -iTCP@127.0.0.1:$port -sTCP:LISTEN -t 2>/dev/null | sort -u)"
else
  printf '%s\n' 'listener-inspection=unavailable' >&2
  exit 3
fi
if [ -z "$pids" ] && [ -n "${listener_rows:-}" ]; then
  current_sshd="$PPID"
  pids="$(pgrep -u "$(id -u)" -x sshd 2>/dev/null | awk -v current="$current_sshd" '$1 != current' | sort -u)"
  [ -z "$pids" ] || printf '%s\n' 'listener-discovery=agent-account-sshd-fallback'
fi
if [ -z "$pids" ]; then
  printf '%s\n' 'listener=none'
  exit 0
fi
printf 'listener-pids=%s\n' "$(printf '%s' "$pids" | tr '\n' ',')"
kill $pids 2>/dev/null || true
sleep 2
remaining=''
for pid in $pids; do
  if kill -0 "$pid" 2>/dev/null; then remaining="$remaining $pid"; fi
done
if [ -n "$remaining" ]; then kill -9 $remaining 2>/dev/null || true; fi
exit 0
'@ -replace '__REMOTE_PORT__', ([string] $RemotePort)

    return Invoke-RemoteCommand -Command $command
}

function Restart-KeeperTask {
    try {
        $task = Get-ScheduledTask -TaskName $KeeperTaskName -ErrorAction Stop
        if ($task.State -ne 'Disabled') {
            Stop-ScheduledTask -TaskName $KeeperTaskName -ErrorAction SilentlyContinue
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
                    $lastProbeWasHealthy = $true
                }
                else {
                    $consecutiveHealFailures++
                    if ($consecutiveHealFailures -eq $HealFailureAlarmThreshold) {
                        Write-OperatorAlarm -Message `
                            "Tunnel heal failed $consecutiveHealFailures consecutive times; inspect $LogPath and the keeper ssh-exit.log."
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
