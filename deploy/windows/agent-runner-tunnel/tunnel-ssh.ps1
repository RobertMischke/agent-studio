[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SshExecutable,

    [Parameter(Mandatory)]
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+:127\.0\.0\.1:[0-9]+$')]
    [string] $Forward,

    [Parameter(Mandatory)]
    [string] $StateDirectory
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
$logPath = Join-Path $StateDirectory 'ssh-exit.log'

function Write-SshLog {
    param(
        [Parameter(Mandatory)] [string] $Event,
        [Parameter(Mandatory)] [string] $Message
    )

    $singleLine = $Message -replace '[\r\n]+', ' ' -replace '\s+', ' '
    $line = '{0:o} event={1} target={2} forward={3} message={4}' -f `
        [DateTime]::UtcNow, $Event, $SshTarget, $Forward, $singleLine.Trim()
    Add-Content -LiteralPath $logPath -Value $line -Encoding utf8
}

$arguments = @(
    '-N', '-T',
    '-o', 'BatchMode=yes',
    '-o', 'ExitOnForwardFailure=yes',
    '-o', 'ServerAliveInterval=30',
    '-o', 'ServerAliveCountMax=3',
    '-R', $Forward,
    $SshTarget
)

try {
    Write-SshLog -Event 'start' -Message 'Starting the reverse-forward SSH process.'
    & $SshExecutable @arguments 2>&1 | ForEach-Object {
        Write-SshLog -Event 'output' -Message ([string] $_)
    }
    $exitCode = $LASTEXITCODE
    Write-SshLog -Event 'exit' -Message "SSH exited with code $exitCode."
    exit $exitCode
}
catch {
    Write-SshLog -Event 'launch-failed' -Message $_.Exception.Message
    exit 4
}
