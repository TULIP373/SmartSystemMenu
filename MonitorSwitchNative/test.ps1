param([int]$Iterations = 500)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'bin\MonitorSwitchTests.exe') $Iterations
if ($LASTEXITCODE -ne 0) { throw 'Native MonitorSwitch checks failed' }
