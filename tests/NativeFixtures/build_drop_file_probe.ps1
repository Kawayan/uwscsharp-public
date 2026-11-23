param(
    [ValidateSet('x86', 'x64')]
    [string]$Architecture = 'x64',
    [string]$OutputDirectory = "$PSScriptRoot/../../artifacts/bin/UWSCSharp.Windows.Tests/debug/drop-file-probe"
)

$ErrorActionPreference = 'Stop'
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installations = @(& $locator -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if ($installations.Count -eq 0) { throw 'MSVCのC++ツールが見つかりません。' }
$developer = Join-Path $installations[0] 'Common7/Tools/VsDevCmd.bat'
$destination = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $Architecture
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$source = Join-Path $PSScriptRoot 'DropFileProbe.cpp'
$executable = Join-Path $destination 'DropFileProbe.exe'
$object = Join-Path $destination 'DropFileProbe.obj'
$command = 'call "' + $developer + '" -no_logo -host_arch=x64 -arch=' + $Architecture +
    ' >nul && cl /nologo /std:c++17 /utf-8 /W4 /WX /EHsc /MT /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 ' +
    '"' + $source + '" /Fo:"' + $object + '" /Fe:"' + $executable + '" ' +
    '/link user32.lib shell32.lib advapi32.lib'
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw 'DROPFILE受信画面のコンパイルに失敗しました。' }
Write-Output $executable
