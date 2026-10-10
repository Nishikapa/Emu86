# Emu86

Intel 80386 (x86) の CPU エミュレータです。リアルモードで起動し、プロテクトモードへの遷移と 32 ビットコードの実行に対応します。SeaBIOS を実際にブートさせ、VHD/VHDX 形式のディスクイメージから MBR を読み込んで起動コードを実行できるところまで到達しています。命令デコードと実行を、C# の LINQ クエリ構文（モナド）で記述しているのが特徴です。

## 特徴

- **80386 命令セットを広範に実装** — データ移動・四則演算（乗除算含む）・論理/シフト/ローテート・ビット操作・スタック・分岐・サブルーチン（near/far）・文字列処理・ループ制御・関数フレーム・割り込み・システム命令（GDT/CRn/CPUID）。
- **プロテクトモード遷移と 32 ビット実行** — `CR0.PE` によるモード遷移、GDT ディスクリプタのデコード（セグメントベースキャッシュ、D/B ビットによる 32 ビットコードセグメント判定）、オペランド/アドレスサイズ/セグメントオーバーライドの各プレフィックスに対応。
- **周辺デバイスのエミュレーション** — CMOS（メモリサイズレジスタ）、仮想 8254 PIT（タイマー）、ATA（PIO、IDENTIFY/READ/WRITE SECTORS）を実装し、SeaBIOS の POST を完走させ、ATA 経由でディスクの MBR をロード・実行できます。タイマー IRQ0 は実機の 8259 と同じく、配送できるまで要求を保留します。
- **VGA 表示とキーボード入力** — VGA のメモリウィンドウ（`0xA0000`〜`0xBFFFF`、4 プレーン）とレジスタ群（Sequencer/Graphics Controller/CRTC/Attribute/DAC）をエミュレートし、テキストモード、640×480×16 色（プレーナ）、320×200×256 色（チェイン 4）を描画します。`--window` で VGA サイズ（640×480）のウィンドウを開き、キー入力はスキャンコードとして 8042 へ注入され IRQ1 で配送されます。
- **シリアルポートとカーネルデバッガ** — 16550 互換の COM1/COM2 を名前付きパイプに接続でき、NTLDR の起動メニューを自動操作して Windows をデバッグモードで起動できます（`--boot-debug`）。WinDbg を `com:pipe` で接続して XP のカーネルデバッグができます。
- **仮想 8086 モード** — ring0 の `IRETD` による V86 への遷移、V86 からの割り込み・例外配送（セグメントレジスタ付きフレーム）、V86 内 `INT n` の #GP 反射に対応し、Windows の HAL が行う V86 経由の BIOS 呼び出しを実行できます。
- **ディスクイメージ対応** — VHD（固定/可変長/差分）、VHDX/AVHDX（動的/差分、親ロケータ解決込み）、生イメージを 1 つの `ReadSector`/`WriteSector` インターフェースで扱います。書き込みは差分オーバーレイ（AVHDX）に蓄積され、ベースイメージは変更されません。
- **モナドによる命令記述** — 各命令を `State<V>` モナド上の LINQ クエリ式として記述。`from ... select` の連鎖で「メモリ読み取り → 計算 → フラグ更新 → 書き戻し」を合成します。CPU と環境は変更可能な状態を保持し、命令失敗時の CPU 復元は `InstructionExecutor` が担当します。
- **実行トレースとデバッグ出力** — 1 命令ごとに `CS:EIP` を `trace.log` に記録。SeaBIOS のデバッグコンソール（port 0x402）を標準エラー出力へ転送し、ブート進行を直接観測できます。

## ビルドと実行

.NET 10 SDK が必要です。表示ウィンドウに Windows Forms を使うため、ターゲットは `net10.0-windows` です。

```sh
# ビルド
dotnet build Emu86.sln -c Release

# 実行（コンソールのみ）
dotnet run -c Release

# VGA 画面をウィンドウに表示する
dotnet run -c Release -- --window
```

CPU はリセットベクタ `F000:FFF0` から起動し、命令を逐次実行します。トレースは `trace.log` に、SeaBIOS のブートログは標準エラー出力に出ます。

起動プロファイル（`Properties/launchSettings.json`）は「Emu86 (window)」が先頭なので、VS Code や Visual Studio の実行/デバッグ、および引数なしの `dotnet run` は `--window --notrace` 付きで起動します。引数を明示した場合はプロファイルの引数は使われません。

`--window` を付けると、エミュレーションを別スレッドで回しながら 640×480 のウィンドウに VGA 出力を約 30fps で描きます。表示解像度はビデオモードに従い（テキストは 640×400、mode 12h は 640×480、mode 13h は 320×200）、ウィンドウに合わせて最近傍補間で拡大縮小します。ウィンドウにフォーカスがあるときのキー入力は、Windows から受け取ったハードウェアスキャンコード（セット 1、拡張キーは `E0` 付き）をそのまま 8042 の出力バッファへ送り、IRQ1 として配送します。ウィンドウを閉じると実行を止め、通常終了と同じくスナップショットを保存します。命令数上限などで実行が先に終わった場合は、最後の画面を表示したままウィンドウが残ります。

表示領域をクリックするとマウスを捕捉し、相対移動・ボタン・ホイールを PS/2 マウス（8042 の AUX ポート、IRQ12、IntelliMouse 対応）として送ります。右 Ctrl キーで解放します（右 Ctrl 自体はゲストへ送りません）。ゲストがシステムリセット（8042 の 0xFE パルス/出力ポート、または 0xCF9）を要求すると、環境を作り直してリセットベクタから再起動します。ディスクの差分は同じオーバーレイに残るので、XP のミニセットアップのような「設定して再起動」も通ります。`--limit max` は上限なし、`--snapshot-interval` は定期保存の間隔（0 で終了時のみ）です。「Emu86 (window)」プロファイルはこの 2 つを付けて起動します。

### カーネルデバッガ（WinDbg）の接続

COM1（0x3F8、IRQ4）と COM2（0x2F8、IRQ3）を 16550 互換 UART としてエミュレートし、`--com1 <名前>` / `--com2 <名前>` でホストの名前付きパイプ `\\.\pipe\<名前>` に橋渡しします。`--boot-debug` を付けると、NTLDR の起動メニューを VRAM の文字を見ながら自動操作（F8 →「Debugging Mode」→ OS 選択）して、boot.ini を変えずに Windows をデバッグモード（XP の既定は COM2、19200bps）で起動します。起動プロファイル「Emu86 (window, kernel debug)」はこの組み合わせです。

```sh
dotnet run -c Release -- --window --notrace --limit max --snapshot-interval 1000000000 --com1 emu86-com1 --com2 emu86-com2 --boot-debug
# 別のコンソールで
windbgx -k com:pipe,port=\\.\pipe\emu86-com2,baud=19200,resets=0,reconnect
```

ゲストの時計は命令数ベース（100 万命令 = 1 秒）なので、KD のタイムアウトもその基準で進みます。パイプの送信は専用タスクが行い、WinDbg が未接続のあいだの送信は捨てられます（カーネルは応答が無ければ再送しつつ起動を続け、後から接続した WinDbg の Break で止まります）。

下部の「中断」ボタンで実行を一時停止でき（タイマ周期である 1 万命令の境界で止まります）、「再開」ボタンで続行します。押せるのは実行中は「中断」、中断中は「再開」だけです。中断中と停止後は「レジスタ表示」で CPU レジスタ（汎用・EIP/EFLAGS・セグメントと基底・CR0〜CR4・GDTR/IDTR/TR/LDTR・FPU）を示すダイアログを開けます。ダイアログは中断のたびに最新の状態へ更新されます。

本物の VGA BIOS は同梱していませんが、PCI バスに VGA 互換デバイス（00:02.0、1234:1111）を置き、その拡張 ROM BAR に最小限の VGA BIOS スタブ（`VgaBiosStub`）を見せています。SeaBIOS は POST 中にこの ROM を `0xC0000` へ取り込んで実行するので、SeaBIOS のバナーや「Booting from Hard Disk...」が画面に出ます。スタブの `INT 10h` はモード設定（3/12h/13h）、文字出力、カーソル、モード取得などを C# 側で処理し、Windows の bootvid/vga.sys が V86 モードで行う BIOS 呼び出しにも応えます。フォントが VRAM（プレーン 2）に読み込まれていないときは、ホストの等幅フォントから生成した 8×16 のフォントで描画します。

### 回帰テスト

`tests/Emu86.Tests` は本体を `ProjectReference` で参照する常設の回帰テストです。追加の NuGet テストフレームワークを使わないコンソール形式のため、`dotnet test` ではなく次のコマンドで実行します。失敗時は非ゼロの終了コードを返します。

```sh
# 全テスト（BIOS・実ディスクイメージの用意は不要）
dotnet run --project tests/Emu86.Tests -c Release

# 実行後処理・ディスクの回帰テストだけを実行
dotnet run --project tests/Emu86.Tests -c Release -- completion disk
```

`core` は両コアの状態一致、`state` は CPU 状態更新・オペランド幅選択・制御レジスタ同期、`devices` は周辺デバイスの入出力と分離前の状態・ログとの一致、`vga` は VGA の VRAM 振り分け・レジスタ・描画と V86 モード遷移・キーボード IRQ1、`decode` は ModRM/SIB・プレフィックス・ページ境界での命令取得、`snapshot` は CPU 復元と新旧スナップショット、`runner` は引数解析・実行・診断・リソース解放、`completion` は例外後も含む上限・定期保存、`disk` は小容量・チャンク境界・BAT 領域境界と異常系を検証します。テスト用ディスクは専用の一時ディレクトリに作成し、終了時に削除します。OS の長時間ブート検証とは別のテストです。

### 必要なファイル（いずれもリポジトリには含まれません）

| ファイル | 役割 |
|---------|------|
| `bios.bin` | 埋め込みリソースとして同梱すれば、起動時に 1 MB メモリ空間の末尾（`0x100000 - size`）へ配置されます。無ければゼロ初期化のまま起動します。 |
| `sample.vhd` または `sample.vhdx` | 実行時の作業ディレクトリに置くと、起動時にプライマリ ATA マスタとして自動接続されます（`vhd` を優先）。存在しなければディスク未接続として起動します。 |
| `sample.avhdx`（自動生成） | ディスクイメージが見つかった場合、差分オーバーレイ（VHDX 形式）として初回起動時に自動作成されます。書き込みはすべてここに蓄積され、ベースイメージ（`sample.vhd`/`.vhdx`）は一切変更されません。親イメージが変わった場合は `sample.avhdx.old` へ退避して作り直します。 |
| `snapshot.snap`（自動生成） | CPU・RAM・デバイス状態とディスク差分をまとめたスナップショット。詳細は次項。 |

### 起動設定

`RunOptions` が引数を先に解析し、不明なオプション・値の不足・不正な数値や範囲は、ディスクを開く前にエラーにします。`--help` で一覧を表示できます。命令数・LBA は十進数、アドレスは十六進数（`0x` は省略可）です。既定は分岐のみをトレースし、`--notrace` で無効化、`--trace-all` で全命令を記録します。

`--disk <base>` でベースイメージを明示でき、`--overlay <path>`（`--disk` と併用）で差分の保存先を指定できます。省略時はベースの拡張子を `.avhdx` に変えたパスを使います。選択したパスは起動時に絶対パスとして固定し、スナップショット復元にも同じ設定を渡します。`--dumplba <start> <count>` は既存のオーバーレイから読み出すだけで、新規作成せず `lba_dump.bin` に保存します。

```sh
dotnet run -c Release -- --disk images/system.vhdx --overlay images/work.avhdx --notrace --limit 1000000
```

### 実行の再開（スナップショット）

実行中は `SnapshotInterval`（既定 1 億命令）ごとに `snapshot.snap` へ自動保存します。`--snapshot <path>` で保存先を変更できます。v10 形式では CPU・FPU・RAM・I/O ポート・CMOS・PIT・PIC・PCI・ATA・キーボードコントローラ・ACPI PM・MSR・デバッグレジスタ・次のタイマー割り込み時刻に加えて、VGA の VRAM とレジスタ、PS/2 マウスの状態、COM1/COM2 のレジスタを保存します（v10）。TLB は復元後に再構築し、トレースや監視点などの診断設定は保存しません。

ディスク接続時は差分オーバーレイの内容も同じファイルに格納し、SHA-256 チェックサムを付けます。一時ファイルの書き込み・フラッシュが完了してから保存先を置き換えるため、保存途中で終了しても直前のチェックポイントが残ります。再開時はチェックサムと保存内容を読み終えてからディスクを復元し、CPU/RAM とディスクの世代の食い違いを防ぎます。ベースイメージ自体は含まれないため、保存時と同じベースイメージが必要です。

```sh
# 続きから再開する
dotnet run -c Release -- --resume
```

`--resume` を付けない場合は `F000:FFF0` から新規に起動します。v2〜v9 の旧形式も従来のレイアウトで読み込み、保存されていないデバイス状態は初期値になります（v7 以前には VGA 状態が無いのでテキストモードの初期状態に、v8 以前にはマウス状態が無いので未接続相当に、v9 以前には UART の状態が無いので初期値になります）。旧形式に `<snapshot>.avhdx` があれば組として復元し、なければ従来どおり現在の作業用オーバーレイを使います。その場合、メモリとディスクの同一時点への復元は保証されません。v7 の CPU/RAM の先頭位置は従来と同じなので、既存の RAM 解析スクリプトも利用できます。

## 現在の到達点

SeaBIOS を実行させた場合、次のところまで動作を確認しています。

1. PCI バス初期化、CMOS からのメモリサイズ検出、ATA コントローラ検出（`ata0-0: ... Hard-Disk`）を含む POST を完走。
2. `Booting from Hard Disk...` → ブートセクタ（MBR）を ATA 経由で読み込み、`0000:7C00` へジャンプして実行。
3. ブートローダー（GRUB 等）が `CR0.PE` をセットしてプロテクトモードへ遷移し、32 ビットコードセグメントでの実行を継続。
4. その後、狭いコード領域（数百バイト）を数千万命令にわたって繰り返し実行する区間に入ることを確認しています。実行アドレスは毎回変化しており、ハングではなく展開処理（自身の解凍など）と見られる正当な処理です。1 命令ずつ解釈実行するインタプリタ方式のため、この種の処理は実 CPU に比べて桁違いに多い命令数を要し、完了には数億命令規模の実行が必要になる場合があります。

ページングや PIC/IDT ベースの割り込みは未実装のため、実 OS カーネルの本格的な起動（タイマー割り込み待ちや DMA を要求する処理など）はまだ確認できていません。現状の律速はページング等の機能欠落よりも、インタプリタの実行速度（命令数上限 `InstructionLimit`、既定 5 億命令）である可能性が高いです。

## アーキテクチャ

エミュレータの中核は `State<V>` デリゲートです。

```csharp
public delegate (bool IsSuccess, V value, CPU cpu, string log)
    State<V>(EmuEnvironment env, CPU param, byte[] opecodes);
```

これは「環境・CPU 状態・オペコードを受け取り、成否・結果値・更新後の CPU 状態・ログを返す」状態モナドです。`Select` / `SelectMany`（LINQ クエリ構文）を実装しているため、各命令を次のように宣言的に書けます。

```csharp
// MOV r/m, Sreg (0x8C): セグメントレジスタを r/m16 へ格納する。
static State<Unit> Mov_8C =>
    from _1 in SetLog("Mov_8C")
    from m in ModRegRm()
    from addr in GetMemOrRegAddr(m.mod, m.rm)
    from sreg in GetSRegData(m.reg)
    from _2 in SetMemOrRegData(addr, sreg.ToTypeData())
    select unit;
```

各 `from` 句が CPU 状態を次へ受け渡し、いずれかが失敗すると以降は短絡します。`State` のコンビネータ自体はロールバックせず、失敗した操作が返した CPU を伝えます。命令を実行する場合は `InstructionExecutor.Step`（通常コアのみなら `Program.Execute2`）を使用します。

`InstructionExecutor` は命令開始前の CPU を退避し、未対応命令・例外時にレジスタと FPU を復元してページング状態を同期します。命令ごとのプレフィックスは `DecodeContext` に分離し、成功・失敗のどちらでも終了時に消去します。REP の途中でページフォルトが起きた場合は完了済みの反復の ECX・ESI・EDI・EAX・EFLAGS を保持し、命令先頭から残りの反復を再開します。メモリや I/O の既発生の副作用は取り消しません。割り込み配送にも同じ CPU 復元境界を適用します。

### 命令ディスパッチ

オペコードは 256 要素のテーブル（`OpecodeDic[]`）で引きます。1 バイト命令は `OneByteStates`、`0F` で始まる 2 バイト命令は `TwoBytesStates` に `(オペコード, 個数, 実装)` のタプルで登録され、起動時に展開されます。`Execute` がプレフィックス処理・オペコード読み取り・ディスパッチを行います。

`Group1`〜`Group8` のように ModRM の `reg` フィールドでサブ命令を選ぶグループ命令は、`Choice(reg, (n, 実装), ...)` で分岐します。オペランド幅は `Data`（`(int type, byte db, ushort dw, uint dd)`、type: 0=byte/1=word/2=dword）型エイリアスで統一的に扱い、`GlobalUsings.cs` で `MemAddr`（`(bool isMem, uint addr)`）と共に定義しています。

### 実行ループとタイマー割り込み

`Main`（`Program.Runner.cs`）は引数解析・起動環境の組み立て・再開処理を担当し、`--window` では `VgaWindow`（Windows Forms）を UI スレッドで開いてエミュレーションを別スレッドで走らせます。`EmulationRunner` が命令実行、タイマー IRQ0・キーボード IRQ1・ATA IRQ14 の配送、ホストから注入されたスキャンコードの 8042 への受け渡し、停止要求の確認、定期保存を制御します。割り込みは IF・PIC のマスク・in-service 状態を確認し、リアルモードでは IVT、プロテクトモードでは準備済みの IDT を経由して配送します。トレース・監視点・停止診断は `RunDiagnostics`、Windows 固有の IRP・デバッグ文字列・EBP チェーンの解釈は `WindowsDiagnostics` に分離しています。

実行は `InstructionLimit`（既定 5 億命令）に達するか、配送できない未対応命令・例外に遭遇するまで続きます。実行中は一定間隔（`SnapshotInterval`）ごとに `SnapshotStore` が状態を `snapshot.snap` へ保存し、`--resume` で続きを再開できます。

通常命令とページフォルト配送は、命令数・TSC の更新、進捗報告、定期保存・上限判定を共通経路で行います。ページフォルト配送自体は命令完了トレースには含めません。開始時点で `--limit` に到達済みの場合は追加の命令を実行せず、最終状態を保存します。

### VHDX の BAT

BAT（ブロック割当テーブル）の計算は `VhdxBatLayout` に集約しています。動的・固定ディスクは最後のペイロードまで、差分ディスクは最終チャンクのビットマップまでのエントリ数を計算し、作成時は領域を 1 MB 単位に切り上げます。読込・書込も同じ添字計算を使い、必要なエントリを格納できない BAT は読み込み時に拒否します。計算の基準は [Microsoft の MS-VHDX BAT 仕様](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-vhdx/af7334e6-ad2c-4378-9b81-afc1334a6ee7)です。

### 環境生成と所有権

`EmuEnvironment` 自体はファイルや埋め込みリソースを探索しません。`new EmuEnvironment(memory: ram, bios: biosBytes, ata: device)` でメモリ・BIOS・ATA を注入できます。RAM はコピーせず使用し、CMOS のメモリ量も配列の長さから設定します。引数省略時は 256 MB のゼロ初期化 RAM・BIOS なし・ディスクなしです。通常起動では `EnvironmentFactory` が埋め込み BIOS の読み込みと、`DiskConfiguration` で指定したディスクの接続を担当します。

環境の生成成功後は、渡した ATA とそのディスクを環境が所有します。`using var env = new EmuEnvironment(...)` とすると、`EmuEnvironment` → `AtaDevice` → `DiskImage` → 親ディスクの順で解放されます。`DiskImage.Close()` も互換用に残しています。ディスク付きスナップショットを直接復元する場合は、ディスク未接続の環境と明示的な設定を `SnapshotStore.Load(path, env, diskConfiguration)` に渡してください。保存形式は変更していないため、注入する RAM のサイズは保存時と復元時で揃える必要があります（CLI は常に既定サイズ）。診断フラグも環境・デバイスごとの状態で、別インスタンスには影響しません。

## ファイル構成

| ファイル | 役割 |
|---------|------|
| `GlobalUsings.cs` | `Data`/`MemAddr` 型エイリアスの定義。 |
| `State.cs` | `State<V>` モナドの定義と LINQ コンビネータ（`Select`/`SelectMany`/`Choice`/`Sequence`/`Many0` 等）。 |
| `CPU.cs` | `CPU` 状態、レジスタアクセサ、共通 ALU/フラグ更新処理を呼び出すモナド操作、シフト計算。 |
| `Alu.cs` | 通常コアと高速コアで共有する算術・論理演算、フラグ更新、条件分岐の判定。 |
| `FastCore.cs` | 高速実行コア。演算規則は `Alu.cs`、メモリアクセスは `Environment.cs` の共通処理を使用し、未対応命令は通常コアへ委譲。 |
| `InstructionExecutor.cs` | 両コアの実行・CPU 復元境界、命令ごとのデコード状態、REP の再開位置。 |
| `InstructionDecoding.cs` / `Registers.cs` | 通常コアの ModRM/SIB 実効アドレス計算と、両コア共通の配列を作らないレジスタ参照。変位・即値は必要な幅だけを直接読み取る。 |
| `Program.Runner.cs` / `RunOptions.cs` | CLI の起動処理、引数解析・検証、セクタダンプ。 |
| `EmulationRunner.cs` | 命令実行ループ、割り込み配送、定期保存の呼び出し。 |
| `RunDiagnostics.cs` / `WindowsDiagnostics.cs` | トレース・監視点・停止診断と、Windows 固有の構造体・スタック解析。 |
| `EnvironmentFactory.cs` / `DiskConfiguration.cs` | 埋め込み BIOS の読み込み、ディスク探索・明示的なパス設定、通常起動用の環境生成。 |
| `SnapshotStore.cs` | 保存形式の版管理、チェックサム検証、ディスク差分を含むチェックポイントの確定と復元。 |
| `CPU.Snapshot.cs` / `Environment.Snapshot.cs` | CPU・FPU と環境・デバイスの追加保存状態。PCI と ATA の内部状態は各デバイスが保存・復元。 |
| `Environment.cs` | `EmuEnvironment`（既定 256 MB メモリ、64 KB I/O ポート、ATA 接続）と、メモリ/レジスタ/スタックアクセス、GDT ディスクリプタデコード、文字列・ビット操作・I/O ポートの振り分け。 |
| `Environment.Devices.cs` | 環境ごとのデバイス所有と、既存の状態名への転送プロパティ。保存形式のバイト順・バージョンは変更しない。 |
| `PicDevice.cs` / `PitDevice.cs` / `CmosDevice.cs` / `KeyboardController.cs` / `AcpiPmDevice.cs` | PIC・PIT・CMOS・8042・ACPI の状態と入出力処理。PIC のマスタ/スレーブは共通実装の独立したインスタンス。 |
| `Ext.cs` | 型変換ユーティリティ（`ToTypeData`/`MapType`/`Choice_` 等）。 |
| `Program.cs` / `Program.*.cs` | 命令の実装とオペコードテーブル。起動処理は `Program.Runner.cs`。 |
| `Disk.cs` | `DiskImage`（VHD/VHDX/AVHDX/生イメージの CoW ディスクバックエンド）と `AtaDevice`（最小限の ATA PIO デバイス）。 |
| `VhdxBatLayout.cs` | VHDX のペイロード・ビットマップの位置、BAT の必要エントリ数・領域サイズ。 |
| `tests/Emu86.Tests/` | 本体を参照する回帰テストと、実行状態・トレースの比較用データ。 |

## 実装済み命令

| 分類 | 命令 |
|------|------|
| データ移動 | `MOV`（reg↔r/m、即値、moffs、Sreg 双方向）、`MOVZX`/`MOVSX`、`XCHG`、`XLAT`、`LEA` |
| 算術 | `ADD`/`ADC`/`SUB`/`SBB`/`CMP`、`INC`/`DEC`（全幅・メモリ）、`MUL`/`IMUL`/`DIV`/`IDIV`（`IMUL` は 1/2/3 オペランド形式）、`NEG`、`CBW`/`CWD` |
| 論理/シフト | `AND`/`OR`/`XOR`/`NOT`/`TEST`、`ROL`/`ROR`/`RCL`/`RCR`/`SHL`/`SHR`/`SAR`、`SHLD`/`SHRD` |
| ビット操作 | `BT`/`BTS`/`BTR`/`BTC`、`BSF`/`BSR`、`SETcc` |
| スタック | `PUSH`/`POP`（reg/imm/r/m/Sreg）、`PUSHA`/`POPA`、`PUSHF`/`POPF` |
| 制御転送 | `Jcc`（rel8/rel16）、`JMP`（near/far/間接）、`CALL`/`RET`（near/far/間接）、`LOOP`/`LOOPE`/`LOOPNE`、`JCXZ` |
| 文字列 | `MOVS`/`STOS`/`LODS`/`CMPS`/`SCAS` + `REP`/`REPE`/`REPNE`（16/32 ビットアドレッシング両対応） |
| 関数フレーム | `ENTER`/`LEAVE` |
| 割り込み | `INT`/`INT3`/`INTO`/`IRET`（リアルモード IVT 方式） |
| フラグ/その他 | `CLC`/`STC`/`CMC`/`CLD`/`STD`/`CLI`/`STI`（実際に IF を操作）、`SAHF`/`LAHF`、`NOP`/`HLT`、`IN`/`OUT` |
| システム | `LGDT`/`LIDT`/`SGDT`/`SIDT`、`MOV CRn`、`CPUID`（最小限、EAX=0/1 のみ）、プロテクトモード遷移、32 ビットコード実行 |

## 周辺デバイス

| デバイス | ポート | 実装範囲 |
|---------|--------|---------|
| CMOS/RTC | 0x70（インデックス）/ 0x71（データ） | メモリサイズレジスタ（0x15-0x18, 0x30-0x35）を SeaBIOS 用に設定済み。 |
| 8254 PIT | 0x40（チャネル0データ）/ 0x43（コントロール） | チャネル0のみ。実時間ではなく、ラッチ操作のたびにカウンタを減算する簡易モデル。 |
| ATA（プライマリ） | 0x1F0-0x1F7, 0x3F6 | IDENTIFY DEVICE、READ/WRITE SECTORS（PIO）に対応。マスタドライブのみ、割り込み（IRQ14）は使わずポーリング前提。スレーブ・セカンダリコントローラ・DMA・ATAPI は非対応。 |
| デバッグコンソール | 0x402 | SeaBIOS のデバッグ出力を標準エラー出力へそのまま転送。 |

## 主な制限

- ページング（CR3/ページテーブル）は未実装です。
- 割り込みはリアルモードの IVT（`vector × 4`）方式のみ。プロテクトモードの IDT ゲート経由のディスパッチ、PIC 8259、ハードウェア外部 IRQ は未実装です（IDT レジスタ自体は `LIDT`/`SIDT` で保持されます）。
- 補助キャリーフラグ（AF）は更新しません。
- セグメントオーバーライドプレフィックスは ModRM 実効アドレス計算と moffs には反映されますが、文字列命令（`MOVS`/`STOS`/`LODS`/`CMPS`/`SCAS`）は DS:SI・ES:DI 固定で、オーバーライドは反映されません。
- ATA はマスタドライブ・PIO 転送のみ。VHD 形式への書き込みは非対応（差分書き込みは常に AVHDX オーバーレイに限定）。
- 自動回帰テストはありますが、OS の長時間ブート確認は別途手動で行います。
