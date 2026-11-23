#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "未作成の出力先を指定してください: $output"
}

$project = Join-Path $root 'src/UWSCSharp.Wpf/UWSCSharp.Wpf.csproj'
$build = Join-Path $output 'build'
$publish = Join-Path $output 'publish'
$package = Join-Path $output 'package'
$nuget = Join-Path $output 'nuget'
New-Item -ItemType Directory -Path $output | Out-Null

$revision = git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw '発行元コミットを取得できません。' }

# 復元したランタイムパックから、実際に同梱する版のライセンスを収録します。
dotnet publish $project -c Release -r win-x64 -p:PublishProfile=SingleFile-win-x64 -p:DebugType=embedded `
    "-p:Version=$Version" "-p:ArtifactsPath=$build" `
    "-p:RestorePackagesPath=$nuget" -o $publish
if ($LASTEXITCODE -ne 0) { throw 'WPF版の発行に失敗しました。' }

$exe = Get-Item -LiteralPath (Join-Path $publish 'UWSCSharp.Wpf.exe')
if (($exe.VersionInfo.ProductVersion -split '\+', 2)[0] -ne $Version) {
    throw 'EXEと配布版番号が一致しません。'
}

$runtimeConfig = Join-Path $build 'bin/UWSCSharp.Wpf/release_win-x64/UWSCSharp.Wpf.runtimeconfig.json'
$runtime = Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json
$frameworks = @($runtime.runtimeOptions.includedFrameworks)
$core = $frameworks | Where-Object name -eq 'Microsoft.NETCore.App'
$desktop = $frameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App'
if ($frameworks.Count -ne 2 -or $core.version -notmatch '^10\.0\.' -or $desktop.version -notmatch '^10\.0\.') {
    throw '同梱する .NET 10 と Windows Desktop の構成を確認できません。'
}

# 32ビットDLL中継の付属ファイルはバンドル外に置きます。
$relayBuild = Join-Path $build 'bin/UWSCSharp.Wpf/release_win-x64/dll-relay'
$relay = Join-Path $package 'dll-relay'
New-Item -ItemType Directory -Path $relay | Out-Null
$relayFiles = @(
    'UWSCSharp.DllRelay.dll', 'UWSCSharp.DllRelay.deps.json',
    'UWSCSharp.DllRelay.runtimeconfig.json', 'UWSCSharp.Core.dll',
    'UWSCSharp.Windows.dll'
)
foreach ($file in $relayFiles) {
    Copy-Item -LiteralPath (Join-Path $relayBuild $file) -Destination (Join-Path $relay $file)
}
Copy-Item -LiteralPath (Join-Path $publish 'dll-relay/UWSCSharp.X86Call.Native.dll') -Destination $relay

Copy-Item -LiteralPath $exe.FullName -Destination $package
Copy-Item -LiteralPath $exe.FullName -Destination (Join-Path $package 'UWSCSharp.Association.exe')
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $package
$examples = Join-Path $package 'examples'
$licenses = Join-Path $package 'licenses'
New-Item -ItemType Directory -Path $examples, $licenses | Out-Null
Set-Content -LiteralPath (Join-Path $examples 'hello.uws') -Encoding utf8 -Value @(
    'PRINT "UWSC# プレビュー"',
    'PRINT "日本語の表示を確認しました"'
)

$corePack = Join-Path $nuget "microsoft.netcore.app.runtime.win-x64/$($core.version)"
$desktopPack = Join-Path $nuget "microsoft.windowsdesktop.app.runtime.win-x64/$($desktop.version)"

# SDKに付属するホストパックから省かれている通知を、同じ版のNuGetパッケージから取得します。
$sdkItems = dotnet msbuild $project -nologo '-getItem:KnownAppHostPack'
if ($LASTEXITCODE -ne 0) { throw 'SDKのホストパック情報を取得できません。' }
$hostPack = ($sdkItems | ConvertFrom-Json).Items.KnownAppHostPack |
    Where-Object TargetFramework -eq 'net10.0'
$hostVersion = $hostPack.AppHostPackVersion
if ($hostVersion -notmatch '^10\.0\.[0-9]+$') { throw '使用した .NET ホストパックを特定できません。' }
$hostName = 'microsoft.netcore.app.host.win-x64'
$hostZip = Join-Path $output 'dotnet-host.zip'
$hostFiles = Join-Path $output 'dotnet-host'
$hostUri = "https://api.nuget.org/v3-flatcontainer/$hostName/$hostVersion/$hostName.$hostVersion.nupkg"
Invoke-WebRequest -Uri $hostUri -OutFile $hostZip -TimeoutSec 60
[IO.Compression.ZipFile]::ExtractToDirectory($hostZip, $hostFiles)

Copy-Item -LiteralPath (Join-Path $corePack 'LICENSE.TXT') -Destination (Join-Path $licenses 'DOTNET-RUNTIME-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $corePack 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses 'DOTNET-RUNTIME-ThirdPartyNotices.txt')
Copy-Item -LiteralPath (Join-Path $desktopPack 'LICENSE') -Destination (Join-Path $licenses 'DOTNET-WPF-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $hostFiles 'LICENSE.TXT') -Destination (Join-Path $licenses 'DOTNET-HOST-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $hostFiles 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses 'DOTNET-HOST-ThirdPartyNotices.txt')

$receipt = [ordered]@{
    schema_version = 1
    version = $Version
    product_version = $exe.VersionInfo.ProductVersion
    source_revision = $revision
    runtime_identifier = 'win-x64'
    frameworks = $frameworks
    app_host_pack_version = $hostVersion
    executable = $exe.Name
    sha256 = (Get-FileHash -LiteralPath $exe.FullName).Hash.ToLowerInvariant()
    bytes = $exe.Length
}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'release.json') -Encoding utf8

$filename = "UWSCSharp-$Version-win-x64.zip"
$zip = Join-Path $output $filename
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip)
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $files = Get-ChildItem -LiteralPath $package -File -Recurse
    if ($archive.Entries.Count -ne @($files).Count) { throw 'ZIP内のファイル数が一致しません。' }
    foreach ($item in $files) {
        $relative = [IO.Path]::GetRelativePath($package, $item.FullName).Replace('\', '/')
        $entry = $archive.GetEntry($relative)
        if ($null -eq $entry) { throw "ZIPにファイルがありません: $relative" }
        $stream = $entry.Open()
        try {
            $archived = Get-FileHash -InputStream $stream
            if ($archived.Hash -ne (Get-FileHash -LiteralPath $item.FullName).Hash) {
                throw "ZIP内のファイルが梱包元と一致しません: $relative"
            }
        }
        finally { $stream.Dispose() }
    }
}
finally { $archive.Dispose() }

$hash = (Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $filename`n" -Encoding ascii -NoNewline
Write-Output "$filename を作成し、ZIP内の $(@($files).Count) ファイルを照合しました。"
