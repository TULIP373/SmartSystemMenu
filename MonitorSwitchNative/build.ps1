param([string]$Compiler = 'D:\Programs\Windhawk\Compiler\bin\clang++.exe')
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Compiler)) { throw "Specify an LLVM-MinGW clang++ compiler with -Compiler: $Compiler" }
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null

# Write standard .res records so builds don't require a separate resource compiler.
function Write-Resource([IO.BinaryWriter]$writer, [uint16]$type, [uint16]$name, [byte[]]$bytes) {
    $writer.Write([uint32]$bytes.Length); $writer.Write([uint32]32)
    $writer.Write([uint16]65535); $writer.Write($type)
    $writer.Write([uint16]65535); $writer.Write($name)
    $writer.Write([uint32]0); $writer.Write([uint16]48); $writer.Write([uint16]0)
    $writer.Write([uint32]0); $writer.Write([uint32]0)
    $writer.Write($bytes)
    while ($writer.BaseStream.Position % 4) { $writer.Write([byte]0) }
}
$manifest = [Text.Encoding]::UTF8.GetBytes((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'app.manifest') -Raw))
$resource = Join-Path $output 'application.res'
$stream = [IO.File]::Create($resource)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint32]0); $writer.Write([uint32]32)
    $writer.Write([uint16]65535); $writer.Write([uint16]0)
    $writer.Write([uint16]65535); $writer.Write([uint16]0)
    $writer.Write([uint32]0); $writer.Write([uint16]0); $writer.Write([uint16]0)
    $writer.Write([uint32]0); $writer.Write([uint32]0)
    Write-Resource $writer 24 1 $manifest
    $icon = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'assets\MonitorSwitch.ico'))
    if ([BitConverter]::ToUInt16($icon, 0) -ne 0 -or [BitConverter]::ToUInt16($icon, 2) -ne 1) { throw 'Invalid icon file' }
    $count = [BitConverter]::ToUInt16($icon, 4)
    $group = [IO.MemoryStream]::new()
    $groupWriter = [IO.BinaryWriter]::new($group)
    try {
        $groupWriter.Write([uint16]0); $groupWriter.Write([uint16]1); $groupWriter.Write($count)
        for ($index = 0; $index -lt $count; $index++) {
            $entry = 6 + 16 * $index
            $length = [BitConverter]::ToUInt32($icon, $entry + 8)
            $offset = [BitConverter]::ToUInt32($icon, $entry + 12)
            $image = [byte[]]::new($length)
            [Array]::Copy($icon, $offset, $image, 0, $length)
            $id = [uint16]($index + 1)
            Write-Resource $writer 3 $id $image
            $groupWriter.Write($icon, $entry, 12)
            $groupWriter.Write($id)
        }
        $groupWriter.Flush()
        Write-Resource $writer 14 101 $group.ToArray()
    } finally { $groupWriter.Dispose() }
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
