# WiX バンドル EXE の作成

Windows x64 向けのセットアップ EXE を作成できます。WiX Burn がアプリの MSI を EXE に内蔵します。.NET 11 SDK、PowerShell 7、Visual Studio の C++ デスクトップ開発ワークロードと Windows SDK を用意してください。WiX 7 はビルド時に NuGet から復元します。[WiX 7 の利用条件](https://docs.firegiant.com/wix/osmf/)を確認し、同意する場合は次のコマンドを実行してください。

リポジトリのルートで実行します。

```powershell
pwsh -NoProfile -File scripts/build-wpf-bundle.ps1 -OutputDirectory artifacts/wpf-bundle -AcceptWixEula
```

変更をコミットしてから、まだ存在しない出力先を指定してください。既定では、ビルドした WPF EXE の製品バージョンをアプリの版に使い、そのメジャー・マイナー・パッチをセットアップ EXE と内蔵 MSI の数値版に使います。必要に応じて `-Version` と `-InstallerVersion` で個別に指定できます。セットアップを更新する場合は数値版も上げてください。`bundle` フォルダーにセットアップ EXE と SHA-256 ファイルを出力します。

セットアップ EXE は現在のユーザーの `%LOCALAPPDATA%\Programs\UWSCSharp` にアプリをインストールし、スタートメニューにショートカットを追加します。`.uws` の関連付け候補の登録は任意です。32ビット DLL を呼び出す場合の x86 版 .NET は別途導入してください。
