param(
    [string]$OutputDirectory = "$PSScriptRoot/../artifacts/native/floating-point",
    [switch]$IncludeX86Call
)

$ErrorActionPreference = 'Stop'
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installations = @(& $locator -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($installations.Count -eq 0) { throw 'MSVCのC++ツールが見つかりません。' }
$developer = Join-Path $installations[0] 'Common7/Tools/VsDevCmd.bat'
$native = Join-Path $PSScriptRoot '../src/UWSCSharp.Windows/Native/FloatingPoint'
$destination = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$library = Join-Path $destination 'UWSCSharp.FloatingPoint.Native.dll'
$importLibrary = Join-Path $destination 'UWSCSharp.FloatingPoint.Native.lib'
$sources = @((Join-Path $native 'arithmetic.asm'), (Join-Path $native 'boundary.cpp'))
$exports = '/export:GetFloatingPointAbiVersion /export:GetControlState /export:GetX87Status /export:EvaluateBinarySafely /export:EvaluateBuiltinSafely /export:InvokeDispatchWithControl'
$commands = @('call "' + $developer + '" -no_logo -host_arch=x64 -arch=x64 >nul')
$objects = @()
for ($index = 0; $index -lt $sources.Count; $index++) {
    $object = Join-Path $destination "floating-point-$index.obj"
    $objects += '"' + $object + '"'
    if ([IO.Path]::GetExtension($sources[$index]) -eq '.asm') {
        $commands += 'ml64 /nologo /c /Fo"' + $object + '" "' + $sources[$index] + '"'
    } else {
        $commands += 'cl /nologo /c /utf-8 /W4 /WX /O2 /MT /EHsc /Fo:"' + $object + '" "' + $sources[$index] + '"'
    }
}
$commands += 'link /nologo /dll /machine:x64 ' + ($objects -join ' ') + ' /out:"' +
    $library + '" /implib:"' + $importLibrary + '" libvcruntime.lib libucrt.lib kernel32.lib ' + $exports
& $env:ComSpec /d /s /c ($commands -join ' && ')
if ($LASTEXITCODE -ne 0) { throw '浮動小数点native DLLの生成に失敗しました。' }
Write-Output $library

if ($IncludeX86Call) {
    $x86Source = Join-Path $native 'x86_call.cpp'
    $x86Library = Join-Path $destination 'UWSCSharp.X86Call.Native.dll'
    $x86Object = [IO.Path]::ChangeExtension($x86Library, '.obj')
    $x86Import = [IO.Path]::ChangeExtension($x86Library, '.lib')
    $x86Command = 'call "' + $developer + '" -no_logo -host_arch=x64 -arch=x86 >nul' +
        ' && cl /nologo /utf-8 /W4 /WX /O2 /MT /EHsc /LD "' + $x86Source +
        '" /Fo:"' + $x86Object + '" /Fe:"' + $x86Library + '" /link /IMPLIB:"' +
        $x86Import + '"'
    & $env:ComSpec /d /s /c $x86Command
    if ($LASTEXITCODE -ne 0) { throw '32ビットDLL呼出し用ライブラリの生成に失敗しました。' }
    Write-Output $x86Library
}
