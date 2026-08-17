[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $AppsettingsPath,

    [string] $BaseUrl = 'http://127.0.0.1:5071'
)

$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $AppsettingsPath).Path
$uri = $null
if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref] $uri)
    -or $uri.Scheme -notin @('http', 'https')) {
    throw "TaskServer:BaseUrl must be an absolute HTTP or HTTPS URL."
}

$configuration = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
if ($null -eq $configuration.TaskServer) {
    $configuration | Add-Member -MemberType NoteProperty -Name TaskServer -Value ([pscustomobject]@{})
}
if ($null -eq $configuration.TaskServer.PSObject.Properties['BaseUrl']) {
    $configuration.TaskServer | Add-Member -MemberType NoteProperty -Name BaseUrl -Value $BaseUrl.TrimEnd('/')
} else {
    $configuration.TaskServer.BaseUrl = $BaseUrl.TrimEnd('/')
}

if ($PSCmdlet.ShouldProcess($path, "Set TaskServer:BaseUrl to $BaseUrl")) {
    $temporary = "$path.tmp"
    $configuration | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $path -Force
}

[pscustomobject]@{ AppsettingsPath = $path; BaseUrl = $BaseUrl.TrimEnd('/') }
