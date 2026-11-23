# UWSC# 利用者向けHTMLヘルプ

Just the Docsの階層目次・検索・本文・ページ内目次という構成を参考に、
独自のHTMLとCSSで作成しています。テーマのコードや画像は使用していません。
閲覧用のHTMLと検索索引を保存しているため、Python、サーバー、外部CDNなしで読めます。

## ローカルで読む

このフォルダーの `index.html` をブラウザーで開いてください。
目次、ページ間の移動、ページ内目次、検索をそのまま使えます。
検索にはJavaScriptが必要です。無効な場合も目次から本文を読めます。
別の場所で読むときは、`index.html`、`pages/`、`assets/` を同じフォルダーにコピーしてください。

閲覧前の生成は不要です。以下のNode.jsコマンドは、原稿を更新する開発者向けです。

### この形式の利点と注意点

Pythonの導入やサーバーの起動が不要で、オフラインでも読めます。

原稿と閲覧用の生成物を両方保存するため、容量とGitの差分が増えます。
原稿だけを直すと閲覧用ファイルに反映されないので、更新時の再生成と `--check` が必要です。
生成後の入口は `index.html`、ほかの閲覧用HTMLは `pages/` にあります。原稿は `content/` にあります。
検索にはJavaScriptを使い、現在は検索索引を各ページで読み込みます。
今後ページ数が大幅に増えた場合は、索引の容量と読み込み時間を確認してください。

## 生成と確認

Node.js 24を用意し、リポジトリのルートで実行してください。
生成・ヘルプ検査にPythonは使いません。追加パッケージのインストールも不要です。

```powershell
node scripts/build_help.js
node scripts/build_help.js --check
node scripts/check_help_build.js
node scripts/check_help_reference.js
node scripts/check_help_search.js
```

生成時にこのフォルダーの閲覧用HTMLと検索索引も更新します。
原稿の更新後は生成を実行し、閲覧用ファイルも一緒に保存してください。
公開出力は `artifacts/help-site/` の28ファイル（24ページと共通資産）です。
生成時に内部リンク・アンカー・公開ファイル一覧を検査します。
`--check` は原稿と、このフォルダーおよび公開出力の生成物が一致することも検査します。
公開出力の別の保存先には `--output` を指定できます。
この場合も、このフォルダーの閲覧用ファイルは更新・検査します。
原稿を含むこのフォルダー自体は、`--output` の保存先に指定できません。

利用ガイドと言語リファレンスの原稿は、`content/` のHTML断片です。
このフォルダー直下のHTMLは生成物なので、直接編集しないでください。
分野別の関数とPRINTは `reference.json` の構造化原稿から生成します。
書式・引数・戻り値・制限と、照合した実装・テストのパスを保持しています。
根拠のパスは公開HTMLに出力しません。
ページ追加時は `pages.json` も更新してください。見出しは一意なIDを持つh2にすると、
ページ内目次にも追加されます。外部リンクは、トップページの不具合報告とGitHub Sponsors、
導入ページのGitHub ReleasesとMicrosoftの.NET 10ダウンロードページを許可します。
内部リンクは、公開対象のファイルとリンク先の見出しを生成時に検査します。
CSSと検索スクリプトは `assets/` にあります。`assets/search-index.js` は生成物です。
検索結果はページだけでなく、関数名・節見出しへの直接リンクも表示します。
HTML原稿の文字参照は `&amp;`、`&lt;`、`&gt;`、`&quot;`、`&apos;`、
`&nbsp;` と数値文字参照に対応します。それ以外は生成時にエラーとなるため、
文字を直接UTF-8で記述するか、数値文字参照を使ってください。

左目次は「はじめに」と、折りたためる「使い方」「言語リファレンス」
「スクリプト関数」「困ったとき」で構成しています。
閲覧中のページのグループだけを初期状態で開き、対象のリンクを強調します。
グループはクリックやEnterキーで開閉できます。開閉と初期展開にJavaScriptは不要です。

`pages.json` の `group` が親見出し、`nav_title` が任意の目次専用ラベルです。
グループを持たないページは最上位へ配置します。同じグループのページは連続して記載してください。
ページの並び順は前後ページリンクにも反映します。

## 収録範囲と掲載例の検査

`check_help_reference.js` は、Core・IO・Windowsの登録済み関数と
原稿の関数名が一致すること、対応状況データの項目が未着手でないこと、公開されているソースとテストの参照先を検査します。
24ページそれぞれの目次の所属・選択状態・初期展開も検査します。
関数全体の原版互換を機械的に確認する検査ではありません。
表示分類ではINPUT・MSGBOX・SLCTBOX、PRINT文とログ表示制御を掲載しています。他のダイアログ関数は未実装です。

掲載例を実行する場合は、限定版ではない通常のコマンドライン版を先にビルドしてください。
古い `src/.../bin/` 配下に機能を限定したコマンドライン版が残っていても使用しません。

```powershell
dotnet build src/UWSCSharp.Cli --no-restore --disable-build-servers -m:1 -c Release
node scripts/check_help_reference.js --cli artifacts/bin/UWSCSharp.Cli/release/UWSCSharp.Cli.dll
node scripts/check_help_search.js
node scripts/check_help_search.js artifacts/help-site
```

検証対象の掲載例について、終了コード0・期待出力・標準エラーなしを検査します。
CALLの例だけは専用一時フォルダーへ2ファイルを作成します。
Windows入力・外部アプリ・クリップボード・COMの実操作例は実行しません。
結果は `artifacts/help-validation/reference-samples.json` に保存します。
Node.jsの検索検査では、公開用JavaScriptをブラウザーとは別に実行し、
関数への直接リンク、全角英字、複数語、該当なし、Escapeによる解除を確認します。
画面表示やブラウザー操作の検査ではありません。
## GitHub Pagesで公開する

GitHubの Settings → Pages → Build and deployment で Source を GitHub Actions に設定します。
Actions の Publish HTML help からワークフローを実行すると、生成した閲覧用ファイルを公開できます。

## 更新時の注意

関数の書式・戻り値・制限は、現行の実装とテストを確認して更新してください。
原版との互換性、製品の回帰テスト、実画面での確認はそれぞれ結果を記載してください。

## UWSC# アプリに同梱するヘルプ

UWSC# アプリは、生成済みの24ページとCSS・JavaScript・検索索引の計27ファイルをEXEへ埋め込みます。
ビルドには保存済みの生成物を使い、利用者のPCでNode.jsやPythonを実行しません。
「ヘルプ」ボタンまたはローカルF1で、利用者のキャッシュへ展開して既定ブラウザーで開きます。
再生・記録中は無効です。詳しくは[編集画面の案内](pages/editor.html#help)を参照してください。
