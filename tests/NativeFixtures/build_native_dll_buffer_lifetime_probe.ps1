param(
    [string]$OutputDirectory = "$PSScriptRoot/../artifacts/native/dll-buffer-lifetime"
)

$ErrorActionPreference = 'Stop'
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installations = @(& $locator -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($installations.Count -eq 0) { throw 'MSVCのC++ツールが見つかりません。' }
$developer = Join-Path $installations[0] 'Common7/Tools/VsDevCmd.bat'
$source = Join-Path $PSScriptRoot 'NativeDllBufferLifetimeProbe/fixture.cpp'
$destination = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$library = Join-Path $destination 'UwscOwnedBufferLifetimeProbe.dll'
$object = Join-Path $destination 'UwscOwnedBufferLifetimeProbe.obj'
$importLibrary = Join-Path $destination 'UwscOwnedBufferLifetimeProbe.lib'
$command = 'call "' + $developer + '" -no_logo -host_arch=x64 -arch=x64 >nul && ' +
    'cl /nologo /std:c++17 /utf-8 /W4 /WX /EHsc /MT /LD "' + $source +
    '" /Fo:"' + $object + '" /Fe:"' + $library + '" /link /IMPLIB:"' +
    $importLibrary + '" kernel32.lib'
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw 'DLL混在バッファ寿命検証用fixtureの生成に失敗しました。' }
Write-Output $library
