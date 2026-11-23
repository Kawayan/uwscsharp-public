# Windowsのネイティブ宣言

UWSCSharp.WindowsのDllImport宣言は、このInteropディレクトリに置きます。共通の宣言は既存のNativeのpartialファイルで管理し、特定の実装型に属する宣言は、その型名の*.Interop.csで管理します。

特定の実装型はpartialに分け、宣言元の型、ネスト、private/internalの可視性、引数と戻り値を維持しています。専用の構造体・デリゲート・SafeHandleは、元の型にある定義を参照します。リソースの所有・解放、例外処理、操作の手順は各実装側で管理します。

同じAPI名でも、EntryPoint、CharSet、ExactSpelling、SetLastError、CallingConvention、MarshalAs、引数の型や方向が異なる宣言は、その違いを確認せずに統合しないでください。今回は重複の削除や宣言設定の修正は行っていません。

## 配置の検査

リポジトリ直下で次を実行します。CIでも同じ検査を実行します。

```powershell
python scripts/check_windows_interop.py
python scripts/test_windows_interop_check.py
```

検査はWindowsプロジェクトのC#ソースを対象とし、Interop外にあるDllImport・LibraryImportの属性名とexternを拒否します。externも検査するため、DllImportの別名を使っても配置違反になります。コメント、文字列、文字リテラルは検査から除外し、無効なプリプロセッサ分岐の中も検査します。ネストしたFeature/Interopは許可先に含めません。読み取りに失敗した場合や対象ファイルがない場合も、成功とは扱いません。

この検査は配置を確認するもので、P/Invokeの型、マーシャリング、OS上の動作の正しさを保証しません。宣言を変更した場合は、関連テストとSandboxのFullを実行してください。CLI・WPF・Coreの宣言と、テスト用の宣言は今回の集約対象に含めていません。
