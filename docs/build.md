# ビルド

Windows 11 x64、.NET 11 SDK（[global.json](../global.json) 参照）、PowerShell 7を使います。

Windowsテストのネイティブフィクスチャのビルドには、Visual StudioのC++デスクトップ開発ワークロードとWindows SDKが必要です。

リポジトリのルートで実行します。

```powershell
dotnet build UWSCSharp.slnx -c Release
pwsh -NoProfile -File scripts/test-headless.ps1 -Configuration Release
```

テストスクリプトは、GUI操作を伴わない重要な回帰テストを実行します。テスト結果は `artifacts/test-runs/` に保存します。

実画面・入力・フォーカスを使うテストはWindows Sandboxなどの専用ゲストで実行してください。

ヘルプを更新する場合は、[生成と確認の手順](user-help/README.md)を参照してください。
