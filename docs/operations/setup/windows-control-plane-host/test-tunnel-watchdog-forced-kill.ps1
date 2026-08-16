[CmdletBinding()]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [ValidateRange(60, 300)]
    [int] $TimeoutSeconds = 150,

    [string] $StateDirectory = (Join-Path $env:LOCALAPPDATA 'Agent Studio\Tunnel\state'),

    [string] $ResultsDirectory = $env:JOB_RESULTS_DIR,

    [string] $SshExecutable = 'ssh.exe'
)

$ErrorActionPreference = 'Stop'
$healthUrl = "http://127.0.0.1:$RemotePort/healthz"
$watchdogLog = Join-Path $StateDirectory 'watchdog-events.log'

function Test-TunnelHealth {
    & $SshExecutable -T -o BatchMode=yes -o ConnectTimeout=10 $SshTarget `
        "curl -sf --max-time 6 '$healthUrl' >/dev/null" 2>$null
    return $LASTEXITCODE -eq 0
}

if (-not (Test-TunnelHealth)) {
    throw "The tunnel must be healthy before the forced-kill test starts: $healthUrl"
}

$cleanupTemplate = @'
port=__PORT__
pids=$(ss -H -ltnp "sport = :$port" 2>/dev/null | awk -v endpoint="127.0.0.1:$port" '$4 == endpoint { line=$0; while (match(line, /pid=[0-9]+/)) { print substr(line, RSTART + 4, RLENGTH - 4); line=substr(line, RSTART + RLENGTH) } }' | sort -u)
if [ -z "$pids" ]; then printf 'no-listener\n'; exit 3; fi
kill -KILL $pids
printf 'forced-kill-pids=%s\n' "$(printf '%s' "$pids" | tr '\n' ',')"
'@
$cleanupCommand = $cleanupTemplate.Replace('__PORT__', $RemotePort.ToString())
$startedAt = [DateTime]::UtcNow
$killOutput = & $SshExecutable -T -o BatchMode=yes -o ConnectTimeout=10 `
    $SshTarget $cleanupCommand 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Could not force-kill the runner listener: $killOutput"
}

$deadline = $startedAt.AddSeconds($TimeoutSeconds)
$recoveredAt = $null
do {
    Start-Sleep -Seconds 3
    if (Test-TunnelHealth) {
        $recoveredAt = [DateTime]::UtcNow
        break
    }
} while ([DateTime]::UtcNow -lt $deadline)

if ($null -eq $recoveredAt) {
    throw "Tunnel watchdog did not restore $healthUrl within $TimeoutSeconds seconds. See $watchdogLog"
}

$elapsed = [Math]::Round(($recoveredAt - $startedAt).TotalSeconds, 1)
$journalEvidence = if (Test-Path -LiteralPath $watchdogLog) {
    Get-Content -LiteralPath $watchdogLog |
        Where-Object { $_ -match 'event=(probe_failed|heal_started|remote_listener_cleanup|keeper_restart|heal_succeeded)' } |
        Select-Object -Last 8
} else { @('watchdog log was not found') }

$report = @(
    '# Tunnel watchdog live forced-kill evidence',
    '',
    "- Started (UTC): $($startedAt.ToString('o'))",
    "- Forced kill: $($killOutput -join ' ')",
    "- Recovered (UTC): $($recoveredAt.ToString('o'))",
    "- Recovery time: $elapsed seconds",
    "- Health URL: $healthUrl",
    '',
    '## Watchdog journal excerpt',
    '',
    '```text',
    $journalEvidence,
    '```'
)

if (-not [string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
    $reportPath = Join-Path $ResultsDirectory 'tunnel-watchdog-forced-kill--real.md'
    $report | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "Evidence written to $reportPath"
}
$report
