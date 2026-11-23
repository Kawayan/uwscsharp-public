# SOUND観測用の素材

`scripts/build_sound_fixtures.ps1`で生成した自作の無音・黒画面の素材を保存する。
WAVは22050Hz・16bit PCM・モノラルの1.2秒、AVIは16×16・10fps・非圧縮BGR24の1.2秒である。
この2形式の長さとコーデックはffprobeでも確認する。

MIDIはFormat 0・1トラック・四分音符あたり100 ticks、テンポ500000μsで、240 ticks後に終了する38byteのファイルである。
音符の発音を要求しない。MIDIのデバイス依存の実行結果は、WindowsのMCI対照と原版観測で別に確認する。

比較用の`sound-midi-note.mid`はvelocity=1のNote Onと、240 ticks後の明示的なNote Offを持つ42byteの素材である。
無音のMIDIと区別し、時間形式をミリ秒へ設定してから長さと再生を調べる。

素材の再生成にはPowerShell 7とffmpeg・ffprobeを使う。
テスト時は保存済みの素材を使い、ゲストへのffmpegの導入は不要である。

```powershell
pwsh -NoProfile -File scripts/build_sound_fixtures.ps1
```

音声や映像のAPI成功・経過時間を、実際の音声出力の確認とは扱わない。
環境が成立しなければ、実音声の受入は未実施として記録する。
