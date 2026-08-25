[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $StableCheckout,

    [string] $BaseUrl = 'http://127.0.0.1:5071'
)

$ErrorActionPreference = 'Stop'
$stable = (Resolve-Path -LiteralPath $StableCheckout).Path
$localSettings = Join-Path $stable 'backend\appsettings.Local.json'
if (-not [Uri]::IsWellFormedUriString($BaseUrl, [UriKind]::Absolute)) {
    throw "Task Server BaseUrl must be an absolute URL: $BaseUrl"
}

if ($PSCmdlet.ShouldProcess($localSettings, "Set TaskServer:BaseUrl to $BaseUrl")) {
    $settings = if (Test-Path -LiteralPath $localSettings) {
        Get-Content -LiteralPath $localSettings -Raw | ConvertFrom-Json
    } else {
        [pscustomobject]@{}
    }
    $taskServerSettings = [pscustomobject]@{ BaseUrl = $BaseUrl.TrimEnd('/') }
    if ($null -eq $settings.TaskServer) {
        $settings | Add-Member -NotePropertyName TaskServer -NotePropertyValue $taskServerSettings
    } else {
        $settings.TaskServer = $taskServerSettings
    }
    $settings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $localSettings -Encoding utf8
    Get-Content -LiteralPath $localSettings -Raw | ConvertFrom-Json | Select-Object -ExpandProperty TaskServer
}
