# リファクタリング計画

2026-11-26 にコードベース全体（src 約9.7万行、tests 約16万行）を調べた結果をもとに、リファクタリングの対象と進め方をまとめます。

## 基本方針

- 動作を変えない変更だけを行います。例外として、安全性を高めるガードの追加（フェーズ1）は、明記したうえで含めます。
- フェーズごとに、ビルドと関連テストが通ることを確かめてからコミットします。
- 影響が大きく、方針を決めてから進めるべきもの（下の「今回は実施しない項目」）には手を付けません。

## 調査で見つかった課題

| # | 課題 | 優先度 | 今回の扱い |
|---|---|---|---|
| 1 | Sandbox で実行しているかの判定が25か所にばらばらに書かれ、判定のない検証がある | 高 | フェーズ1で実施 |
| 2 | 記録処理でウィンドウメッセージやイベント種別が数値のまま使われている | 中 | フェーズ2で実施 |
| 3 | 同じ Win32 API の宣言が複数のファイルで重複している | 中 | フェーズ3で実施 |
| 4 | 製品の EXE の約6割が検証用のコード（Diagnostics）である | 中 | 実施しない（要判断） |
| 5 | 180行を超える長いメソッドがある | 低 | 実施しない（関連修正のついでに分割） |

## フェーズ1: Sandbox 判定の統一

### 現状
- `src/UWSCSharp.Wpf` の25か所で、`Environment.UserName` を `"WDAGUtilityAccount"` と比べています。
- 書き方は少なくとも5通りあり、大文字・小文字を区別するもの（`==` / `!=`）としないもの（`StringComparison.OrdinalIgnoreCase`）が混ざっています。
- `AssociationRegistrationVerification` は、コメントでは「使い捨てSandbox内だけで実行」としているのに判定がありません。通常の PC で `--verify-association-registration` を実行すると、HKCU の `.uws` の関連付けを書き換えます。

### 変更内容
- `SandboxEnvironment.IsCurrent`（大文字・小文字を区別しない比較）を追加し、src の判定をすべてこれに置き換えます。
- 判定に失敗したときのメッセージや例外の種類は、各か所の現状のまま変えません。
- `AssociationRegistrationVerification.Run` に Sandbox の判定を追加します。**これは動作の変更です**（Sandbox の外では実行しないようになります）。

### 範囲外
- tests の判定（約200か所）は、ほぼすべてが `Assert.Equal("WDAGUtilityAccount", Environment.UserName)` という同じ書き方に揃っているので、今回は変えません。

### 確認方法
- WPF プロジェクトのビルド
- `RecordingUiRuntimeTests` のうち、Sandbox 専用を除くもの

## フェーズ2: 記録処理の定数化

### 現状
- `BasicRecorder` には、ウィンドウメッセージの16進数（`0x201`、`0x20c` など）、フックの種類（`13`、`14`）、注入フラグ（`0x10`、`1`）がそのまま書かれています。
- 記録イベントの種類（`InputEvent.Kind` の 0〜5）が数値のままで、`Kind is 1 or 2` のような条件の意味を、コードから読み取れません。

### 変更内容
- 低レベル入力のメッセージ・フック種類・フラグを、名前付き定数として `Native` に追加します。`Native` の既存の命名規則（`WmGetText` など）に合わせます。
- `InputEvent.Kind` を `RecordedInputKind` enum に変更します。値は次のとおりです。

| 値 | 名前 | 意味 |
|---|---|---|
| 0 | `Key` | キーの押下・解放 |
| 1 | `MouseMove` | マウス移動 |
| 2 | `MouseButton` | 座標付きのマウスボタン |
| 3 | `ButtonRelease` | 座標なしのボタン解放（フック復旧時の補完） |
| 4 | `Wheel` | ホイール |
| 5 | `WindowMovement` | ウィンドウの移動・サイズ変更 |

- 押下・解放の状態（1 / 2）は、生成するスクリプトの `KBD(…,1)` / `BTN(…,2)` の引数とそのまま対応しています。ホイールでは回転量、ウインドウ移動では段階と、種類ごとに意味も違うため、数値のまま残し、`InputEvent` のコメントで意味を説明します。

### 確認方法
- Windows プロジェクトのビルド
- 記録関連のテスト（`FullyQualifiedName~Recording`、Sandbox 専用を除く）。生成されるスクリプトが変わらないことを、既存のテストで確認します。

## フェーズ3: Win32 API 宣言の集約

### 現状
- `UWSCSharp.Wpf` は `InternalsVisibleTo` で `UWSCSharp.Windows` の `Native` を使えるのに、`GetForegroundWindow`、`GetWindowThreadProcessId`、`PostMessageW` などを独自に宣言しているファイルがあります。

### 変更内容
- `Native` に同じシグネチャの宣言が既にある API は、WPF 側の独自宣言を削除して `Native` を使うようにします。
- シグネチャが違うもの（引数の型や構造体が違うもの）は、無理にまとめません。対象外とした理由を、このドキュメントの実施記録に残します。

### 確認方法
- WPF プロジェクトのビルド
- 関係する検証を実行するテストのうち、Sandbox 専用を除くもの

## 今回は実施しない項目

### 検証用コードの製品 EXE からの分離（課題4）
`src/UWSCSharp.Wpf/Diagnostics` は69ファイル・約1.35万行あり、WPF プロジェクト全体の約62%を占めます。`--verify-*` 引数で、配布する EXE から呼び出せます。

分離すると、検証用 EXE の追加、テストの起動先の変更、リリースのワークフローの見直しが必要になり、配布物の構成も変わります。どの方式にするか（別 EXE にする、Release ビルドで除外する、など）を決めてから、別の作業として進めます。

### 長いメソッドの分割（課題5）
`NativeDllArgumentPlan.Create`（約276行）、`Interpreter.StartThreadAsync`（約243行）、`App.OnStartup`（約242行）、`NativeSelectionDialog.Create`（約237行）、`SchedulerWorkerClient.RunCoreAsync`（約202行）などがあります。

どれも複雑ですが既に動いており、まとめて分割すると誤りが入り込む危険のほうが大きいと判断しました。関係する修正を行うときに、少しずつ分割します。

## 実施記録

各フェーズを終えたら、ここに結果を追記します。

### フェーズ1（完了）
- `src/UWSCSharp.Wpf/Diagnostics/SandboxEnvironment.cs` を追加し、src の判定25か所（24ファイル）をすべて `SandboxEnvironment.IsCurrent` に置き換えました。src から `"WDAGUtilityAccount"` の直接比較はなくなりました。
- `AssociationRegistrationVerification.Run` に判定を追加しました。通常の PC で `--verify-association-registration` を実行すると、レジストリを変更せずに終了コード1で終わることを確認しました。
- 確認: WPF プロジェクトのビルド（エラー・警告なし）、`RecordingUiRuntimeTests`（Sandbox 専用を除く3件）すべて成功。

### フェーズ2（完了）
- `Native.LowLevelInput.cs` を追加し、低レベル入力のメッセージ、フックの種類、注入フラグ、右 Shift のスキャンコード、WinEvent の ID を名前付き定数にしました。`BasicRecorder` と `WindowMovementMonitor` の数値リテラルを置き換えています。フックの `wParam` は `nint` で届き、`uint` の定数ではパターン照合できないため、メッセージの定数は `int` にしました。
- `InputEvent.Kind` を `RecordedInputKind` enum に変更しました。テストの中で `InputEvent` を作っている17か所も、enum の名前に置き換えています。リテラルの `0` は enum に暗黙変換されてエラーにならないため、残っていた3か所も確認して揃えました。
- キー状態の判定に使う `0x8000`（`GetAsyncKeyState` の押下ビット）は、よく知られた慣用表現なので残しました。
- 確認: ソリューション全体のビルド（エラー・警告なし）、記録関連のテスト（Sandbox 専用を除く96件）すべて成功。
