param(
    [string]$OutputDirectory = "$PSScriptRoot/../artifacts/def-dll-abi"
)

$ErrorActionPreference = 'Stop'
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installations = @(& $locator -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($installations.Count -eq 0) { throw 'MSVCのC++ツールが見つかりません。' }
$developer = Join-Path $installations[0] 'Common7/Tools/VsDevCmd.bat'
$source = Join-Path $PSScriptRoot 'DefDllAbiProbe/fixture.cpp'
$comSource = Join-Path $PSScriptRoot 'DefDllAbiProbe/com_control.cpp'
$comIdl = Join-Path $PSScriptRoot 'DefDllAbiProbe/com_control.idl'
$comFaultSource = Join-Path $PSScriptRoot 'FloatingPointControlProbe/com_dispatch_control.asm'
foreach ($architecture in @('x64', 'x86')) {
    $destination = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $architecture
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $library = Join-Path $destination 'DefDllAbiFixture.dll'
    $objectDirectory = $destination + [IO.Path]::DirectorySeparatorChar
    $importLibrary = Join-Path $destination 'DefDllAbiFixture.lib'
    $command = 'call "' + $developer + '" -no_logo -host_arch=x64 -arch=' + $architecture + ' >nul'
    $typeLibrary = Join-Path $destination 'com_control.tlb'
    $resourceSource = Join-Path $destination 'com_control.rc'
    $resource = Join-Path $destination 'com_control.res'
    $resourcePath = $typeLibrary.Replace([char]92, [char]47)
    Set-Content -LiteralPath $resourceSource -Encoding ascii ('1 TYPELIB "' + $resourcePath + '"')
    $midlEnvironment = if ($architecture -eq 'x64') { 'x64' } else { 'win32' }
    $command += ' && midl /nologo /env ' + $midlEnvironment +
        ' /out "' + $destination + '" /tlb com_control.tlb "' + $comIdl + '"'
    $command += ' && rc /nologo /fo "' + $resource + '" "' + $resourceSource + '"'
    $comFaultInput = ''
    if ($architecture -eq 'x64') {
        $comFaultObject = Join-Path $destination 'com_fault.obj'
        $command += ' && ml64 /nologo /c /Fo"' + $comFaultObject + '" "' + $comFaultSource + '"'
        $comFaultInput = ' "' + $comFaultObject + '"'
    }
    $command += ' && cl /nologo /std:c++17 /utf-8 /W4 /WX /EHsc /MT /LD "' + $source +
        '" "' + $comSource + '" /Fo:"' + $objectDirectory + '\" /Fe:"' + $library +
        '"' + $comFaultInput + ' "' + $resource + '" /link oleaut32.lib /IMPLIB:"' + $importLibrary + '"'
    & $env:ComSpec /d /s /c $command
    if ($LASTEXITCODE -ne 0) { throw "DEF_DLL検証用DLLの生成に失敗しました: $architecture" }
    Write-Output $library
}
