[CmdletBinding()]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [string] $SshExecutable = 'ssh.exe',

    [string] $WatchdogTaskName = 'AgentRunner-TunnelWatchdog',

    [string] $DevspaceDirectory = 'C:\Projects\agent-taskboard-devspace',

    [ValidateRange(60, 300)]
    [int] $RecoveryDeadlineSeconds = 150,

    [string] $ResultDirectory = $env:JOB_RESULTS_DIR
)

$ErrorActionPreference = 'Stop'
$healthUrl = "http://127.0.0.1:$RemotePort/healthz"
$watchdogLog = Join-Path $DevspaceDirectory '.tunnel-watchdog.log'
$sshPath = (Get-Command $SshExecutable -ErrorAction Stop).Source
if ([string]::IsNullOrWhiteSpace($ResultDirectory)) {
    throw 'ResultDirectory is required. Pass -ResultDirectory or set JOB_RESULTS_DIR.'
}
New-Item -ItemType Directory -Path $ResultDirectory -Force | Out-Null
$resultPath = Join-Path $ResultDirectory 'tunnel-watchdog-forced-kill.md'

function Invoke-Remote {
    param([Parameter(Mandatory)] [string] $Command)

    $output = & $sshPath -T -o BatchMode=yes -o ConnectTimeout=10 $SshTarget $Command 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output) -join ' ' }
}

function Test-Health {
    $result = Invoke-Remote -Command "curl -sf --max-time 6 '$healthUrl'"
    return $result.ExitCode -eq 0
}

if (-not (Test-Health)) {
    throw "Precondition failed: $healthUrl is not healthy through $SshTarget."
}
$task = Get-ScheduledTask -TaskName $WatchdogTaskName -ErrorAction Stop
if ($task.State -eq 'Disabled') {
    throw "Precondition failed: scheduled task $WatchdogTaskName is disabled."
}

$startedAt = [DateTime]::UtcNow
$existingLogLength = if (Test-Path -LiteralPath $watchdogLog) {
    (Get-Item -LiteralPath $watchdogLog).Length
} else { 0 }

$killCommand = @'
port=__REMOTE_PORT__
listener_rows="$(ss -H -ltnp "sport = :$port" 2>/dev/null | awk -v endpoint="127.0.0.1:$port" '$4 == endpoint { print }')"
if [ -z "$listener_rows" ]; then printf '%s\n' 'forced-kill=listener-not-found'; exit 4; fi
current_sshd="$PPID"
pids="$(printf '%s\n' "$listener_rows" | sed -n 's/.*pid=\([0-9][0-9]*\).*/\1/p' | sort -u)"
if [ -z "$pids" ]; then
  pids="$(pgrep -u "$(id -u)" -x sshd 2>/dev/null | awk -v current="$current_sshd" '$1 != current' | sort -u)"
fi
if [ -z "$pids" ]; then printf '%s\n' 'forced-kill=none'; exit 4; fi
printf 'forced-kill-pids=%s\n' "$(printf '%s' "$pids" | tr '\n' ',')"
kill -9 $pids
'@ -replace '__REMOTE_PORT__', ([string] $RemotePort)
$kill = Invoke-Remote -Command $killCommand
if ($kill.ExitCode -ne 0) {
    throw "Forced kill failed with exit $($kill.ExitCode): $($kill.Output)"
}

$sawOutage = $false
$recoveredAt = $null
$deadline = $startedAt.AddSeconds($RecoveryDeadlineSeconds)
while ([DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds 5
    if (Test-Health) {
        if ($sawOutage) {
            $recoveredAt = [DateTime]::UtcNow
            break
        }
    }
    else {
        $sawOutage = $true
    }
}

$newJournal = ''
if (Test-Path -LiteralPath $watchdogLog) {
    $stream = [IO.File]::Open($watchdogLog, 'Open', 'Read', 'ReadWrite')
    try {
        [void] $stream.Seek($existingLogLength, 'Begin')
        $reader = [IO.StreamReader]::new($stream)
        try { $newJournal = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

$elapsed = if ($recoveredAt) {
    [Math]::Round(($recoveredAt - $startedAt).TotalSeconds, 1)
} else { $null }
$passed = $sawOutage -and $null -ne $recoveredAt -and `
    $elapsed -le $RecoveryDeadlineSeconds -and `
    $newJournal -match 'event=heal-result status=healthy'
$status = if ($passed) { 'PASS' } else { 'FAIL' }
$elapsedText = if ($null -eq $elapsed) { 'not recovered' } else { "$elapsed seconds" }
$journalBlock = if ([string]::IsNullOrWhiteSpace($newJournal)) { '(no new watchdog journal lines)' } else { $newJournal.Trim() }

$report = @"
# Tunnel watchdog forced-kill test

- Status: **$status**
- Started (UTC): $($startedAt.ToString('o'))
- Target: $SshTarget
- Health URL from runner: $healthUrl
- Forced-kill result: $($kill.Output)
- Outage observed: $sawOutage
- Recovery time: $elapsedText
- Recovery deadline: $RecoveryDeadlineSeconds seconds
- Watchdog task: $WatchdogTaskName ($($task.State) at test start)

## Watchdog journal excerpt

~~~text
$journalBlock
~~~
"@
$report | Set-Content -LiteralPath $resultPath -Encoding utf8
Write-Output $report

if (-not $passed) { exit 4 }
exit 0
