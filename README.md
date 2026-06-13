# Emu86

Intel 80386 (x86) の命令エミュレータです。リアルモードで起動し、プロテクトモードへの遷移と 32 ビットコードの実行にも対応します。命令デコードと実行を、C# の LINQ クエリ構文（モナド）で記述しているのが特徴です。

## 特徴

- **80386 リアルモード命令セットを広範に実装** — データ移動・四則演算（乗除算含む）・論理/シフト/ローテート・ビット操作・スタック・分岐・サブルーチン（near/far）・文字列処理・ループ制御・関数フレーム・割り込み。
- **プロテクトモード遷移と 32 ビット実行** — `CR0.PE` のセットによるモード遷移、32 ビットコードセグメント（D=1）の実行、オペランド/アドレスサイズプレフィックスに対応。
- **モナドによる命令記述** — 各命令を `State<V>` モナド上の LINQ クエリ式として宣言的に記述。`from ... select` の連鎖で「メモリ読み取り → 計算 → フラグ更新 → 書き戻し」を副作用なく合成します。
- **実行トレース** — 1 命令ごとに `CS:EIP` を `trace.log` に記録。未実装オペコードに到達すると、その位置とバイト列を表示して停止します。

## ビルドと実行

.NET 10 SDK が必要です。

```sh
# ビルド
dotnet build Emu86.sln -c Release

# 実行（BIOS イメージ bios.bin が必要）
dotnet run -c Release
```

実行すると CPU はリセットベクタ `F000:FFF0` から起動し、命令を逐次実行します。トレースは `trace.log` に出力されます。

### BIOS イメージ

エミュレータは起動時に、埋め込みリソース `bios.bin` が存在すれば 1 MB メモリ空間の末尾（`0x100000 - size`）へ配置します。`bios.bin` はリポジトリには含まれません（`.gitignore` で除外）。実行するにはプロジェクト直下に `bios.bin` を置いてください。存在しない場合はゼロ初期化のまま起動します。

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

`Group1`〜`Group8` のように ModRM の `reg` フィールドでサブ命令を選ぶグループ命令は、`Choice(reg, (n, 実装), ...)` で分岐します。

## ファイル構成

| ファイル | 役割 |
|---------|------|
| `State.cs` | `State<V>` モナドの定義と LINQ コンビネータ（`Select`/`SelectMany`/`Choice`/`Sequence`/`Many0` 等） |
| `CPU.cs` | `CPU` 構造体（汎用/セグメント/制御レジスタ、EFLAGS、プロテクトモード状態）、レジスタアクセサ、フラグ更新、シフト計算 |
| `Environment.cs` | `EmuEnvironment`（1 MB メモリ + 64 KB I/O ポート）、メモリ/レジスタ/スタックアクセス、実効アドレス計算、文字列・ビット操作などのヘルパー |
| `Ext.cs` | 型変換ユーティリティ（`ToTypeData`/`MapType`/`Choice_` 等） |
| `Program.cs` | 全命令の実装、オペコードテーブル、実行ループ（`Main`） |

オペランド幅は `(int type, byte db, ushort dw, uint dd)` タプルで統一的に扱います（`type`: 0=byte, 1=word, 2=dword）。

## 実装済み命令

| 分類 | 命令 |
|------|------|
| データ移動 | `MOV`（reg↔r/m、即値、moffs、Sreg 双方向）、`MOVZX`/`MOVSX`、`XCHG`、`XLAT`、`LEA` |
| 算術 | `ADD`/`ADC`/`SUB`/`SBB`/`CMP`、`INC`/`DEC`（全幅・メモリ）、`MUL`/`IMUL`/`DIV`/`IDIV`、`NEG`、`CBW`/`CWD` |
| 論理/シフト | `AND`/`OR`/`XOR`/`NOT`/`TEST`、`ROL`/`ROR`/`RCL`/`RCR`/`SHL`/`SHR`/`SAR` |
| ビット操作 | `BT`/`BTS`/`BTR`/`BTC`、`BSF`/`BSR`、`SETcc` |
| スタック | `PUSH`/`POP`（reg/imm/r/m）、`PUSHA`/`POPA`、`PUSHF`/`POPF` |
| 制御転送 | `Jcc`（rel8/rel16）、`JMP`（near/far/間接）、`CALL`/`RET`（near/far/間接）、`LOOP`/`LOOPE`/`LOOPNE`、`JCXZ` |
| 文字列 | `MOVS`/`STOS`/`LODS`/`CMPS`/`SCAS` + `REP`/`REPE`/`REPNE` |
| 関数フレーム | `ENTER`/`LEAVE` |
| 割り込み | `INT`/`INT3`/`INTO`/`IRET`（リアルモード IVT 方式） |
| フラグ/その他 | `CLC`/`STC`/`CMC`/`CLD`/`STD`/`CLI`/`STI`、`SAHF`/`LAHF`、`NOP`/`HLT`、`IN`/`OUT` |
| システム | `LGDT`/`LIDT`、プロテクトモード遷移（`MOV CRn`）、32 ビットコード実行 |

### 主な制限

- リアルモードと、プロテクトモードの平坦な 32 ビットコード実行が対象です。ページング、特権レベル、保護チェック、ハードウェア割り込み（外部 IRQ）はモデル化していません。
- 割り込みはリアルモードの IVT（`vector × 4`）方式のみ。プロテクトモードの IDT ゲート経由は未対応です。
- 補助キャリーフラグ（AF）は更新しません。
- セグメントオーバーライドプレフィックス（`2E`/`26`/`64`/`65` 等）はデコードしますが、メモリアクセスへの反映は限定的です。
