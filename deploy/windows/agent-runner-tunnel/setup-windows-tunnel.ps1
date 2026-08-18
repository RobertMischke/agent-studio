[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [ValidateRange(1, 65535)]
    [int] $TaskServerPort = 5031,

    [ValidateRange(1, 60)]
    [int] $IntervalMinutes = 5,

    [string] $KeeperTaskName = 'AgentRunner-TunnelKeeper',

    [ValidateRange(10, 3600)]
    [int] $ProbeIntervalSeconds = 60,

    [ValidateRange(1, 20)]
    [int] $FailureThreshold = 2,

    [string] $WatchdogTaskName = 'AgentRunner-TunnelWatchdog',

    [string] $DevspacePath,

    [string] $OperatorAlarmPath,

    [string] $BashExecutable = 'C:\Program Files\Git\bin\bash.exe',

    # Internal: set by the elevated re-invocation to hand its JSON result back
    # to the originating non-elevated process instead of a shared console.
    [string] $ResultPath
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Register-OneTask {
    param(
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $ScriptPath,
        [Parameter(Mandatory)] [hashtable] $BoundParameters,
        [Parameter(Mandatory)] [string] $TaskName
    )

    $result = [ordered]@{
        label = $Label
        taskName = $TaskName
        registered = $false
        error = $null
    }
    try {
        & $ScriptPath @BoundParameters | Out-Null
        $result.registered = $true
    }
    catch {
        $result.error = $_.Exception.Message
    }
    return $result
}

function Invoke-Registration {
    $keeperParams = @{
        SshTarget = $SshTarget
        RemotePort = $RemotePort
        TaskServerPort = $TaskServerPort
        IntervalMinutes = $IntervalMinutes
        TaskName = $KeeperTaskName
    }
    $watchdogParams = @{
        SshTarget = $SshTarget
        RemotePort = $RemotePort
        KeeperTaskName = $KeeperTaskName
        ProbeIntervalSeconds = $ProbeIntervalSeconds
        FailureThreshold = $FailureThreshold
        TaskName = $WatchdogTaskName
        BashExecutable = $BashExecutable
    }
    if ($DevspacePath) { $watchdogParams.DevspacePath = $DevspacePath }
    if ($OperatorAlarmPath) { $watchdogParams.OperatorAlarmPath = $OperatorAlarmPath }

    $keeper = Register-OneTask -Label 'keeper' `
        -ScriptPath (Join-Path $scriptDir 'register-tunnel-keeper.ps1') `
        -BoundParameters $keeperParams -TaskName $KeeperTaskName
    $watchdog = Register-OneTask -Label 'watchdog' `
        -ScriptPath (Join-Path $scriptDir 'register-tunnel-watchdog.ps1') `
        -BoundParameters $watchdogParams -TaskName $WatchdogTaskName

    return [ordered]@{
        elevated = $true
        completedAt = [DateTime]::UtcNow.ToString('o')
        keeper = $keeper
        watchdog = $watchdog
        ok = ($keeper.registered -and $watchdog.registered)
    }
}

# ---------------------------------------------------------------------------
# Elevated re-invocation: register both tasks and hand the JSON result back to
# the waiting non-elevated parent through -ResultPath, because Start-Process
# -Verb RunAs cannot redirect standard output across the UAC boundary.
# ---------------------------------------------------------------------------
if ($ResultPath) {
    $outcome = $null
    try {
        $outcome = Invoke-Registration
    }
    catch {
        $outcome = [ordered]@{
            elevated = $true
            completedAt = [DateTime]::UtcNow.ToString('o')
            ok = $false
            error = $_.Exception.Message
        }
    }
    ($outcome | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $ResultPath -Encoding utf8
    exit 0
}

# ---------------------------------------------------------------------------
# Already elevated (operator launched this script from an elevated session):
# register directly and print the result as one JSON line.
# ---------------------------------------------------------------------------
if (Test-Elevated) {
    (Invoke-Registration | ConvertTo-Json -Depth 6)
    exit 0
}

# ---------------------------------------------------------------------------
# Not elevated: explain why one UAC prompt is required, then re-launch this
# same script elevated. Registering an AtStartup Scheduled Task needs that
# authority once; the keeper and watchdog themselves still run unprivileged
# (LogonType S4U, RunLevel Limited).
# ---------------------------------------------------------------------------
Write-Host 'Windows tunnel keeper and watchdog setup needs administrator rights once, to register two AtStartup Scheduled Tasks.'
Write-Host 'A Windows "User Account Control" consent prompt is about to open. Approve it to continue; the two scheduled tasks it creates run under your own account with limited rights, not as administrator.'

$temporaryResult = [IO.Path]::Combine([IO.Path]::GetTempPath(), "agent-runner-tunnel-setup-$([Guid]::NewGuid().ToString('N')).json")
$forwardedArguments = @(
    '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
    '-File', ('"{0}"' -f $PSCommandPath),
    '-SshTarget', $SshTarget,
    '-RemotePort', $RemotePort,
    '-TaskServerPort', $TaskServerPort,
    '-IntervalMinutes', $IntervalMinutes,
    '-KeeperTaskName', ('"{0}"' -f $KeeperTaskName),
    '-ProbeIntervalSeconds', $ProbeIntervalSeconds,
    '-FailureThreshold', $FailureThreshold,
    '-WatchdogTaskName', ('"{0}"' -f $WatchdogTaskName),
    '-BashExecutable', ('"{0}"' -f $BashExecutable),
    '-ResultPath', ('"{0}"' -f $temporaryResult)
)
if ($DevspacePath) { $forwardedArguments += @('-DevspacePath', ('"{0}"' -f $DevspacePath)) }
if ($OperatorAlarmPath) { $forwardedArguments += @('-OperatorAlarmPath', ('"{0}"' -f $OperatorAlarmPath)) }

$outcome = $null
try {
    if ($PSCmdlet.ShouldProcess('AgentRunner-TunnelKeeper, AgentRunner-TunnelWatchdog', 'Elevate and register Scheduled Tasks')) {
        $elevated = Start-Process -FilePath 'powershell.exe' -ArgumentList $forwardedArguments -Verb RunAs -Wait -PassThru
        if (Test-Path -LiteralPath $temporaryResult) {
            $outcome = Get-Content -LiteralPath $temporaryResult -Raw | ConvertFrom-Json
        }
        elseif ($elevated.ExitCode -ne 0) {
            $outcome = [ordered]@{
                elevated = $true
                ok = $false
                error = "The elevated registration process exited with code $($elevated.ExitCode) and left no result."
            }
        }
    }
}
catch [System.ComponentModel.Win32Exception] {
    # ERROR_CANCELLED (1223): the operator dismissed the UAC consent prompt.
    $outcome = [ordered]@{
        elevated = $false
        ok = $false
        error = 'Elevation was declined at the Windows consent prompt. No scheduled task was registered.'
    }
}
finally {
    Remove-Item -LiteralPath $temporaryResult -ErrorAction SilentlyContinue
}

if (-not $outcome) {
    $outcome = [ordered]@{ elevated = $false; ok = $false; error = 'Elevation did not produce a result.' }
}
($outcome | ConvertTo-Json -Depth 6)
if (-not $outcome.ok) { exit 1 }
exit 0
