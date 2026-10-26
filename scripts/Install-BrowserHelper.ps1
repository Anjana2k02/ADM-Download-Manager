param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-p]{32}$')][string]$ExtensionId,
    [string]$AppDirectory = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
$bridgePath = Join-Path $AppDirectory 'ADM.Bridge.exe'
if (-not (Test-Path -LiteralPath $bridgePath)) { throw "ADM.Bridge.exe was not found in $AppDirectory. Run this script from the published ADM folder, or pass -AppDirectory." }
$manifestPath = Join-Path $AppDirectory 'com.adm.download_manager.json'
$manifest = @{
    name = 'com.adm.download_manager'
    description = 'ADM browser download link helper'
    path = (Resolve-Path -LiteralPath $bridgePath).Path
    type = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
} | ConvertTo-Json -Depth 3
[System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))
foreach ($browserKey in @('HKCU:\Software\Google\Chrome\NativeMessagingHosts', 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts')) {
    $hostKey = Join-Path $browserKey 'com.adm.download_manager'
    New-Item -Path $hostKey -Force | Out-Null
    Set-Item -LiteralPath $hostKey -Value (Resolve-Path -LiteralPath $manifestPath).Path
}
Write-Host 'ADM browser helper connected for your Windows account. Reload the extension and send a link.'
