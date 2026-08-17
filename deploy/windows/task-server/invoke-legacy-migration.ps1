[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $LegacyRoot,

    [Parameter(Mandatory)]
    [string] $WorkspaceName,

    [string] $TaskServerUrl = 'http://127.0.0.1:5071',

    [string] $EvidencePath,

    [switch] $Import,

    [switch] $FreezeConfirmed
)

$ErrorActionPreference = 'Stop'
$legacy = (Resolve-Path -LiteralPath $LegacyRoot).Path
$baseUrl = $TaskServerUrl.TrimEnd('/')
$protocol = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/v1/protocol"
$headers = @{
    'X-Task-Protocol-Version' = [string] $protocol.Current
    'X-Client-Id' = 'task-server-cutover'
    'X-Actor-Id' = 'task-server-cutover'
}
$request = @{
    legacyRoot = $legacy
    workspaceName = $WorkspaceName
    freezeConfirmed = $false
    preserveEvidenceGit = $true
} | ConvertTo-Json -Depth 10
$inventory = Invoke-RestMethod `
    -Method Post `
    -Uri "$baseUrl/api/v1/management/migrations/legacy/inventory" `
    -Headers $headers `
    -ContentType 'application/json' `
    -Body $request

$result = $null
if ($Import) {
    if (-not $FreezeConfirmed) {
        throw 'Import requires -FreezeConfirmed after every legacy writer and runner has stopped.'
    }
    $modeBody = @{ mode = 'maintenance'; reason = 'planned single-writer legacy cutover' } |
        ConvertTo-Json
    Invoke-RestMethod `
        -Method Put `
        -Uri "$baseUrl/api/v1/management/mode" `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $modeBody | Out-Null
    $importBody = @{
        legacyRoot = $legacy
        workspaceName = $WorkspaceName
        freezeConfirmed = $true
        preserveEvidenceGit = $true
        expectedMigrationId = $inventory.migrationId
    } | ConvertTo-Json -Depth 10
    $result = Invoke-RestMethod `
        -Method Post `
        -Uri "$baseUrl/api/v1/management/migrations/legacy/import" `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $importBody
    foreach ($field in @('projects', 'tasks', 'events', 'artifacts', 'runnerIdentities', 'runs', 'leases', 'reviewAttempts')) {
        if ($inventory.$field -ne $result.$field) {
            throw "Migration count mismatch for $field: inventory=$($inventory.$field) import=$($result.$field)"
        }
    }
}

$evidence = [pscustomobject]@{
    observedAt = [DateTime]::UtcNow.ToString('o')
    sourceKind = if ($Import) { 'frozen-legacy-root' } else { 'inventory-dry-run' }
    taskServerUrl = $baseUrl
    protocol = $protocol
    inventory = $inventory
    import = $result
}
if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    $parent = Split-Path -Parent $EvidencePath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $evidence | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
}
$evidence
