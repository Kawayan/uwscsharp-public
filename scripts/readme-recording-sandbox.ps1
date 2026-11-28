# Windows Sandbox の LogonCommand から Windows PowerShell で実行します。
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'Windows Sandbox でのみ実行できます。' }

$inputDir = 'C:\UwscDemoInput'
$output = 'C:\UwscDemoOutput'
try {
    $app = Join-Path $env:TEMP 'UwscReadmeCapture'
    Copy-Item -LiteralPath (Join-Path $inputDir 'app') -Destination $app -Recurse
    $env:DOTNET_ROOT = Join-Path $inputDir 'dotnet'
    $desktop = [Environment]::GetFolderPath('Desktop')
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path $desktop 'メモ帳.lnk'))
    $link.TargetPath = Join-Path $inputDir 'notepad\notepad.exe'
    $link.WorkingDirectory = $app
    $link.Save()

    $process = Start-Process -FilePath (Join-Path $app 'UWSCSharp.Wpf.exe') -WorkingDirectory $app `
        -ArgumentList '--capture-recording-demo C:\UwscDemoInput C:\UwscDemoOutput\capture.json' -PassThru -Wait
    Set-Content -LiteralPath (Join-Path $output 'finished.txt') -Value $process.ExitCode
}
catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $output 'capture.json.error') -Encoding utf8
    exit 1
}
