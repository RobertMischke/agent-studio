[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $TaskServerExecutable,

    [Parameter(Mandatory)]
    [string] $LegacySourceRoot,

    [Parameter(Mandatory)]
    [string] $EvidenceDirectory,

    [Parameter(Mandatory)]
    [string] $WorkspaceName
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $TaskServerExecutable).Path
$source = (Resolve-Path -LiteralPath $LegacySourceRoot).Path
$evidenceRoot = [System.IO.Path]::GetFullPath($EvidenceDirectory)
$pathSeparators = [char[]]@(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$sourcePrefix = $source.TrimEnd($pathSeparators) + [System.IO.Path]::DirectorySeparatorChar
$evidencePrefix = $evidenceRoot.TrimEnd($pathSeparators) + [System.IO.Path]::DirectorySeparatorChar
if ($evidenceRoot.Equals($source, [System.StringComparison]::OrdinalIgnoreCase) -or
    $evidencePrefix.StartsWith($sourcePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'EvidenceDirectory must be outside LegacySourceRoot so the rehearsal copy cannot include itself.'
}
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$evidenceRoot = (Resolve-Path -LiteralPath $EvidenceDirectory).Path
$runRoot = Join-Path $evidenceRoot ("task-server-migration-rehearsal-" + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssZ'))
$legacyCopy = Join-Path $runRoot 'legacy-copy'
$storePath = Join-Path $runRoot 'store'
$backupPath = Join-Path $storePath 'backups'
New-Item -ItemType Directory -Path $legacyCopy, $storePath, $backupPath -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $legacyCopy -Recurse -Force
foreach ($hidden in @('.metadata', '.git')) {
    $entry = Join-Path $source $hidden
    if (Test-Path -LiteralPath $entry) {
        Copy-Item -LiteralPath $entry -Destination (Join-Path $legacyCopy $hidden) -Recurse -Force
    }
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint] $listener.LocalEndpoint).Port
$listener.Stop()
$baseUrl = "http://127.0.0.1:$port"

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $executable
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.EnvironmentVariables['LISTEN_URL'] = $baseUrl
$startInfo.EnvironmentVariables['STORE_PATH'] = $storePath
$startInfo.EnvironmentVariables['BACKUP_PATH'] = $backupPath
$startInfo.EnvironmentVariables['AUTH'] = 'none'
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
$null = $process.Start()
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()

try {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        try {
            $ready = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/readyz" -TimeoutSec 3
            if ($ready.StatusCode -eq 200) { break }
        } catch { }
        if ($process.HasExited) {
            throw "Rehearsal Task Server exited early with code $($process.ExitCode): $($stderrTask.GetAwaiter().GetResult())"
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $ready -or $ready.StatusCode -ne 200) {
        throw "Rehearsal Task Server did not become ready at $baseUrl."
    }

    $evidencePath = Join-Path $runRoot 'migration-evidence.json'
    $migration = & (Join-Path $PSScriptRoot 'invoke-legacy-migration.ps1') `
        -LegacyRoot $legacyCopy `
        -WorkspaceName $WorkspaceName `
        -TaskServerUrl $baseUrl `
        -Import `
        -FreezeConfirmed `
        -EvidencePath $evidencePath
    if ($null -eq $migration.import -or [string]::IsNullOrWhiteSpace($migration.import.integritySha256)) {
        throw 'The rehearsal import did not produce an integrity digest.'
    }
    $migration
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(10000) | Out-Null
    }
    $stdoutTask.GetAwaiter().GetResult() |
        Set-Content -LiteralPath (Join-Path $runRoot 'task-server.stdout.log') -Encoding UTF8
    $stderrTask.GetAwaiter().GetResult() |
        Set-Content -LiteralPath (Join-Path $runRoot 'task-server.stderr.log') -Encoding UTF8
    $process.Dispose()
}
