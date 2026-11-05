# 差分実行ハーネス

通常コア(`Instructions.Execute2`。翻訳キャッシュを使わない `InstructionExecutor`)の挙動が、リファクタリングの前後で変わっていないことを確かめる道具です。回帰テスト(`tests/Emu86.Tests`)の `core` は通常コアと IR の一致を見ますが、IR の対象外の命令(far CALL/RET、PUSHA/POPA、IN/OUT、LOOP、MOV CR/DR、記述子テーブル命令など)は通常コアにしか実装がないので、こちらは「前のコミットの通常コア」と比べます。

| ファイル | 役割 |
|---|---|
| `Diff.cs` | ハーネス本体。実モードと 32 ビット保護モード × 2 つの初期状態 × 命令列(far 制御転送・スタック・I/O・分岐・乗除算・シフト・ビット操作・文字列・ALU・BCD など)を、64KB の環境に置いて 1 命令ずつ実行し、CPU(スナップショット形式)・メモリと I/O ポートのハッシュ・MSR・DR を 1 ケース 1 行で書き出す。 |
| `Emu86.Diff.csproj` | ハーネスのプロジェクト。本体の internal に触るため、アセンブリ名は回帰テストと同じ `Emu86.Tests`(本体の `InternalsVisibleTo` の対象。過去のコミットにもそのまま通る)。ソースの場所と比較対象の本体は MSBuild プロパティ `HarnessDir`/`Emu86Project` で差し替える(既定はこのフォルダと `../../Emu86.csproj`)。`Emu86.sln` にも入っているので、通常のビルドでコンパイルは確かめられる。 |
| `Compare.ps1` | 基準コミット(既定 `HEAD`)を `git worktree` に取り出し、ハーネスのプロジェクトを一時フォルダへコピーして基準側・作業ツリー側の本体に対してビルド・実行し、出力を比べる。差分が無ければ 0、有れば差分行を表示して 1 を返す。 |

```powershell
pwsh tools/diff-harness/Compare.ps1               # 作業ツリー vs HEAD
pwsh tools/diff-harness/Compare.ps1 -Base HEAD~1  # 作業ツリー vs 1 つ前のコミット
pwsh tools/diff-harness/Compare.ps1 -Keep         # 両方の出力(base.txt / new.txt)を残す
```

ハーネスの bin/obj は一時フォルダ(`%TEMP%\emu86-diff-*`)に置くので、リポジトリ側に残るのは作業ツリーの本体のいつものビルド出力だけです。

注意:

- CPU はスナップショット形式(`CPU.WriteTo`)の 16 進で比べるので、形式を変える変更(`CPU.Snapshot.cs`)では全件が差分になります。その場合は基準側と作業ツリー側の行を見て、差が形式の変更だけかを確かめてください。
- 基準側のビルドにも作業ツリーの `Diff.cs` を使います。ハーネスが使う本体の API(`Instructions.Execute2`、`EmuEnvironment(memory:)`、`Ram`/`IoPort`/`Tsc`/`Msrs`/`Dr`、`Paging.Sync`、`CPU.WriteTo`、レジスタのアクセサ)が基準コミットに無いと基準側のビルドが失敗します。
- 命令列を増やすときは `Cases` に足すだけです。1 ケースは 1 命令の実行なので、REP 付きも含めて全ケースで数秒です。
