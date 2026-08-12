[CmdletBinding()]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [ValidateRange(1, 65535)]
    [int] $TaskServerPort = 5031,

    [string] $SshExecutable = 'ssh.exe',

    [string] $StateDirectory = (Join-Path $env:LOCALAPPDATA 'AgentTaskboard\tunnel-keeper')
)

$ErrorActionPreference = 'Stop'
$forward = "${RemotePort}:127.0.0.1:${TaskServerPort}"
$logPath = Join-Path $StateDirectory 'ssh-exit.log'

function Write-SshLog {
    param([Parameter(Mandatory)] [string] $Message)

    $line = '{0:o} {1}' -f [DateTime]::UtcNow, ($Message -replace "[\r\n]+", ' ')
    Add-Content -LiteralPath $logPath -Value $line -Encoding utf8
}

New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null

try {
    $sshPath = (Get-Command $SshExecutable -ErrorAction Stop).Source
    $arguments = @(
        '-N', '-T',
        '-o', 'BatchMode=yes',
        '-o', 'ExitOnForwardFailure=yes',
        '-o', 'ServerAliveInterval=30',
        '-o', 'ServerAliveCountMax=3',
        '-R', $forward,
        $SshTarget
    )

    Write-SshLog "event=ssh-start wrapper_pid=$PID target=$SshTarget forward=$forward"
    & $sshPath @arguments 2>&1 | ForEach-Object {
        Write-SshLog "event=ssh-output message=$([string] $_)"
    }
    $exitCode = $LASTEXITCODE
    Write-SshLog "event=ssh-exit exit_code=$exitCode target=$SshTarget forward=$forward"
    exit $exitCode
}
catch {
    Write-SshLog "event=ssh-launch-failed message=$($_.Exception.Message)"
    exit 4
}
