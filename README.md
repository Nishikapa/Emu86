# Emu86

Intel 80386 (x86) の CPU エミュレータです。リアルモードで起動し、プロテクトモードへの遷移と 32 ビットコードの実行に対応します。SeaBIOS を実際にブートさせ、VHD/VHDX 形式のディスクイメージから MBR を読み込んで起動コードを実行できるところまで到達しています。命令デコードと実行を、C# の LINQ クエリ構文（モナド）で記述しているのが特徴です。

## 特徴

- **80386 命令セットを広範に実装** — データ移動・四則演算（乗除算含む）・論理/シフト/ローテート・ビット操作・スタック・分岐・サブルーチン（near/far）・文字列処理・ループ制御・関数フレーム・割り込み・システム命令（GDT/CRn/CPUID）。
- **プロテクトモード遷移と 32 ビット実行** — `CR0.PE` によるモード遷移、GDT ディスクリプタのデコード（セグメントベースキャッシュ、D/B ビットによる 32 ビットコードセグメント判定）、オペランド/アドレスサイズ/セグメントオーバーライドの各プレフィックスに対応。
- **周辺デバイスのエミュレーション** — CMOS（メモリサイズレジスタ）、仮想 8254 PIT（タイマー）、ATA（PIO、IDENTIFY/READ/WRITE SECTORS）を実装し、SeaBIOS の POST を完走させ、ATA 経由でディスクの MBR をロード・実行できます。
- **ディスクイメージ対応** — VHD（固定/可変長/差分）、VHDX/AVHDX（動的/差分、親ロケータ解決込み）、生イメージを 1 つの `ReadSector`/`WriteSector` インターフェースで扱います。書き込みは差分オーバーレイ（AVHDX）に蓄積され、ベースイメージは変更されません。
- **モナドによる命令記述** — 各命令を `State<V>` モナド上の LINQ クエリ式として宣言的に記述。`from ... select` の連鎖で「メモリ読み取り → 計算 → フラグ更新 → 書き戻し」を副作用なく合成します。
- **実行トレースとデバッグ出力** — 1 命令ごとに `CS:EIP` を `trace.log` に記録。SeaBIOS のデバッグコンソール（port 0x402）を標準エラー出力へ転送し、ブート進行を直接観測できます。

## ビルドと実行

.NET 10 SDK が必要です。

```sh
# ビルド
dotnet build Emu86.sln -c Release

# 実行
dotnet run -c Release
```

CPU はリセットベクタ `F000:FFF0` から起動し、命令を逐次実行します。トレースは `trace.log` に、SeaBIOS のブートログは標準エラー出力に出ます。

### 必要なファイル（いずれもリポジトリには含まれません）

| ファイル | 役割 |
|---------|------|
| `bios.bin` | 埋め込みリソースとして同梱すれば、起動時に 1 MB メモリ空間の末尾（`0x100000 - size`）へ配置されます。無ければゼロ初期化のまま起動します。 |
| `sample.vhdx` または `sample.vhd` | プロジェクト直下に置くと、起動時にプライマリ ATA マスタとして自動接続されます（`vhdx` を優先）。存在しなければディスク未接続として起動します。 |
| `sample.avhdx`（自動生成） | ディスクイメージが見つかった場合、差分オーバーレイ（VHDX 形式）として初回起動時に自動作成されます。書き込みはすべてここに蓄積され、ベースイメージ（`sample.vhdx`/`.vhd`）は一切変更されません。 |

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

各 `from` 句が CPU 状態を次へ受け渡し、いずれかが失敗すると以降は短絡します。失敗は「未実装オペコード」を表現し、実行ループの停止条件になります。

### 命令ディスパッチ

オペコードは 256 要素のテーブル（`OpecodeDic[]`）で引きます。1 バイト命令は `OneByteStates`、`0F` で始まる 2 バイト命令は `TwoBytesStates` に `(オペコード, 個数, 実装)` のタプルで登録され、起動時に展開されます。`Execute` がプレフィックス処理・オペコード読み取り・ディスパッチを行います。

`Group1`〜`Group8` のように ModRM の `reg` フィールドでサブ命令を選ぶグループ命令は、`Choice(reg, (n, 実装), ...)` で分岐します。オペランド幅は `Data`（`(int type, byte db, ushort dw, uint dd)`、type: 0=byte/1=word/2=dword）型エイリアスで統一的に扱い、`GlobalUsings.cs` で `MemAddr`（`(bool isMem, uint addr)`）と共に定義しています。

### 実行ループとタイマー割り込み

`Main`（Program.cs）は1命令ずつ実行するループで、一定命令数（`IrqPeriod`）ごとに、リアルモードかつ IF=1 のときだけ `INT 08h`（タイマー割り込み、リアルモード IVT 方式）を注入します。これにより SeaBIOS の BDA ティックカウンタが進み、遅延ループが完了します。プロテクトモード中はこの注入を行わないため、保護モードのコードがハードウェアタイマー割り込みに依存する処理は現状進みません。

実行は `InstructionLimit`（既定 5 億命令）に達するか、未実装オペコード・例外に遭遇するまで続きます。1 命令ずつ状態モナドを合成して解釈するため、ブートローダーの展開処理のような計算量の大きい区間では実 CPU に比べて非常に多くの命令数・実行時間を要します。

## ファイル構成

| ファイル | 役割 |
|---------|------|
| `GlobalUsings.cs` | `Data`/`MemAddr` 型エイリアスの定義。 |
| `State.cs` | `State<V>` モナドの定義と LINQ コンビネータ（`Select`/`SelectMany`/`Choice`/`Sequence`/`Many0` 等）。 |
| `CPU.cs` | `CPU` 構造体（汎用/セグメント/制御レジスタ、セグメントベースキャッシュ、EFLAGS、GDT/IDT レジスタ、プロテクトモード状態）、レジスタアクセサ、フラグ更新、ALU/シフト計算。 |
| `Environment.cs` | `EmuEnvironment`（32 MB メモリ、64 KB I/O ポート、CMOS、PIT、ATA 接続の実体）と、メモリ/レジスタ/スタックアクセス、GDT ディスクリプタデコード、実効アドレス計算（セグメントオーバーライド込み）、文字列・ビット操作・I/O ポート分岐などのヘルパー。 |
| `Ext.cs` | 型変換ユーティリティ（`ToTypeData`/`MapType`/`Choice_` 等）。 |
| `Program.cs` | 全命令の実装、オペコードテーブル、実行ループ（`Main`）とタイマー割り込み注入。 |
| `Disk.cs` | `DiskImage`（VHD/VHDX/AVHDX/生イメージの CoW ディスクバックエンド）と `AtaDevice`（最小限の ATA PIO デバイス）。 |

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
- 自動テストは未整備です（手動でのブート確認のみ）。
