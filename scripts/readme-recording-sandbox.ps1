# Windows Sandbox の LogonCommand から Windows PowerShell で実行します。
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'Windows Sandbox でのみ実行できます。' }

$inputDir = 'C:\UwscDemoInput'
$output = 'C:\UwscDemoOutput'
try {
    Add-Type -TypeDefinition @'
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class CaptureWallpaper
{
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, string path, uint flags);

    public static void Set(string path)
    {
        if (!SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
'@
    [CaptureWallpaper]::Set((Join-Path $inputDir 'img0.jpg'))

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
