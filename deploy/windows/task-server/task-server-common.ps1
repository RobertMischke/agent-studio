# Shared helpers for the Windows Task Server wrapper scripts.
# Dot-source this file; it defines functions only and starts no process.

function Import-ServerEnvironmentFile {
    <#
    .SYNOPSIS
    Loads the host-owned server.env bootstrap contract into the current process.
    .DESCRIPTION
    Mirrors the systemd EnvironmentFile= directive. Returns the loaded key names
    so a caller can log them without ever logging a secret value.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Environment file '$Path' does not exist."
    }

    $names = [Collections.Generic.List[string]]::new()
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $Path) {
        $lineNumber++
        if ($line -match '^\s*(#|$)') { continue }
        if ($line -notmatch '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') {
            throw "Environment file '$Path' line ${lineNumber} is not a KEY=VALUE assignment."
        }
        $name = $Matches[1]
        $value = $Matches[2].Trim()
        if ($value.Length -ge 2 -and
            (($value.StartsWith('"') -and $value.EndsWith('"')) -or
             ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
        $names.Add($name)
    }
    return $names.ToArray()
}

function Get-TaskServerProbeUri {
    <#
    .SYNOPSIS
    Derives a loopback probe base URI from the configured Kestrel addresses.
    .DESCRIPTION
    The service resolves its addresses as ASPNETCORE_URLS first and LISTEN_URL
    second, so the probe follows the same order; reading LISTEN_URL alone would
    probe the wrong port on a host that sets ASPNETCORE_URLS machine-wide. Only
    the first of several semicolon-separated addresses is used, and a wildcard
    host is probed over loopback.
    #>
    [CmdletBinding()]
    param(
        [string] $ListenUrl
    )

    if (-not $PSBoundParameters.ContainsKey('ListenUrl')) {
        $ListenUrl = if (-not [string]::IsNullOrWhiteSpace($env:ASPNETCORE_URLS)) {
            $env:ASPNETCORE_URLS
        } else {
            $env:LISTEN_URL
        }
    }
    if ([string]::IsNullOrWhiteSpace($ListenUrl)) { $ListenUrl = 'http://127.0.0.1:5071' }
    $first = ($ListenUrl -split ';')[0].Trim()
    if ($first -notmatch '^(?<scheme>https?)://(?<host>\[[^\]]+\]|[^:/]+)(?::(?<port>\d+))?/?$') {
        throw "Listen address '$first' is not an absolute HTTP or HTTPS address."
    }

    $scheme = $Matches['scheme']
    $listenHost = $Matches['host']
    $port = if ($Matches['port']) { $Matches['port'] } elseif ($scheme -eq 'https') { '443' } else { '80' }
    if ($listenHost -in @('+', '*', '0.0.0.0', '[::]', '[::0]')) { $listenHost = '127.0.0.1' }
    return [Uri]("{0}://{1}:{2}" -f $scheme, $listenHost, $port)
}

function Test-TaskServerEndpoint {
    <#
    .SYNOPSIS
    Returns true when the endpoint answers HTTP 200 within the timeout.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Url,
        [ValidateRange(1, 120)] [int] $TimeoutSeconds = 10
    )

    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -Method Get -TimeoutSec $TimeoutSeconds
        return [int] $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Get-TaskServerPathCandidate {
    <#
    .SYNOPSIS
    Returns every executable path a running Task Server may report for this
    installation.
    .DESCRIPTION
    The documented install points a 'current' junction at a versioned directory,
    and Windows resolves reparse points while parsing a path, so the running
    process reports the versioned path even though the service was configured
    with the junction path. Comparing the configured spelling alone would never
    match, and the supervisor would keep launching a second instance. Both
    spellings are returned so the comparison holds either way.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ExecutablePath
    )

    $candidates = [Collections.Generic.List[string]]::new()
    $candidates.Add($ExecutablePath)
    $directory = Split-Path -Parent $ExecutablePath
    if ($directory) {
        $item = Get-Item -LiteralPath $directory -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            $target = @($item.Target)[0]
            if ($target) {
                $resolved = Join-Path $target (Split-Path -Leaf $ExecutablePath)
                if (-not $candidates.Contains($resolved)) { $candidates.Add($resolved) }
            }
        }
    }
    return $candidates.ToArray()
}

function Get-TaskServerObservation {
    <#
    .SYNOPSIS
    Reports whether a Task Server from this installation is already running.
    .DESCRIPTION
    Matching is by executable path, never by process name alone, so an unrelated
    task-server on the same host is neither adopted nor stopped.

    A same-named process whose image path cannot be read (another account, a
    different elevation, a different bitness) is counted as unidentified rather
    than treated as absent. Starting a second writer against one SQLite store
    would break the single-owner contract, so the caller must refuse to start
    while any process is unidentified.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ExecutablePath
    )

    $candidates = Get-TaskServerPathCandidate -ExecutablePath $ExecutablePath
    $name = [IO.Path]::GetFileNameWithoutExtension($ExecutablePath)
    $owned = $null
    $unidentified = 0
    foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $process.Path } catch { $path = $null }
        if ([string]::IsNullOrEmpty($path)) { $unidentified++; continue }
        if (-not $owned -and ($candidates -contains $path)) { $owned = $process }
    }

    $startedAt = ''
    $processId = 0
    $ownedPath = ''
    if ($owned) {
        $processId = $owned.Id
        $ownedPath = $owned.Path
        try { $startedAt = $owned.StartTime.ToUniversalTime().ToString('o') } catch { }
    }
    return [pscustomobject]@{
        Process = $owned
        ProcessId = $processId
        ExecutablePath = $ownedPath
        StartedAt = $startedAt
        UnidentifiedCount = $unidentified
    }
}

function Remove-ExpiredWrapperFile {
    <#
    .SYNOPSIS
    Prunes the wrapper's own captured output files.
    .DESCRIPTION
    journald rotates the Linux service's output. Nothing rotates these, and a
    restart loop creates one pair of files per attempt, so each run drops the
    files it will never be asked about again. state.json and events.log are
    never matched by the caller's patterns.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Directory,
        [Parameter(Mandatory)] [string[]] $Pattern,
        [ValidateRange(1, 3650)] [int] $RetentionDays = 30
    )

    # -Filter, not -Include: -Include is ignored on a -LiteralPath without
    # -Recurse and would silently prune nothing.
    $cutoff = [DateTime]::UtcNow.AddDays(-$RetentionDays)
    foreach ($single in $Pattern) {
        foreach ($item in @(Get-ChildItem -LiteralPath $Directory -File -Filter $single -ErrorAction SilentlyContinue)) {
            if ($item.LastWriteTimeUtc -lt $cutoff) {
                Remove-Item -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Write-TaskServerEvent {
    <#
    .SYNOPSIS
    Appends one structured line to the wrapper event log.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $LogPath,
        [Parameter(Mandatory)] [string] $Event,
        [hashtable] $Data = @{}
    )

    # PSBase is required: a data field named "keys" or "count" would otherwise
    # shadow the hashtable property and silently drop every field from the line.
    $pairs = foreach ($key in ($Data.PSBase.Keys | Sort-Object)) {
        '{0}={1}' -f $key, ([string] $Data[$key] -replace '\s+', '_')
    }
    $line = '{0:o} event={1} {2}' -f [DateTime]::UtcNow, $Event, ($pairs -join ' ')
    Add-Content -LiteralPath $LogPath -Value $line.TrimEnd() -Encoding utf8
}
