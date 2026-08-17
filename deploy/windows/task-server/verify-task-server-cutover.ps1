[CmdletBinding()]
param(
    [string] $TaskServerUrl = 'http://127.0.0.1:5071',
    [string] $StableApiUrl = 'http://127.0.0.1:5031',
    [string] $EvidencePath,
    [DateTime] $Since = ([DateTime]::UtcNow.AddHours(-1)),
    [switch] $RequireFleetEvidence
)

$ErrorActionPreference = 'Stop'
$direct = $TaskServerUrl.TrimEnd('/')
$stable = $StableApiUrl.TrimEnd('/')
$protocol = Invoke-RestMethod -Method Get -Uri "$direct/api/v1/protocol"
$headers = @{
    'X-Task-Protocol-Version' = [string] $protocol.Current
    'X-Client-Id' = 'task-server-cutover-verifier'
    'X-Actor-Id' = 'task-server-cutover-verifier'
}
$directReady = Invoke-WebRequest -UseBasicParsing -Uri "$direct/readyz" -TimeoutSec 10
$stableHealth = Invoke-WebRequest -UseBasicParsing -Uri "$stable/healthz" -TimeoutSec 10
$directStatus = Invoke-RestMethod -Method Get -Uri "$direct/api/v1/management/status" -Headers $headers
$proxyStatus = Invoke-RestMethod -Method Get -Uri "$stable/api/v1/management/status" -Headers $headers
$board = Invoke-RestMethod -Method Get -Uri "$stable/api/tasks/grouped" -Headers $headers
if ($directStatus.serverId -ne $proxyStatus.serverId) {
    throw "Stable proxy server id '$($proxyStatus.serverId)' differs from direct Task Server '$($directStatus.serverId)'."
}

$audit = @(Invoke-RestMethod -Method Get -Uri "$direct/api/v1/management/audit?after=0" -Headers $headers)
$recent = @($audit | Where-Object { [DateTime] $_.occurredAt -ge $Since.ToUniversalTime() })
$actions = @($recent | ForEach-Object { $_.action })
$fleetChecks = [ordered]@{
    runnerClaim = $actions -contains 'run.claimed'
    codingCompletion = $actions -contains 'run.completed'
    reviewClaim = $actions -contains 'review.claimed'
    reviewReport = $actions -contains 'review.reported'
}
if ($RequireFleetEvidence) {
    foreach ($entry in $fleetChecks.GetEnumerator()) {
        if (-not $entry.Value) { throw "Missing post-cutover fleet evidence: $($entry.Key)" }
    }
}

$evidence = [pscustomobject]@{
    observedAt = [DateTime]::UtcNow.ToString('o')
    since = $Since.ToUniversalTime().ToString('o')
    directReadyStatus = $directReady.StatusCode
    stableHealthStatus = $stableHealth.StatusCode
    directStatus = $directStatus
    proxyStatus = $proxyStatus
    boardProjectionType = $board.GetType().FullName
    recentAuditRecords = $recent.Count
    fleetChecks = $fleetChecks
}
if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    $parent = Split-Path -Parent $EvidencePath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $evidence | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
}
$evidence
