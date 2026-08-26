[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $StableCheckout,
    [string] $BaseUrl = 'http://127.0.0.1:5071'
)

$ErrorActionPreference = 'Stop'
$settingsPath = Join-Path $StableCheckout 'backend\appsettings.Local.json'
if (-not (Test-Path -LiteralPath $settingsPath)) {
    throw "Stable local settings were not found: $settingsPath"
}
try {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
}
catch {
    throw "Stable local settings are not valid JSON: $settingsPath"
}

if ($null -eq $settings.TaskServer) {
    $settings | Add-Member -NotePropertyName TaskServer -NotePropertyValue ([pscustomobject]@{})
}
if ($null -eq $settings.TaskServer.PSObject.Properties['BaseUrl']) {
    $settings.TaskServer | Add-Member -NotePropertyName BaseUrl -NotePropertyValue $BaseUrl
} else {
    $settings.TaskServer.BaseUrl = $BaseUrl
}

if ($PSCmdlet.ShouldProcess($settingsPath, "Set TaskServer:BaseUrl to $BaseUrl")) {
    $backup = "$settingsPath.before-task-server-cutover"
    Copy-Item -LiteralPath $settingsPath -Destination $backup -Force
    $json = $settings | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($settingsPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ SettingsPath = $settingsPath; BackupPath = $backup; BaseUrl = $BaseUrl } | ConvertTo-Json
}
