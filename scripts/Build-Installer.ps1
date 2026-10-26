param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '1.0.0',
    [string]$CompilerPath = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$application = Join-Path $projectRoot 'artifacts\ADM-standalone\ADM.exe'
if (-not (Test-Path -LiteralPath $application)) { throw 'Build the standalone package before building the installer.' }
$binaryVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($application).FileVersion
if ([version]($Version + $(if ($Version.Split('.').Count -eq 3) { '.0' } else { '' })) -ne [version]$binaryVersion) {
    throw "Installer version $Version does not match application version $binaryVersion. Rebuild with the same -Version."
}
if (-not $CompilerPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $CompilerPath = $command.Source }
    else { $CompilerPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
}
if (-not (Test-Path -LiteralPath $CompilerPath)) { throw 'Inno Setup 6 is required. Install it from https://jrsoftware.org/isdl.php or pass -CompilerPath.' }
& $CompilerPath "/DAppVersion=$Version" (Join-Path $projectRoot 'installer\ADM.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $projectRoot 'artifacts\ADM-Setup-win-x64.exe'
if (-not (Test-Path -LiteralPath $installer)) { throw 'Installer output is missing.' }
Get-FileHash -LiteralPath $installer -Algorithm SHA256
