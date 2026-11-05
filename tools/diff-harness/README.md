# 差分実行ハーネス

通常コア(`Instructions.Execute2`。翻訳キャッシュを使わない `InstructionExecutor`)の挙動が、リファクタリングの前後で変わっていないことを確かめる道具です。回帰テスト(`tests/Emu86.Tests`)の `core` は通常コアと IR の一致を見ますが、IR の対象外の命令(far CALL/RET、PUSHA/POPA、IN/OUT、LOOP、MOV CR/DR、記述子テーブル命令など)は通常コアにしか実装がないので、こちらは「前のコミットの通常コア」と比べます。

| ファイル | 役割 |
|---|---|
| `Diff.cs` | ハーネス本体。実モードと 32 ビット保護モード × 2 つの初期状態 × 命令列(far 制御転送・スタック・I/O・分岐・乗除算・シフト・ビット操作・文字列・ALU・BCD・CR/DR/MSR・記述子テーブルと SLDT/LLDT/VERR/LAR 系・FPU など)を、64KB の環境に置いて 1 命令ずつ実行し、CPU(スナップショット形式。FPU スタックと TR/LDTR を含む)・メモリと I/O ポートのハッシュ・MSR・DR を 1 ケース 1 行で書き出す。初期状態は FPU も含めて決まっている(片方は ST0-ST2 だけ有効、もう片方は 0・NaN・無限大・非正規化数を含む 8 本)。GDT には種別の違う 16 個の記述子(コード/データの各種、TSS、LDT、各ゲート、不在、GDT の外を指すセレクタ)を置き、VERR/VERW/LAR/LSL に通す。 |
| (同) 場面 `Scenes` | 特権レベルと記述子の内容で分かれる経路。保護モードの状態から、場面ごとに CPL・スタックの枠・GDT の項を置き換えて 1 命令実行する(行の先頭に `[場面名]`): 32 ビット IRET の復帰先 CS の検査(ヌル、データ、システム記述子、DPL と RPL の組、コンフォーミング、CPL 3 から、V86 への復帰)、リング 3 と V86 モードからの INT(TSS の SS0:ESP0 へのスタック切り替え、ゲートの DPL と IOPL の検査)、リング 3 からの記述子の照合、基底が 0 でない記述子と D/B ビットが落ちた記述子のロード。 |
| (同) 固定の結果 `Tables` | 命令の実行ではないバイト列を `table ...` の行で: VGA BIOS の ROM、PCI コンフィグ空間の初期値、モード設定(7 モード)後の BIOS データ領域と VGA の状態、ATA の IDENTIFY の応答。レイアウトの書き方を変えても中身が変わらないことを見る。 |
| `Emu86.Diff.csproj` | ハーネスのプロジェクト。本体の internal に触るため、アセンブリ名は回帰テストと同じ `Emu86.Tests`(本体の `InternalsVisibleTo` の対象。過去のコミットにもそのまま通る)。ソースの場所と比較対象の本体は MSBuild プロパティ `HarnessDir`/`Emu86Project` で差し替える(既定はこのフォルダと `../../Emu86.csproj`)。`Emu86.sln` にも入っているので、通常のビルドでコンパイルは確かめられる。 |
| `Compare.ps1` | 基準コミット(既定 `HEAD`)を `git worktree` に取り出し、ハーネスのプロジェクトを一時フォルダへコピーして基準側・作業ツリー側の本体に対してビルド・実行し、出力を比べる。差分が無ければ 0、有れば差分行を表示して 1 を返す。本体も毎回ビルドし直す(`--no-incremental`。全体で 30 秒ほど)。 |

```powershell
pwsh tools/diff-harness/Compare.ps1               # 作業ツリー vs HEAD
pwsh tools/diff-harness/Compare.ps1 -Base HEAD~1  # 作業ツリー vs 1 つ前のコミット
pwsh tools/diff-harness/Compare.ps1 -Keep         # 両方の出力(base.txt / new.txt)を残す
```

ハーネスの bin/obj は一時フォルダ(`%TEMP%\emu86-diff-*`)に置くので、リポジトリ側に残るのは作業ツリーの本体のいつものビルド出力だけです。

注意:

- CPU はスナップショット形式(`CPU.WriteTo` と `SaveRuntimeState`)の 16 進で比べるので、形式を変える変更(`CPU.Snapshot.cs`)では全件が差分になります。その場合は基準側と作業ツリー側の行を見て、差が形式の変更だけかを確かめてください。
- 基準側のビルドにも作業ツリーの `Diff.cs` を使います。ハーネスが使う本体の API(`Instructions.Execute2`、`EmuEnvironment(memory:)`、`Ram`/`IoPort`/`Tsc`/`Msrs`/`Dr`、`Paging.Sync`、`CPU.WriteTo`/`SaveRuntimeState`、レジスタのアクセサと FPU のフィールド、`VgaBiosStub.Rom`/`SetMode`、`Pci`/`Vga` の `SaveState`、`AtaDevice`/`DiskImage`)が基準コミットに無いと基準側のビルドが失敗します。
- 命令列を増やすときは `Cases` に足すだけです(特権や記述子を変える場合は `Scenes`、命令の実行ではないバイト列は `Tables`)。1 ケースは 1 命令の実行なので、REP 付きも含めて全ケースで数秒です。命令を書き換える前に、その命令のケースがあるかを確かめ、無ければ先に足して基準と一致することを見ておくと、書き換えの検証になります。
- ハーネスが差分を検出できることは、書き換えた箇所をわざと 1 か所壊して実行すると確かめられます(差分行が出て終了コード 1)。
- 壊した箇所をバックアップからのコピーで戻すと、ファイルの更新時刻が古いまま戻るので、通常の増分ビルド(`dotnet build`)は再コンパイルを省き、壊した版のバイナリが残ります。`Compare.ps1` は本体をビルドし直すので影響を受けませんが、回帰テストの前には、戻したファイルの更新時刻を進めるか `--no-incremental` でビルドし直してください。
