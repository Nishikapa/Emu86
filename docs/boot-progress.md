# Linux ブート進捗メモ

Emu86 で `sample.vhdx`(Debian 4.19.0-21-686-pae)を起動する作業の進捗記録。
別環境でも再開できるよう、現状・調査手法・次の一手をまとめる。

## 現在の到達点(2026-07-06 時点)

SeaBIOS → GRUB → カーネル展開 → ページング → メモリ初期化 → CPU バグ検査
→ PCI/デバイス初期化 → **PnP BIOS 呼び出し** まで到達。
dmesg は Spectre/MDS 緩和、AppArmor、VFS、PnPBIOS スキャンまで出力。

命令数の目安(冷起動):
- ~3.10B: GRUB→カーネル、ページング有効化(cr0.PG=1)
- ~3.30B: `check_fpu()`(FDIV バグ検査、x87 が必要)
- ~3.45B: PnP BIOS への far call(`lcall 0x0090:0`)

## 直近で修正したバグ(このブランチの未コミット分に対応)

いずれも「FPU が必要」と思われた停止の背後にあった別バグ:

1. **32bit PUSH/POP セグメントレジスタ幅**(コミット済 `bbc5457`)
   `push es`/`push ds` 等が 32bit モードで 2 バイトしか積まず、割り込みフレームが
   4 バイトずれて `common_exception` がハンドラを誤読 → #PF ストームでコード破壊。
2. **PIC in-service/EOI**(コミット済 `bbc5457`)。
3. **REP + オペランドサイズ prefix**(コミット済 `d87dbf4`)
   `F3 66 AB`(REP STOSW)で REP の後の 0x66 を消費していなかった。
4. **x87 FPU 実装**(未コミット、`Fpu.cs` 新規)
   `check_fpu()` の FDIV バグ検査を通すため。ST スタック8本を double で保持。
   ロード/ストア(f32/f64/f80/int16/32/64)、四則演算(全方向・P 付き)、比較、
   FXCH/FCHS/FABS/定数、FNSAVE/FRSTOR(108 バイト形式・80bit 変換)、FNINIT 等。
   WAIT(0x9B)も追加。
5. **far CALL(0x9A)のオペランドサイズ対応**(未コミット)
   32bit モードでオフセットを 32bit で読むべきところ 16bit で読み、PnP BIOS 呼び出し
   `lcall 0x0090:0x00000000` を `CS=0` ロードと誤解釈して暴走。RETF(CA/CB)も同様に修正。
6. **スタック幅を SS.B で決める(`stack32`)**(未コミット、これが最新の作業)
   スタック操作の SP/ESP 選択は CS.D(code32)ではなく SS.B(スタックセグメントの
   B ビット)で決まる。PnP BIOS は 16bit CS + 32bit SS で動くため両者が食い違い、
   `push esp` が SP(0x3e84、未マップ)へ書いて #PF していた。
   CPU に `stack32` を追加、`LoadSReg` で SS ロード時に設定、Push/Pop/RET/LEAVE/
   Retf を stack32 基準に変更。スナップショットにも保存。

## 現在検証中(未確定)

`stack32` 修正で PnP BIOS の 16bit コードが正しいスタックで動くか **冷起動で検証中に中断**。
ビルドは通り、10M 命令の fast/slow パリティは一致。PnP BIOS 通過の可否は未確認。

**次にやること**: 冷起動(`--limit 6000000000`)で PnP BIOS を通過するか確認。
```
dotnet build
./bin/Debug/net10.0/Emu86.exe --notrace --snapshot cold.snap --limit 6000000000
```
- 通過したら次のブロッカー(デバイスドライバ、ルートFS マウント、init 起動など)へ。
- ダブルフォルト/未実装命令で止まったら、下記の調査手法で原因を特定。

## 次に想定される課題

- **TSS/タスクゲート未実装**: NMI(2)と #DF(8)は 32bit Linux/Windows ともタスクゲート。
  現状 #DF 相当(#PF 配送中の #PF 等)は診断表示して停止。正規配送には TSS ハード
  タスクスイッチ(バックリンク・レジスタ退避/復元)の実装が必要。
- **ルートファイルシステム(ext4)マウント**: ATA PIO 経由でカーネルがルートを読む。
- **userspace 到達**: init/systemd は FPU/文字列命令を多用。x87 は実装済みだが SSE は未。

## 調査手法(このリポジトリのデバッグ用フラグ)

すべて指定時のみ有効(通常実行は無コスト)。原因の局所化に有効だったもの:

- `--brhist`: 直近 64 分岐の履歴を STOP 時に出力。CS=0 化などの捕捉トラップも同経路。
- `--watch <physLo> <physHi>`: 物理アドレス範囲への書き込みで停止し犯人命令の EIP を出力。
- `--wlog <physLo> <physHi>`: 同範囲への書き込みを値付きでログ(`--pftrap` と併用)。
- `--esptrap`: ESP が特定コード領域を指す不正値になった瞬間を捕捉。
- `--intlog`: プロテクトモード割り込み配送(vector/err/eip/esp)をログ。
- `--pftrap`: 最初の保護違反 #PF で停止(スナップショット保存)。
- `--breakeip <hex>` / `--breakeax <hex>`: 指定 EIP(かつ EAX 一致)で停止しレジスタ+スタックダンプ。
- `--noirq`: タイマ割り込み注入を無効化。
- `--slow`: 高速コアを使わず全命令をモナド版で実行(fast/slow パリティ比較の基準)。
- `--snapshot <path>` / `--resume`: チェックポイントの保存先指定と再開。

### 決め手になった外部ツール
- **capstone**(`/c/Python314/python`): スナップショット RAM の逆アセンブル。
- **Unicorn**(pip 導入済): 参照 CPU。#PF が実 CPU でも起きるか(私のバグか仕様通りか)を
  スナップショットをロードして 1 命令実行で判定。
- **カーネル printk ログバッファの直接パース**: 0x1a779e4 付近の struct printk_log を辿り
  dmesg を再構成(進捗確認)。
- **スナップショットのページテーブル手動ウォーク**(PAE 3 段)で PTE/フラグを検証。

## 回帰検証の原則

- 変更後は必ず **先頭 10M 命令の fast/slow 分岐トレースがバイト一致**することを確認
  (`--slow` 版と既定版の trace.log を SHA256 比較)。real-mode SeaBIOS 区間なので
  スタック/FPU 変更の多くはここに現れないが、命令的コアとモナド版の等価性は守る。
- スナップショットはバージョン管理(現在 v5: TSC/MSR/PIC/x87/stack32 を末尾追記、旧版互換ロード)。

## 主要ファイル

- `Fpu.cs`: x87 FPU 本体(新規)。
- `CPU.cs`: レジスタ状態。`code32`/`stack32`、FPU スタック、スナップショット直列化。
- `Environment.cs`: メモリ/ページング(MMU・TLB・#PF)、ModRM、スタック、LoadSReg、
  ポート I/O(CMOS/PIT/PIC/ATA/debugcon)、割り込み配送。
- `FastCore.cs`: 命令的高速コア(方針 B)。未対応命令はモナド版へフォールバック。
- `Program.cs`: 命令ハンドラ本体(モナド版)。
- `Program.OpcodeTable.cs`: ディスパッチテーブル、プレフィックス処理。
- `Program.Runner.cs`: 実行ループ、タイマ IRQ 注入、#PF 配送、各種デバッグトラップ。
- `Program.Snapshot.cs`: スナップショット保存/復元。
- `Disk.cs`: VHD/VHDX/AVHDX(COW オーバーレイ)+ ATA PIO デバイス。
