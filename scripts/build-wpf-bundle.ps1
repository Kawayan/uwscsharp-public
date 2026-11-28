#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Version,

    [string]$InstallerVersion,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [switch]$AcceptWixEula
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "未作成の出力先を指定してください: $output" }

$package = Join-Path $output 'package'
$installerArguments = @{ OutputDirectory = $package; AcceptWixEula = $AcceptWixEula }
if ($PSBoundParameters.ContainsKey('Version')) { $installerArguments.Version = $Version }
if ($PSBoundParameters.ContainsKey('InstallerVersion')) { $installerArguments.InstallerVersion = $InstallerVersion }
& "$PSScriptRoot/build-wpf-installer.ps1" @installerArguments
if (!$InstallerVersion) {
    $release = Get-Content -LiteralPath (Join-Path $package 'app/package/release.json') -Raw | ConvertFrom-Json
    $InstallerVersion = ($release.version -split '-', 2)[0]
}

$msiName = "UWSCSharp-$InstallerVersion-win-x64.msi"
$msi = Join-Path $package "msi/$msiName"
$project = Join-Path $PSScriptRoot '../installer/UWSCSharp.Bundle/UWSCSharp.Bundle.wixproj'
$bundleDirectory = Join-Path $output 'bundle'
dotnet build $project -c Release -m:1 /nr:false -p:AcceptEula=wix7 `
    "-p:InstallerMsi=$msi" "-p:InstallerVersion=$InstallerVersion" `
    "-p:OutputPath=$bundleDirectory/" "-p:BaseIntermediateOutputPath=$output/obj/"
if ($LASTEXITCODE -ne 0) { throw 'バンドルEXEの作成に失敗しました。' }

$name = "UWSCSharp-$InstallerVersion-win-x64-setup.exe"
$bundle = Join-Path $bundleDirectory $name
$wixBin = dotnet msbuild $project -nologo -getProperty:WixBinDir
if ($LASTEXITCODE -ne 0 -or -not $wixBin) { throw 'ビルドに使用したWiXツールの場所を取得できません。' }

# バンドルを起動せず、内蔵MSIを取り出して梱包元と照合します。
$extracted = Join-Path $output 'extracted'
$metadata = Join-Path $output 'bootstrapper'
# ブートストラッパーのマニフェストも取り出すと、内蔵ファイルが元の名前で展開されます。
dotnet (Join-Path $wixBin 'wix.dll') -acceptEula wix7 burn extract $bundle -out $extracted -outba $metadata
if ($LASTEXITCODE -ne 0) { throw 'バンドルEXEから収録ファイルを取り出せません。' }
$embedded = @(Get-ChildItem -LiteralPath $extracted -Filter '*.msi' -File -Recurse)
if ($embedded.Count -ne 1 -or $embedded[0].Name -cne $msiName -or
    (Get-FileHash -LiteralPath $embedded[0].FullName).Hash -ne (Get-FileHash -LiteralPath $msi).Hash) {
    throw 'バンドルEXEに内蔵したMSIが梱包元と一致しません。'
}

$hash = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$bundle.sha256" -Value "$hash  $name" -Encoding ascii
Write-Output "内蔵MSIを照合し、バンドルEXEを作成しました: $bundle"
