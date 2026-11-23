#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$InstallerVersion,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [switch]$AcceptWixEula
)

$ErrorActionPreference = 'Stop'
if (-not $AcceptWixEula) {
    throw 'WiX 7の利用条件を確認し、同意する場合は-AcceptWixEulaを指定してください。https://docs.firegiant.com/wix/osmf/'
}

$major, $minor, $build = $InstallerVersion.Split('.')
if ([decimal]$major -gt 255 -or [decimal]$minor -gt 255 -or [decimal]$build -gt 65535) {
    throw 'MSIの数値版は0～255.0～255.0～65535の範囲で指定してください。'
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "未作成の出力先を指定してください: $output" }

# ZIPと同じ発行処理で、アプリ・DLL中継・同梱ライセンスを用意します。
$app = Join-Path $output 'app'
& "$PSScriptRoot/package-wpf-release.ps1" -Version $Version -OutputDirectory $app
$payload = Join-Path $app 'package'
$project = Join-Path $PSScriptRoot '../installer/UWSCSharp.Installer/UWSCSharp.Installer.wixproj'
dotnet build $project -c Release -m:1 /nr:false -p:AcceptEula=wix7 `
    "-p:PayloadDirectory=$payload" "-p:InstallerVersion=$InstallerVersion" `
    "-p:OutputPath=$output/msi/" "-p:BaseIntermediateOutputPath=$output/obj/"
if ($LASTEXITCODE -ne 0) { throw 'MSIの作成に失敗しました。' }

$name = "UWSCSharp-$InstallerVersion-win-x64.msi"
$msi = Join-Path $output "msi/$name"
& "$PSScriptRoot/check-wpf-installer.ps1" -Msi $msi -PayloadDirectory $payload -InstallerVersion $InstallerVersion
$hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$msi.sha256" -Value "$hash  $name" -Encoding ascii
Write-Output "MSIを作成しました: $msi"
