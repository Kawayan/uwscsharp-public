# UWSC#

UWSC# は、Windows 11 x64 でマウスやキーボードの操作を記録し、スクリプトとして再生できる自動化ツールです。WPF 版では、記録した操作を編集したり、自分で書いた `.uws` ファイルを読み込んで実行したりできます。コマンドライン版と、スクリプト作成を支援するローカル MCP サーバーもあります。

## 最初のスクリプトを実行する

1. [GitHub Releases](https://github.com/Kawayan/uwscsharp-public/releases) から Windows x64 向けの ZIP をダウンロードし、書き込み可能なフォルダーにすべて展開します。
2. `UWSCSharp.Wpf.exe` を起動し、画面下部の **「スクリプト編集・実行結果」** を開きます。
3. 上側の編集欄に次のスクリプトを貼り付け、画面上部の **「▶ 再生」** をクリックします。

```text
FOR i = 1 TO 3
    PRINT i
NEXT
```

「実行が完了しました」と表示され、下側の「実行結果」に `1`、`2`、`3` が1行ずつ並びます。

![UWSC# の実行画面。編集欄の FOR ループを再生し、実行結果に 1、2、3 が表示されている](docs/images/first-run.png)

## マウスやキーボードの操作を記録する

新規スクリプトで **「● 記録」** をクリックし、繰り返したい操作を行います。**「■ 記録終了」** をクリックすると、編集欄に操作内容がスクリプトとして表示されます。対象を操作開始前の状態に戻してから、**「▶ 再生」** で動作を確認できます。記録を残す場合は、**「▣ 保存」** で `.uws` ファイルに保存します。

下のGIFでは、Windows Sandbox 内でメモ帳を開き、タイトルバーをドラッグして `Hello UWSC#` と入力する操作を記録します。記録を終了し、メモ帳を元の位置に戻して閉じてから **「▶ 再生」** を押すと、同じ操作が繰り返されます。

![UWSC# でメモ帳の起動、ドラッグ、文字入力を記録し、生成されたスクリプトを再生する](docs/images/notepad-record-replay.gif)

記録設定や対応する操作は[操作記録のヘルプ](https://kawayan.github.io/uwscsharp-public/pages/recording.html)、構文や関数は[利用者向けヘルプ](https://kawayan.github.io/uwscsharp-public/)を参照してください。オフラインではブラウザーで [docs/user-help/index.html](docs/user-help/index.html) を開けます。

## 開発者向けの手順

- [ビルド](docs/build.md)
- [記録・再生GIFを撮り直す](docs/capture-readme-recording.md)
- [WiX バンドル EXE の作成](docs/build-wpf-bundle.md)

## 不具合の報告

不具合を見つけた場合は以下から報告いただけると助かります。  
[<kbd>不具合を報告</kbd>](https://github.com/Kawayan/uwscsharp-public/issues/new?template=bug-report.yml)


## 支援
UWSC# を気に入ってもらえたら、GitHub Sponsors で応援していただけると開発の励みになります！  
[<kbd>GitHub Sponsors で支援</kbd>](https://github.com/sponsors/Kawayan)

## ライセンス

[MIT License](LICENSE)
