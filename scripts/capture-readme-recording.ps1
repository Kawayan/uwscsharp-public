#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '../docs/images/notepad-record-replay.gif'),

    [string]$FfmpegPath = 'ffmpeg',

    [string]$NotepadPath = (Join-Path $env:SystemRoot 'System32/notepad.exe')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ([IO.Path]::GetExtension($output) -ne '.gif') { throw '出力先には .gif ファイルを指定してください。' }
if (![Environment]::Is64BitProcess -or ![OperatingSystem]::IsWindows()) {
    throw 'Windows x64 の PowerShell 7 で実行してください。'
}
$ffmpeg = (Get-Command $FfmpegPath -CommandType Application | Select-Object -First 1).Source
$notepad = Get-Item -LiteralPath $NotepadPath
$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$wsb = (Get-Command wsb -CommandType Application | Select-Object -First 1).Source
$font = Join-Path $env:SystemRoot 'Fonts/YuGothM.ttc'
$wallpaper = Join-Path $env:SystemRoot 'Web/Wallpaper/Windows/img0.jpg'
if (!(Test-Path -LiteralPath $font -PathType Leaf)) { throw '字幕の描画に Yu Gothic Medium が必要です。' }
if (!(Test-Path -LiteralPath $wallpaper -PathType Leaf)) { throw "撮影用の壁紙が見つかりません: $wallpaper" }
if ($notepad.PSIsContainer) { throw 'クラシック版メモ帳の実行ファイルを指定してください。' }

$existing = & $wsb list --raw
if ($LASTEXITCODE -ne 0) { throw 'Windows Sandbox の一覧を取得できませんでした。' }
if (@(($existing | ConvertFrom-Json).WindowsSandboxEnvironments).Count -gt 0) {
    throw 'Windows Sandbox を閉じてから実行してください。'
}

$sessionId = [guid]::NewGuid().ToString()
$work = Join-Path $root "artifacts/readme-recording/$sessionId"
$inputDir = Join-Path $work 'input'
$capture = Join-Path $work 'capture'
$runtime = Join-Path $inputDir 'dotnet'
$app = Join-Path $inputDir 'app'
$note = Join-Path $inputDir 'notepad'
New-Item -ItemType Directory -Path $app,$note,$capture -Force | Out-Null
Write-Host "撮影ログの保存先 $work"

Copy-Item -LiteralPath $ffmpeg -Destination (Join-Path $inputDir 'ffmpeg.exe')
Copy-Item -LiteralPath $wallpaper -Destination (Join-Path $inputDir 'img0.jpg')
Copy-Item -LiteralPath $notepad.FullName -Destination (Join-Path $note 'notepad.exe')
foreach ($directory in Get-ChildItem -LiteralPath $notepad.DirectoryName -Directory) {
    $mui = Join-Path $directory.FullName ($notepad.Name + '.mui')
    if (Test-Path -LiteralPath $mui -PathType Leaf) {
        $language = New-Item -ItemType Directory -Path (Join-Path $note $directory.Name)
        Copy-Item -LiteralPath $mui -Destination (Join-Path $language.FullName 'notepad.exe.mui')
    }
}
Copy-Item -LiteralPath $font -Destination (Join-Path $work 'captions.ttc')
# ゲストの Windows PowerShell 5.1 が日本語を正しく読める形式で配置する。
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'readme-recording-sandbox.ps1') -Raw |
    Set-Content -LiteralPath (Join-Path $inputDir 'readme-recording-sandbox.ps1') -Encoding utf8BOM

Push-Location $root
try {
    & $dotnet build 'src/UWSCSharp.Wpf/UWSCSharp.Wpf.csproj' -c Release -o $app
    if ($LASTEXITCODE -ne 0) { throw 'UWSC# のビルドに失敗しました。' }
}
finally { Pop-Location }

# 実行ファイルが要求する共有ランタイムだけをSandboxへ渡す。
$dotnetRoot = Split-Path $dotnet -Parent
$runtimeConfig = Get-Content -LiteralPath (Join-Path $app 'UWSCSharp.Wpf.runtimeconfig.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path (Join-Path $runtime 'host') -Force | Out-Null
Copy-Item -LiteralPath $dotnet -Destination (Join-Path $runtime 'dotnet.exe')
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'host/fxr') -Destination (Join-Path $runtime 'host') -Recurse
foreach ($framework in $runtimeConfig.runtimeOptions.frameworks) {
    $minimum = [version]$framework.version
    $source = Join-Path $dotnetRoot "shared/$($framework.name)"
    $version = Get-ChildItem -LiteralPath $source -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
        Where-Object { $v = [version]$_.Name; $v -ge $minimum -and $v.Major -eq $minimum.Major } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (!$version) { throw "$($framework.name) $minimum に対応するランタイムが見つかりません。" }
    $target = New-Item -ItemType Directory -Path (Join-Path $runtime "shared/$($framework.name)") -Force
    Copy-Item -LiteralPath $version.FullName -Destination $target.FullName -Recurse
}

$inXml = [Security.SecurityElement]::Escape($inputDir)
$outXml = [Security.SecurityElement]::Escape($capture)
$configuration = @"
<Configuration>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$inXml</HostFolder><SandboxFolder>C:\UwscDemoInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$outXml</HostFolder><SandboxFolder>C:\UwscDemoOutput</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\UwscDemoInput\readme-recording-sandbox.ps1</Command></LogonCommand>
</Configuration>
"@
$receipt = Join-Path $capture 'capture.json'
$guestError = Join-Path $capture 'capture.json.error'
$started = $false
try {
    & $wsb start --id $sessionId --config $configuration --raw
    if ($LASTEXITCODE -ne 0) { throw '撮影用の Windows Sandbox を起動できませんでした。' }
    $started = $true
    & $wsb connect --id $sessionId --raw
    if ($LASTEXITCODE -ne 0) { throw '撮影用の Windows Sandbox に接続できませんでした。' }
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    $finished = Join-Path $capture 'finished.txt'
    while (!(Test-Path -LiteralPath $finished) -and !(Test-Path -LiteralPath $guestError)) {
        if ($elapsed.Elapsed.TotalSeconds -ge 150) { throw "撮影の制限時間を超えました。ログ: $capture" }
        Start-Sleep -Milliseconds 350
    }
    if (Test-Path -LiteralPath $guestError) { throw (Get-Content -LiteralPath $guestError -Raw) }
    if ((Get-Content -LiteralPath $finished -Raw).Trim() -ne '0' -or !(Test-Path -LiteralPath $receipt)) {
        throw "ゲスト内の撮影が失敗しました。ログ: $capture"
    }
}
finally {
    if ($started) {
        & $wsb stop --id $sessionId --raw
        $stopped = $LASTEXITCODE -eq 0
        if (!$stopped) { Write-Warning "撮影用の Windows Sandbox を終了できませんでした ($sessionId)。" }
    }
}

$timing = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
if ($timing.resetAt -le 0 -or $timing.replayAt -le $timing.resetAt) { throw '字幕の切り替え時刻を取得できません。' }
$invariant = [Globalization.CultureInfo]::InvariantCulture
$resetAt = $timing.resetAt.ToString($invariant)
$replayAt = $timing.replayAt.ToString($invariant)
$captionStyle = "fontfile=captions.ttc:fontcolor=white:fontsize=21:x=18:y=9"
$filters = @(
    '[0:v]scale=1100:-1:flags=lanczos,fps=12,pad=iw:ih+42:0:42:color=0x18212B'
    "drawtext=${captionStyle}:text='操作を記録':enable='lt(t,$resetAt)'"
    "drawtext=${captionStyle}:text='メモ帳を元の位置に戻して閉じる':enable='gte(t,$resetAt)*lt(t,$replayAt)'"
    "drawtext=${captionStyle}:text='記録した操作を再生':enable='gte(t,$replayAt)'"
)
$filter = ($filters -join ',') + ',split[frames][stats];[stats]palettegen[palette];[frames][palette]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle'
$gif = Join-Path $work 'notepad-record-replay.gif'
Push-Location $work
try {
    & $ffmpeg -hide_banner -loglevel error -i (Join-Path $capture 'capture.mkv') -filter_complex $filter -loop 0 $gif
    if ($LASTEXITCODE -ne 0) { throw 'GIFへの変換に失敗しました。' }
    & $ffmpeg -hide_banner -loglevel error -xerror -i $gif -f null NUL
    if ($LASTEXITCODE -ne 0) { throw 'GIFを読み戻せませんでした。' }
}
finally { Pop-Location }

$outputDirectory = Split-Path $output -Parent
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$temporary = Join-Path $outputDirectory (".$sessionId.gif")
try {
    [IO.File]::Copy($gif, $temporary)
    [IO.File]::Move($temporary, $output, $true)
}
finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
if ($stopped) {
    try {
        $staging = Get-Item -LiteralPath $inputDir
        $expected = Join-Path (Resolve-Path -LiteralPath $work).Path 'input'
        if ($staging.FullName -eq $expected -and !$staging.LinkType) {
            Remove-Item -LiteralPath $expected -Recurse -Force
        }
    }
    catch { Write-Warning "撮影用の一時ファイルを片付けられませんでした: $inputDir" }
}
Write-Host "GIFを保存しました $output"
