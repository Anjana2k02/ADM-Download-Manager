$ErrorActionPreference = 'Stop'
foreach ($hostKey in @('HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.adm.download_manager', 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.adm.download_manager')) {
    if (Test-Path -LiteralPath $hostKey) { Remove-Item -LiteralPath $hostKey }
}
Write-Host 'ADM native host registrations removed. Remove the extension in your browser if desired.'
