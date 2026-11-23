#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)]
    [string]$Msi,
    [Parameter(Mandatory = $true)]
    [string]$PayloadDirectory,
    [Parameter(Mandatory = $true)]
    [string]$InstallerVersion
)

$ErrorActionPreference = 'Stop'
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase([IO.Path]::GetFullPath($Msi), 0)
function Read-Rows([string]$Sql, [int]$Columns) {
    $view = $database.OpenView($Sql)
    try {
        $view.Execute() | Out-Null
        while ($null -ne ($record = $view.Fetch())) {
            $values = @()
            for ($index = 1; $index -le $Columns; $index++) {
                $values += $record.StringData($index)
            }
            ,$values
        }
    } finally {
        $view.Close() | Out-Null
    }
}
$properties = @{}
foreach ($row in (Read-Rows 'SELECT `Property`, `Value` FROM `Property`' 2)) {
    $properties[$row[0]] = $row[1]
}
if ($properties.ProductVersion -ne $InstallerVersion -or
    $properties.ProductLanguage -ne '1041' -or
    $properties.UpgradeCode -ne '{A87F1A67-46FB-46CC-9006-0B08054E450A}' -or
    $properties.INSTALLLEVEL -ne '1' -or
    $properties.ALLUSERS) {
    throw 'MSIの版番号・言語・更新識別子・導入範囲が想定と異なります。'
}
$files = @(Read-Rows 'SELECT `FileName`, `FileSize` FROM `File`' 2)
$payload = @(Get-ChildItem -LiteralPath $PayloadDirectory -File -Recurse)
if ($files.Count -ne $payload.Count -or $files.Count -ne 16) {
    throw 'MSIの収録ファイル数が配布対象と一致しません。'
}
foreach ($file in $payload) {
    $matches = @($files | Where-Object {
        ($_[0] -split '\|')[-1] -ceq $file.Name -and [long]$_[1] -eq $file.Length
    })
    if ($matches.Count -ne 1) { throw "MSIのファイル名・サイズが一致しません: $($file.Name)" }
}
$relayDirectory = @(Read-Rows 'SELECT `Directory_Parent`, `DefaultDir` FROM `Directory` WHERE `Directory` = ''DllRelayFolder''' 2)
$relayComponent = @(Read-Rows 'SELECT `Directory_` FROM `Component` WHERE `Component` = ''DllRelay''' 1)
$relayFiles = @(Read-Rows 'SELECT `FileName` FROM `File` WHERE `Component_` = ''DllRelay''' 1 |
    ForEach-Object { ($_[0] -split '\|')[-1] })
$expectedRelay = @(Get-ChildItem -LiteralPath (Join-Path $PayloadDirectory 'dll-relay') -File | Select-Object -ExpandProperty Name)
if ($relayDirectory.Count -ne 1 -or $relayDirectory[0][0] -ne 'INSTALLFOLDER' -or
    ($relayDirectory[0][1] -split '\|')[-1] -ne 'dll-relay' -or
    $relayComponent.Count -ne 1 -or $relayComponent[0][0] -ne 'DllRelayFolder' -or
    $expectedRelay.Count -ne 6 -or $relayFiles.Count -ne 6 -or
    (Compare-Object $expectedRelay $relayFiles -CaseSensitive)) {
    throw '32ビットDLLの中継ファイルをインストール先のdll-relayに配置できません。'
}
$media = @(Read-Rows 'SELECT `Cabinet` FROM `Media`' 1)
if ($media.Count -eq 0 -or
    @($media | Where-Object { -not $_[0].StartsWith('#') }).Count -gt 0) {
    throw 'MSIへ埋め込んだCABを確認できません。'
}
$shortcuts = @(Read-Rows 'SELECT `Target` FROM `Shortcut`' 1)
if ($shortcuts.Count -ne 1 -or $shortcuts[0][0] -ne '[#ApplicationExe]') {
    throw 'スタートメニューの起動先が一致しません。'
}
$upgrades = @(Read-Rows 'SELECT `ActionProperty` FROM `Upgrade`' 1)
if (@($upgrades | Where-Object { $_[0] -eq 'SAMEVERSIONFOUND' }).Count -ne 1 -or
    @($upgrades | Where-Object { $_[0] -eq 'WIX_UPGRADE_DETECTED' }).Count -ne 1) {
    throw '同一版検出・更新規則が不足しています。'
}
$dialogs = @(Read-Rows 'SELECT `Dialog` FROM `Dialog`' 1)
if (@($dialogs | Where-Object { $_[0] -eq 'LicenseAgreementDlg' }).Count -ne 1 -or
    @($dialogs | Where-Object { $_[0] -eq 'CustomizeDlg' }).Count -ne 1) {
    throw 'インストール画面が不足しています。'
}
$features = @(Read-Rows 'SELECT `Feature`, `Level` FROM `Feature`' 2)
if (@($features | Where-Object { $_[0] -eq 'AssociationOption' -and $_[1] -eq '2' }).Count -ne 1) {
    throw '関連付け候補の任意選択または既定値を確認できません。'
}
$sequence = @{}
foreach ($row in (Read-Rows 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`' 2)) {
    $sequence[$row[0]] = [int]$row[1]
}
if (-not ($sequence.InstallInitialize -lt $sequence.RemoveExistingProducts -and
        $sequence.RemoveExistingProducts -lt $sequence.InstallFiles)) {
    throw '更新時のロールバックを可能にするアクション順序ではありません。'
}
foreach ($action in @('AssociationInstall', 'AssociationInstallRollback',
        'AssociationDisable', 'AssociationDisableRollback',
        'AssociationUpgradeDisable', 'AssociationUpgradeDisableRollback',
        'AssociationUninstall', 'AssociationUninstallRollback')) {
    if (-not $sequence.ContainsKey($action)) { throw "関連付け処理が不足しています: $action" }
}
if (-not ($sequence.AssociationUninstallRollback -lt $sequence.AssociationUninstall -and
        $sequence.AssociationUninstall -lt $sequence.RemoveFiles -and
        $sequence.AssociationDisableRollback -lt $sequence.AssociationDisable -and
        $sequence.AssociationDisable -lt $sequence.RemoveFiles -and
        $sequence.InstallFiles -lt $sequence.AssociationInstallRollback -and
        $sequence.AssociationInstallRollback -lt $sequence.AssociationInstall -and
        $sequence.AssociationInstall -lt $sequence.InstallFinalize -and
        $sequence.AssociationUpgradeDisableRollback -lt $sequence.AssociationUpgradeDisable -and
        $sequence.AssociationUpgradeDisable -lt $sequence.InstallFinalize)) {
    throw '関連付け処理とロールバックの順序が不正です。'
}
$actions = @{}
foreach ($row in (Read-Rows 'SELECT `Action`, `Type` FROM `CustomAction`' 2)) {
    $actions[$row[0]] = [int]$row[1]
}
$installTarget = @(Read-Rows 'SELECT `Target` FROM `CustomAction` WHERE `Action` = ''AssociationInstall''' 1)
if ($installTarget.Count -ne 1 -or -not ([string]$installTarget[0][0]).Contains('[Installed]')) {
    throw '関連付け導入時の変更前の製品状態を確認できません。'
}
if (@($actions.Keys | Where-Object { $_ -like 'AssociationProbe*' }).Count -gt 0) {
    throw '通常のMSIに検証用の失敗注入処理が含まれています。'
}
foreach ($action in @('AssociationInstallRollback', 'AssociationDisableRollback',
        'AssociationUpgradeDisableRollback', 'AssociationUninstallRollback')) {
    if (-not $actions.ContainsKey($action) -or ($actions[$action] -band 1280) -ne 1280) {
        throw "関連付けロールバックの実行種別が不正です: $action"
    }
}
$buttons = @(Read-Rows 'SELECT `Text` FROM `Control` WHERE `Control` = ''Next''' 1)
if (@($buttons | Where-Object { $_[0] -like '次へ*' }).Count -eq 0) {
    throw '日本語のインストール画面を確認できません。'
}
$components = @(Read-Rows 'SELECT `Attributes`, `KeyPath` FROM `Component`' 2)
$registry = @(Read-Rows 'SELECT `Registry`, `Root` FROM `Registry`' 2)
foreach ($component in $components) {
    if (([int]$component[0] -band 4) -eq 0 -or
        @($registry | Where-Object { $_[0] -eq $component[1] -and $_[1] -eq '1' }).Count -ne 1) {
        throw 'ユーザー単位のComponentにHKCUのKeyPathがありません。'
    }
}
$removeFolders = @(Read-Rows 'SELECT `DirProperty` FROM `RemoveFile`' 1)
foreach ($directory in @('INSTALLFOLDER', 'ProgramsFolder', 'ExamplesFolder', 'LicensesFolder', 'DllRelayFolder', 'AppMenuFolder')) {
    if (@($removeFolders | Where-Object { $_[0] -eq $directory }).Count -ne 1) {
        throw "空フォルダーの削除定義がありません: $directory"
    }
}
Write-Output "PASS: MSIの構成・16ファイル・埋込CAB・起動先・更新規則・日本語画面を確認しました: $Msi"
