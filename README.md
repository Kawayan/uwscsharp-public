# UWSC#

UWSC# は、Windowsでスクリプトを実行するためのアプリです。  
WPFアプリでは、スクリプトの編集、記録、再生ができます。  
コマンドライン版と、スクリプト作成を支援するローカルMCPサーバーもあります。

導入手順、構文、関数の使い方は[利用者向けヘルプ（GitHub Pages）](https://kawayan.github.io/uwscsharp-public/)で確認できます。  
オフラインで読む場合は、ブラウザーで [`docs/user-help/index.html`](docs/user-help/index.html) を開いてください。

## ビルド

Windows 11 x64、.NET 10 SDK（`global.json` 参照）、PowerShell 7を使います。  
Windowsテストのネイティブフィクスチャのビルドには、Visual StudioのC++デスクトップ開発ワークロードとWindows SDKが必要です。

```powershell
dotnet build UWSCSharp.slnx -c Release
pwsh -NoProfile -File scripts/test-headless.ps1 -Configuration Release
```

テストスクリプトは、GUI操作を伴わない重要な回帰テストを実行します。テスト結果は `artifacts/test-runs/` に保存します。  
実画面・入力・フォーカスを使うテストはWindows Sandboxなどの専用ゲストで実行してください。

ヘルプを更新する場合は、[生成と確認の手順](docs/user-help/README.md)を参照してください。

## 不具合の報告

不具合を見つけた場合は以下から報告いただけると助かります。  
[<kbd>不具合を報告</kbd>](https://github.com/Kawayan/uwscsharp-public/issues/new?template=bug-report.yml)


## 支援
UWSC# を気に入ってもらえたら、GitHub Sponsors で応援していただけると開発の励みになります！  
[<kbd>GitHub Sponsors で支援</kbd>](https://github.com/sponsors/Kawayan)

## WiX バンドル EXE の作成

Windows x64 向けのセットアップ EXE を作成できます。WiX Burn がアプリの MSI を EXE に内蔵します。.NET 10 SDK、PowerShell 7、Visual Studio の C++ デスクトップ開発ワークロードと Windows SDK を用意してください。WiX 7 はビルド時に NuGet から復元します。[WiX 7 の利用条件](https://docs.firegiant.com/wix/osmf/)を確認し、同意する場合は次のコマンドを実行してください。

```powershell
pwsh -NoProfile -File scripts/build-wpf-bundle.ps1 -Version 0.1.0-preview.1 -InstallerVersion 0.1.0 -OutputDirectory artifacts/wpf-bundle -AcceptWixEula
```

変更をコミットしてから、まだ存在しない出力先を指定してください。`-Version` はアプリの版、`-InstallerVersion` はセットアップ EXE と内蔵 MSI が更新の判定に使う数値版です。セットアップを更新する場合は数値版も上げてください。`bundle` フォルダーにセットアップ EXE と SHA-256 ファイルを出力します。

セットアップ EXE は現在のユーザーの `%LOCALAPPDATA%\Programs\UWSCSharp` にアプリをインストールし、スタートメニューにショートカットを追加します。`.uws` の関連付け候補の登録は任意です。32ビット DLL を呼び出す場合の x86 版 .NET は別途導入してください。

## ライセンス

[MIT License](LICENSE)
