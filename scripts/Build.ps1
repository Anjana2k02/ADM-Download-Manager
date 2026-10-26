param(
    [switch]$Standalone,
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $projectRoot ('artifacts\' + $(if ($Standalone) { 'ADM-standalone' } else { 'ADM-portable' }))
$resolvedOutput = [System.IO.Path]::GetFullPath($output)
$allowedOutputs = @([System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\ADM-standalone')), [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\ADM-portable')))
if ($resolvedOutput -notin $allowedOutputs) { throw 'Refusing to clean an unexpected output directory.' }
if (Test-Path -LiteralPath $resolvedOutput) { Remove-Item -LiteralPath $resolvedOutput -Recurse -Force }
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($project in @('ADM.App', 'ADM.Bridge')) {
    $projectFile = Join-Path $projectRoot "src\$project\$project.csproj"
    if ($Standalone) {
        dotnet publish $projectFile -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -o $output
    } else {
        dotnet publish $projectFile -c Release -r win-x64 -p:SelfContained=false -p:PublishSelfContained=false -p:PublishSingleFile=true "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -o $output
    }
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}
foreach ($file in @('README.md', 'BROWSER-SETUP.md', 'GITHUB-RELEASES.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $output -Force }
foreach ($file in @('Install-BrowserHelper.ps1', 'Uninstall-BrowserHelper.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $output -Force }
Copy-Item -LiteralPath (Join-Path $projectRoot 'browser-extension') -Destination $output -Recurse -Force
$archive = Join-Path $projectRoot ('artifacts\' + $(if ($Standalone) { 'ADM-standalone-win-x64.zip' } else { 'ADM-portable-win-x64.zip' }))
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
Write-Host "Ready: $archive"
