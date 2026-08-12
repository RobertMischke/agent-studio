[CmdletBinding()]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [ValidateRange(2, 10)]
    [int] $ProbeFailureThreshold = 2,

    [ValidateRange(10, 180)]
    [int] $RecoveryWaitSeconds = 45,

    [string] $KeeperTaskName = 'AgentRunner-TunnelKeeper',

    [string] $SshExecutable = 'ssh.exe',

    [string] $StateDirectory = (Join-Path $env:LOCALAPPDATA 'AgentTaskboard\tunnel-watchdog'),

    [string] $DevspaceDirectory = '',

    [string] $LogPath = '',

    [string] $AlarmPath = ''
)

$ErrorActionPreference = 'Stop'
$sentinel = 'AGENT_TASK_SERVER_ROUTE_OK'
$healthUrl = "http://127.0.0.1:$RemotePort/healthz"
$statePath = Join-Path $StateDirectory 'state.json'
$mutexName = "Local\AgentTaskboardTunnelWatchdog-$RemotePort"
$mutex = [Threading.Mutex]::new($false, $mutexName)
$ownsMutex = $false

if ([string]::IsNullOrWhiteSpace($DevspaceDirectory)) {
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
    $DevspaceDirectory = Split-Path -Parent $repositoryRoot
}
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $DevspaceDirectory '.tunnel-watchdog.log'
}
if ([string]::IsNullOrWhiteSpace($AlarmPath)) {
    $AlarmPath = Join-Path $DevspaceDirectory '.operator-alarm'
}

function Write-Journal {
    param([Parameter(Mandatory)] [string] $Message)

    $line = '{0:o} {1}' -f [DateTime]::UtcNow, ($Message -replace "[\r\n]+", ' ')
    Add-Content -LiteralPath $LogPath -Value $line -Encoding utf8
}

function Read-WatchdogState {
    if (-not (Test-Path -LiteralPath $statePath)) { return $null }
    try { return Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json }
    catch {
        Write-Journal "status=warning event=state-read-failed message=$($_.Exception.Message)"
        return $null
    }
}

function Write-WatchdogState {
    param(
        [Parameter(Mandatory)] [int] $ConsecutiveProbeFailures,
        [Parameter(Mandatory)] [int] $ConsecutiveHealFailures,
        [Parameter(Mandatory)] [bool] $AlarmRaised,
        [Parameter(Mandatory)] [string] $Status
    )

    $state = [ordered]@{
        status = $Status
        observedAt = [DateTime]::UtcNow.ToString('o')
        sshTarget = $SshTarget
        remotePort = $RemotePort
        consecutiveProbeFailures = $ConsecutiveProbeFailures
        consecutiveHealFailures = $ConsecutiveHealFailures
        alarmRaised = $AlarmRaised
    }
    $temporary = "$statePath.tmp"
    $state | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $statePath -Force
}

function Get-StateInteger {
    param(
        $State,
        [Parameter(Mandatory)] [string] $Property
    )

    if ($null -eq $State -or $null -eq $State.$Property) { return 0 }
    return [int] $State.$Property
}

function Invoke-RemoteCommand {
    param([Parameter(Mandatory)] [string] $Command)

    $output = & $script:sshPath `
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
    $remoteCommand =
        "curl -sf --max-time 6 '$healthUrl' >/dev/null && printf '%s\n' '$sentinel'"
    $result = Invoke-RemoteCommand -Command $remoteCommand
    $script:lastProbeDetail = if ($result.Output) { $result.Output } else { "ssh-exit-$($result.ExitCode)" }
    return $result.Succeeded -and $result.Output -match "(^|\s)$sentinel(\s|$)"
}

function Clear-RemoteListener {
    # Some hardened hosts hide ss(8)'s pid= field even from the socket owner.
    # Resolve the socket's cgroup as a bounded fallback, then signal only
    # processes in that agent-owned session. This preserves exact port and
    # account scoping without sudo.
    $command = @'
port='__REMOTE_PORT__'
endpoint="127.0.0.1:$port"
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
    Stop-ScheduledTask -TaskName $KeeperTaskName -ErrorAction SilentlyContinue
    Start-ScheduledTask -TaskName $KeeperTaskName -ErrorAction Stop
}

function Wait-ForHealthyTunnel {
    $deadline = [DateTime]::UtcNow.AddSeconds($RecoveryWaitSeconds)
    do {
        Start-Sleep -Seconds 3
        if (Test-TunnelHealth) { return $true }
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

try {
    $ownsMutex = $mutex.WaitOne(0)
    if (-not $ownsMutex) { exit 0 }

    New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null
    $script:sshPath = (Get-Command $SshExecutable -ErrorAction Stop).Source
    $previous = Read-WatchdogState

    if (Test-TunnelHealth) {
        if ((Get-StateInteger -State $previous -Property 'consecutiveProbeFailures') -gt 0) {
            Write-Journal 'status=healthy event=probe-recovered consecutive_probe_failures=0'
        } else {
            Write-Journal 'status=healthy event=probe-ok'
        }
        Write-WatchdogState -ConsecutiveProbeFailures 0 -ConsecutiveHealFailures 0 `
            -AlarmRaised $false -Status 'healthy'
        exit 0
    }

    $probeFailures = (Get-StateInteger -State $previous -Property 'consecutiveProbeFailures') + 1
    $healFailures = Get-StateInteger -State $previous -Property 'consecutiveHealFailures'
    $alarmRaised = $previous -and $previous.alarmRaised -eq $true
    Write-Journal "status=unhealthy event=probe-failed consecutive_probe_failures=$probeFailures detail=$script:lastProbeDetail"

    if ($probeFailures -lt $ProbeFailureThreshold) {
        Write-WatchdogState -ConsecutiveProbeFailures $probeFailures `
            -ConsecutiveHealFailures $healFailures -AlarmRaised $alarmRaised -Status 'suspect'
        exit 0
    }

    Write-Journal "status=healing event=heal-start attempt=$($healFailures + 1)"
    $cleanup = Clear-RemoteListener
    Write-Journal "status=healing event=remote-listener-cleanup succeeded=$($cleanup.Succeeded.ToString().ToLowerInvariant()) detail=$($cleanup.Output)"

    $keeperRestarted = $true
    try {
        Restart-KeeperTask
        Write-Journal "status=healing event=keeper-restarted task=$KeeperTaskName"
    }
    catch {
        $keeperRestarted = $false
        Write-Journal "status=unhealthy event=keeper-restart-failed task=$KeeperTaskName message=$($_.Exception.Message)"
    }

    if ($keeperRestarted -and (Wait-ForHealthyTunnel)) {
        Write-Journal "status=healthy event=heal-succeeded attempt=$($healFailures + 1)"
        Write-WatchdogState -ConsecutiveProbeFailures 0 -ConsecutiveHealFailures 0 `
            -AlarmRaised $false -Status 'healthy'
        exit 0
    }

    $healFailures++
    Write-Journal "status=unhealthy event=heal-failed consecutive_heal_failures=$healFailures detail=$script:lastProbeDetail"
    if ($healFailures -ge 2 -and -not $alarmRaised) {
        $alarmLine = '{0:o} severity=alarm source=tunnel-watchdog event=heal-failed-twice target={1} port={2} keeper_task={3}' -f `
            [DateTime]::UtcNow, $SshTarget, $RemotePort, $KeeperTaskName
        try {
            Add-Content -LiteralPath $AlarmPath -Value $alarmLine -Encoding utf8
            $alarmRaised = $true
            Write-Journal "status=alarm event=operator-alarm-written path=$AlarmPath"
        }
        catch {
            Write-Journal "status=alarm event=operator-alarm-write-failed path=$AlarmPath message=$($_.Exception.Message)"
        }
    }

    Write-WatchdogState -ConsecutiveProbeFailures $probeFailures `
        -ConsecutiveHealFailures $healFailures -AlarmRaised $alarmRaised -Status 'unhealthy'
    exit 4
}
catch {
    if (-not (Test-Path -LiteralPath $StateDirectory)) {
        New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    }
    try { Write-Journal "status=unhealthy event=watchdog-error message=$($_.Exception.Message)" }
    catch { Write-Error $_.Exception.Message }
    exit 4
}
finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
