[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $LegacyRoot,

    [Parameter(Mandatory)]
    [string] $RehearsalRoot,

    [Parameter(Mandatory)]
    [string] $EvidencePath,

    [string] $TaskServerUrl = 'http://127.0.0.1:5072',

    [string] $WorkspaceName = 'Agent Studio for Software',

    [string] $AuthTokenFile
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $LegacyRoot).Path
$copy = [IO.Path]::GetFullPath($RehearsalRoot)
if (Test-Path -LiteralPath $copy) {
    throw "Rehearsal root must not already exist: $copy"
}
if ($copy.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Rehearsal root must not be inside the legacy source.'
}

$headers = @{
    'X-Task-Protocol-Version' = '2'
    'X-Client-Id' = 'legacy-migration-rehearsal'
}
if (-not [string]::IsNullOrWhiteSpace($AuthTokenFile)) {
    $tokenPath = (Resolve-Path -LiteralPath $AuthTokenFile).Path
    $headers['Authorization'] = "Bearer $((Get-Content -LiteralPath $tokenPath -Raw).Trim())"
}

function Invoke-TaskServerJson {
    param(
        [Parameter(Mandatory)] [ValidateSet('Get', 'Post', 'Put')] [string] $Method,
        [Parameter(Mandatory)] [string] $Path,
        [object] $Body
    )
    $parameters = @{
        Method = $Method
        Uri = "$($TaskServerUrl.TrimEnd('/'))$Path"
        Headers = $headers
        UseBasicParsing = $true
        TimeoutSec = 60
    }
    if ($null -ne $Body) {
        $parameters['ContentType'] = 'application/json'
        $parameters['Body'] = ($Body | ConvertTo-Json -Depth 20)
    }
    Invoke-RestMethod @parameters
}

if ($PSCmdlet.ShouldProcess($copy, 'Create a frozen rehearsal copy and import it into the rehearsal Task Server')) {
    New-Item -ItemType Directory -Path $copy -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $copy -Recurse -Force

    $request = @{
        legacyRoot = $copy
        workspaceName = $WorkspaceName
        freezeConfirmed = $false
        preserveEvidenceGit = $true
    }
    $inventory = Invoke-TaskServerJson -Method Post -Path '/api/v1/management/migrations/legacy/inventory' -Body $request
    Invoke-TaskServerJson -Method Put -Path '/api/v1/management/mode' -Body @{
        mode = 'Maintenance'
        reason = 'Dry-run import against a frozen copy before the production cutover.'
    } | Out-Null
    $request.freezeConfirmed = $true
    $request.expectedMigrationId = $inventory.migrationId
    $import = Invoke-TaskServerJson -Method Post -Path '/api/v1/management/migrations/legacy/import' -Body $request
    $status = Invoke-TaskServerJson -Method Get -Path '/api/v1/management/status'
    $invariants = Invoke-TaskServerJson -Method Get -Path '/api/v1/management/invariants'

    if ($inventory.tasks -ne $import.tasks `
        -or $inventory.runnerIdentities -ne $import.runnerIdentities `
        -or $inventory.codingAttempts -ne $import.codingAttempts `
        -or $inventory.reviewAttempts -ne $import.reviewAttempts `
        -or $inventory.activeAuthorities -ne $import.activeAuthorities `
        -or $inventory.authorityEpoch -ne $import.authorityEpoch) {
        throw 'Migration rehearsal changed task or attempt-authority counts.'
    }
    if ([string]::IsNullOrWhiteSpace($import.integritySha256)) {
        throw 'Migration rehearsal returned no integrity digest.'
    }

    $evidenceDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($EvidencePath))
    New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
    [pscustomobject]@{
        rehearsedAtUtc = [DateTime]::UtcNow.ToString('o')
        source = $source
        frozenCopy = $copy
        server = $TaskServerUrl
        inventory = $inventory
        import = $import
        status = $status
        invariants = $invariants
        assertion = 'Task, runner-identity, coding-attempt, review-attempt, active-authority, and authority-epoch values match.'
    } | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $EvidencePath -Encoding utf8

    Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
}
