$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sources = 'Program.cs', 'SwitchForm.cs', 'TaskbarIntegration.cs', 'WindowOperations.cs', 'NativeMethods.cs' |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    "/out:$output\MonitorSwitch.exe" `
    "/win32manifest:$PSScriptRoot\app.manifest" `
    "/r:$framework\System.Windows.Forms.dll" "/r:$framework\System.Drawing.dll" `
    "/r:$framework\WPF\UIAutomationClient.dll" "/r:$framework\WPF\UIAutomationTypes.dll" `
    "/r:$framework\WPF\WindowsBase.dll" @sources
if ($LASTEXITCODE -ne 0) { throw 'MonitorSwitch build failed' }
Write-Output "Built $output\MonitorSwitch.exe"
