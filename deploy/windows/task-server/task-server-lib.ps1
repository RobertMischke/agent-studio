Set-StrictMode -Version Latest

function Import-ServerEnvironmentFile {
    param(
        [Parameter(Mandatory)] [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "The bootstrap contract file does not exist: $Path"
    }

    $imported = [Collections.Generic.List[string]]::new()
    $lineNumber = 0
    foreach ($rawLine in (Get-Content -LiteralPath $Path)) {
        $lineNumber++
        $line = $rawLine.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
        if ($line.StartsWith('export ')) { $line = $line.Substring(7).Trim() }
        $separator = $line.IndexOf('=')
        if ($separator -lt 1) {
            throw "server.env line ${lineNumber} is not a KEY=VALUE assignment."
        }
        $key = $line.Substring(0, $separator).Trim()
        if ($key -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
            throw "server.env line ${lineNumber} has an unusable variable name."
        }
        $value = $line.Substring($separator + 1).Trim()
        if ($value.Length -ge 2 -and
            (($value.StartsWith('"') -and $value.EndsWith('"')) -or
             ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        [Environment]::SetEnvironmentVariable($key, $value, 'Process')
        $imported.Add($key)
    }
    return $imported.ToArray()
}

function Resolve-TaskServerExecutable {
    param(
        [Parameter(Mandatory)] [string] $InstallRoot,
        [string] $FileName = 'task-server.exe'
    )

    $candidate = Join-Path $InstallRoot $FileName
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "The published Task Server executable was not found: $candidate"
    }
    return (Resolve-Path -LiteralPath $candidate).Path
}

function Write-ServiceEvent {
    param(
        [Parameter(Mandatory)] [string] $StateDirectory,
        [Parameter(Mandatory)] [string] $Line
    )

    New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    Add-Content `
        -LiteralPath (Join-Path $StateDirectory 'events.log') `
        -Value ('{0:o} {1}' -f [DateTime]::UtcNow, $Line) `
        -Encoding utf8
}

function Write-ServiceState {
    param(
        [Parameter(Mandatory)] [string] $StateDirectory,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [hashtable] $State
    )

    New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    $ordered = [ordered]@{ observedAt = [DateTime]::UtcNow.ToString('o') }
    foreach ($key in ($State.Keys | Sort-Object)) { $ordered[$key] = $State[$key] }
    $statePath = Join-Path $StateDirectory $Name
    $temporary = "$statePath.tmp"
    $ordered | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $statePath -Force
    return $statePath
}
