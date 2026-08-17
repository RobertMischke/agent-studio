[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ServiceRoot
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ServiceRoot).Path
$pointerPath = Join-Path $root 'current-release.txt'
$environmentPath = Join-Path $root 'server.env'
$logDirectory = Join-Path $root 'logs'

if (-not (Test-Path -LiteralPath $pointerPath -PathType Leaf)) {
    throw "Task Server release pointer is missing: $pointerPath"
}
if (-not (Test-Path -LiteralPath $environmentPath -PathType Leaf)) {
    throw "Task Server environment file is missing: $environmentPath"
}

$releasePath = (Get-Content -LiteralPath $pointerPath -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($releasePath)) {
    throw "Task Server release pointer is empty: $pointerPath"
}
$executable = Join-Path $releasePath 'task-server.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Task Server executable is missing: $executable"
}

foreach ($line in Get-Content -LiteralPath $environmentPath) {
    $trimmed = $line.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) { continue }
    $separator = $trimmed.IndexOf('=')
    if ($separator -lt 1) { throw "Invalid server.env entry: $trimmed" }
    $name = $trimmed.Substring(0, $separator).Trim()
    $value = $trimmed.Substring($separator + 1)
    if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_:]*$') {
        throw "Invalid server.env key: $name"
    }
    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
}

New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$date = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
$stdout = Join-Path $logDirectory "task-server-$date.log"
$stderr = Join-Path $logDirectory "task-server-$date.err.log"

& $executable 1>> $stdout 2>> $stderr
exit $LASTEXITCODE
