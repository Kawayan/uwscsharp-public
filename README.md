# UWSC#

UWSC# は、Windowsでスクリプトを実行するためのアプリです。WPFアプリでは、スクリプトの編集、記録、再生ができます。コマンドライン版と、スクリプト作成を支援するローカルMCPサーバーもあります。

[<kbd>不具合を報告</kbd>](https://github.com/Kawayan/uwscsharp-public/issues/new?template=bug-report.yml)　[<kbd>GitHub Sponsors で支援</kbd>](https://github.com/sponsors/Kawayan)

導入手順、構文、関数の使い方は[利用者向けヘルプ（GitHub Pages）](https://kawayan.github.io/uwscsharp-public/)で確認できます。
オフラインで読む場合は、ブラウザーで [`docs/user-help/index.html`](docs/user-help/index.html) を開いてください。

## ビルド

Windows 11 x64、.NET 10 SDK（`global.json` 参照）、PowerShell 7を使います。Windowsテストのネイティブフィクスチャのビルドには、Visual StudioのC++デスクトップ開発ワークロードとWindows SDKが必要です。

```powershell
dotnet build UWSCSharp.slnx -c Release
pwsh -NoProfile -File scripts/test-headless.ps1 -Configuration Release
```

テストスクリプトは、GUI操作を伴わない重要な回帰テストを実行します。テスト結果は `artifacts/test-runs/` に保存します。実画面・入力・フォーカスを使うテストはWindows Sandboxなどの専用ゲストで実行してください。

ヘルプを更新する場合は、[生成と確認の手順](docs/user-help/README.md)を参照してください。

## ライセンス

[MIT License](LICENSE)
