[CmdletBinding()]
param(
    [string] $InstallRoot = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentOrchestrator\current'),

    [string] $EnvironmentFile = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentOrchestrator\server.env'),

    [string] $StateDirectory = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentOrchestrator\state\task-server'),

    [string] $WorkingDirectory,

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $InstanceName = 'default'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'task-server-lib.ps1')

$mutexName = "Local\AgentOrchestratorTaskServer-$InstanceName"
$mutex = [Threading.Mutex]::new($false, $mutexName)
$ownsMutex = $false

try {
    $ownsMutex = $mutex.WaitOne(0)
    if (-not $ownsMutex) {
        Write-ServiceEvent -StateDirectory $StateDirectory `
            -Line "event=start_skipped instance=$InstanceName reason=another_start_owns_this_instance"
        exit 0
    }

    $imported = Import-ServerEnvironmentFile -Path $EnvironmentFile
    $executable = Resolve-TaskServerExecutable -InstallRoot $InstallRoot
    $listenUrl = [Environment]::GetEnvironmentVariable('LISTEN_URL')
    if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $storePath = [Environment]::GetEnvironmentVariable('STORE_PATH')
        $WorkingDirectory = if ([string]::IsNullOrWhiteSpace($storePath)) { $InstallRoot } else { $storePath }
    }
    New-Item -ItemType Directory -Path $WorkingDirectory -Force | Out-Null

    $attemptId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $stdoutPath = Join-Path $StateDirectory "task-server-$attemptId.stdout.log"
    $stderrPath = Join-Path $StateDirectory "task-server-$attemptId.stderr.log"
    New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null

    $process = Start-Process `
        -FilePath $executable `
        -WorkingDirectory $WorkingDirectory `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru
    Write-ServiceEvent -StateDirectory $StateDirectory `
        -Line ("event=server_started pid={0} executable={1} listen={2} settings={3} stdout={4} stderr={5}" -f `
            $process.Id, $executable, $listenUrl, $imported.Count, $stdoutPath, $stderrPath)
    Write-ServiceState -StateDirectory $StateDirectory -Name 'state.json' -State @{
        status = 'running'
        instance = $InstanceName
        processId = $process.Id
        executable = $executable
        listenUrl = $listenUrl
        workingDirectory = $WorkingDirectory
        environmentFile = $EnvironmentFile
        stdoutLog = $stdoutPath
        stderrLog = $stderrPath
    } | Out-Null

    $process.WaitForExit()
    $exitCode = $process.ExitCode
    Write-ServiceEvent -StateDirectory $StateDirectory `
        -Line "event=server_exited pid=$($process.Id) exit_code=$exitCode"
    $status = if ($exitCode -eq 0) { 'stopped' } else { 'failed' }
    Write-ServiceState -StateDirectory $StateDirectory -Name 'state.json' -State @{
        status = $status
        instance = $InstanceName
        processId = $process.Id
        executable = $executable
        listenUrl = $listenUrl
        exitCode = $exitCode
        stdoutLog = $stdoutPath
        stderrLog = $stderrPath
        message = 'The service manager owns the next start; captured output paths stay in this state file.'
    } | Out-Null
    exit $exitCode
}
catch {
    Write-ServiceEvent -StateDirectory $StateDirectory `
        -Line ("event=start_failed message={0}" -f ($_.Exception.Message -replace '\s+', '_'))
    Write-ServiceState -StateDirectory $StateDirectory -Name 'state.json' -State @{
        status = 'failed'
        instance = $InstanceName
        message = $_.Exception.Message
    } | Out-Null
    exit 4
}
finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
