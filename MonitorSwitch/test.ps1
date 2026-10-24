$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /codepage:65001 /main:MonitorSwitchTests `
    "/out:$output\MonitorSwitchTests.exe" `
    "/r:$framework\System.Windows.Forms.dll" "/r:$framework\System.Drawing.dll" `
    "/r:$framework\WPF\UIAutomationClient.dll" "/r:$framework\WPF\UIAutomationTypes.dll" `
    "/r:$framework\WPF\WindowsBase.dll" `
    (Join-Path $PSScriptRoot 'MonitorSwitch.cs') (Join-Path $PSScriptRoot 'MonitorSwitchTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'MonitorSwitch test build failed' }
& (Join-Path $output 'MonitorSwitchTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'MonitorSwitch tests failed' }
