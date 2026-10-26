param([string]$Compiler = 'D:\Programs\Windhawk\Compiler\bin\clang++.exe')
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Compiler)) { throw "Specify an LLVM-MinGW clang++ compiler with -Compiler: $Compiler" }
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null

# Write a standard .res file so builds don't require a separate resource compiler.
$manifest = [Text.Encoding]::UTF8.GetBytes((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'app.manifest') -Raw))
$resource = Join-Path $output 'manifest.res'
$stream = [IO.File]::Create($resource)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint32]0); $writer.Write([uint32]32)
    $writer.Write([uint16]65535); $writer.Write([uint16]0)
    $writer.Write([uint16]65535); $writer.Write([uint16]0)
    $writer.Write([uint32]0); $writer.Write([uint16]0); $writer.Write([uint16]0)
    $writer.Write([uint32]0); $writer.Write([uint32]0)
    $writer.Write([uint32]$manifest.Length); $writer.Write([uint32]32)
    $writer.Write([uint16]65535); $writer.Write([uint16]24)
    $writer.Write([uint16]65535); $writer.Write([uint16]1)
    $writer.Write([uint32]0); $writer.Write([uint16]48); $writer.Write([uint16]0)
    $writer.Write([uint32]0); $writer.Write([uint32]0)
    $writer.Write($manifest)
    while ($stream.Position % 4) { $writer.Write([byte]0) }
} finally { $writer.Dispose() }
$common = @('-target', 'x86_64-w64-mingw32', '-std=c++17', '-DUNICODE', '-D_UNICODE', '-D_WIN32_WINNT=0x0A00', '-DWINVER=0x0A00',
    '-Os', '-Wall', '-Wextra', '-Werror', '-Wno-missing-field-initializers', '-static', '-municode', '-s',
    (Join-Path $PSScriptRoot 'Core.cpp'))
$libraries = @('-lole32', '-loleaut32', '-luiautomationcore', '-luuid', '-lshell32', '-lpropsys', '-ldwmapi', '-lversion')
& $Compiler @common (Join-Path $PSScriptRoot 'App.cpp') $resource '-mwindows' '-o' (Join-Path $output 'MonitorSwitch.exe') @libraries
if ($LASTEXITCODE -ne 0) { throw 'Native application build failed' }
& $Compiler @common (Join-Path $PSScriptRoot 'Tests.cpp') '-lpsapi' '-o' (Join-Path $output 'MonitorSwitchTests.exe') @libraries
if ($LASTEXITCODE -ne 0) { throw 'Native tests build failed' }
Write-Output "Built native application and tests in $output"
