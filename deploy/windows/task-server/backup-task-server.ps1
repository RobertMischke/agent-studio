[CmdletBinding()]
param(
    [string] $InstallRoot = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentOrchestrator\current'),

    [string] $EnvironmentFile = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentOrchestrator\server.env'),

    [string] $StateDirectory = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgentOrchestrator\state\task-server'),

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $Name = 'timer'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'task-server-lib.ps1')

try {
    Import-ServerEnvironmentFile -Path $EnvironmentFile | Out-Null
    $executable = Resolve-TaskServerExecutable -InstallRoot $InstallRoot
    New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    $attemptId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $stdoutPath = Join-Path $StateDirectory "backup-$attemptId.stdout.log"
    $stderrPath = Join-Path $StateDirectory "backup-$attemptId.stderr.log"

    $process = Start-Process `
        -FilePath $executable `
        -ArgumentList @('backup', '--name', $Name) `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    if ($exitCode -ne 0) {
        throw "task-server backup --name $Name exited with code ${exitCode}; see $stderrPath"
    }

    Write-ServiceEvent -StateDirectory $StateDirectory `
        -Line "event=backup_completed name=$Name exit_code=$exitCode stdout=$stdoutPath"
    $text = (Get-Content -LiteralPath $stdoutPath -Raw).Trim()
    Set-Content -LiteralPath (Join-Path $StateDirectory 'last-backup.json') -Value $text -Encoding utf8
    Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    Write-Output $text
    exit 0
}
catch {
    Write-ServiceEvent -StateDirectory $StateDirectory `
        -Line ("event=backup_failed name={0} message={1}" -f $Name, ($_.Exception.Message -replace '\s+', '_'))
    Write-Error $_.Exception.Message
    exit 4
}
