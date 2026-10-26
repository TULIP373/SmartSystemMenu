$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'bin'
$executable = Join-Path $output 'MonitorSwitch.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run build.ps1 first' }
$package = Join-Path $output 'package'
New-Item -ItemType Directory -Path $package -Force | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $package 'MonitorSwitch.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $package 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\LICENSE') -Destination (Join-Path $package 'LICENSE') -Force
$archive = Join-Path $output 'MonitorSwitch-native-x64.zip'
# Explicit files keep old diagnostics and C# build outputs out of the release.
Compress-Archive -LiteralPath (Join-Path $package 'MonitorSwitch.exe'), (Join-Path $package 'README.md'), (Join-Path $package 'LICENSE') -DestinationPath $archive -Force
Write-Output $archive
