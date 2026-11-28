# 記録・再生GIFを撮り直す

Windows 11 x64 で、Windows Sandbox CLI（`wsb`）、.NET 11 SDK、PowerShell 7、FFmpeg を用意します。FFmpeg は単体で実行でき、`gdigrab`、`libx264`、`drawtext` に対応したビルドを使います。既存の Windows Sandbox を閉じてから、リポジトリのルートで実行します。[Windows Sandbox CLI のドキュメント](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli)

```powershell
pwsh -NoProfile -File scripts/capture-readme-recording.ps1
```

UWSC# のビルドから、Windows Sandbox 内での記録・再生と撮影、字幕付きGIFへの変換まで自動で実行します。記録時と再生時でメモ帳の移動先と入力文字列が一致すると、`docs/images/notepad-record-replay.gif` を更新します。別のファイルへ保存するには `-OutputPath` を指定してください。元動画・生成スクリプト・ログは `artifacts/readme-recording/` に残ります。

Windows Sandbox のログオン時に、ホストの `%SystemRoot%\Web\Wallpaper\Windows\img0.jpg`（通常は `C:\Windows\Web\Wallpaper\Windows\img0.jpg`）をサンドボックスのデスクトップの壁紙に設定します。

撮影にはクラシック版のメモ帳を使います。既定の `%SystemRoot%\System32\notepad.exe` で起動できない環境では、`-NotepadPath` でクラシック版の実行ファイルを指定します。
