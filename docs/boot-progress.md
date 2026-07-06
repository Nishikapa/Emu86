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

## 現在の到達点の更新(2026-07-06、PnP BIOS 通過後)

`stack32`/far-call/FPU 修正は妥当だった。冷起動は **PnP BIOS(~3.45B)を通過**し、
5.7B+ 命令まで到達。停止・暴走ではなく、**カーネルの crypto 自己テスト(testmgr)を実行中**。

### 判明した事実
- 停止に見えたのは、initcall 中の **crypto testmgr 自己テスト**。
  進捗プリントが `cs=0060 eip=c1380xxx` 一点に見えるのは、実行時間の大半を
  **MPI(多倍長整数)乗算ループ**が占めるサンプリング偏り。上位の RSA/DH は前進している。
- 逆アセンブルで c1380bd0/c1380c50/c1380d10 は GMP 形式の **mul_1 / addmul_1 / submul_1**、
  c13824fc は Karatsuba の再帰。呼び出し元は `.init.text`(c16a6eca …)の initcall。
- ログ領域の文字列に "Now is the time for all good men"、RFC 7539(ChaCha20-Poly1305)の
  IETF テストベクタ本文、"bcdefghiabcabc…"(ハッシュ入力)= **testmgr のテストベクタ群**。
- **正しさは Unicorn で確証済み**: cold.snap を PAE ページング付きで Unicorn にロードし、
  `--noirq` の自前トレースと **264,695 命令バイト一致**(uc_lock.py)。MPI 実装にバグは無い。
- fast-path カバレッジはこの区間で ~97%(ほぼ高速コアで実行)。遅いのは本質的な物量
  (~2M 命令/秒 × 数十億命令)。実機なら約1秒の計算がエミュでは数十分。

### スループット: Release ビルドを使う(重要)
- **Debug 2.15M/s → Release 8.69M/s(約4倍)**。コード変更ゼロ。ブート走行は必ず Release で:
  `dotnet build -c Release` → `./bin/Release/net10.0/Emu86.exe ...`
- fast-path カバレッジは区間により 1.5〜9%(累積)。crypto の MPI は高速コアで回るが物量が支配的。
- crypto は複数アルゴリズムを順に処理して前進中(EIP が c138 MPI 以外に c10e/c102/c151 等へ遷移)。

### 次にやること(選択肢)
1. **待つ**: 正しく前進しているので、放置すれば crypto テストを抜けて次段へ進むはず。
   `burst.snap` で +600M ずつ流して EIP が c130/c138 crypto 領域を抜けるか観測中。
2. **テストを飛ばす(config 派生・推奨)**: カーネル cmdline に `cryptomgr.notests` を渡すと
   自己テストを丸ごとスキップできる。ただし cmdline は GRUB 設定 → ディスク(ext4)書換えが
   必要で、現状 ext4 ライタが無い。注入経路の検討が要る。
3. **高速コアの MPI ホットループ最適化**: imul/adc/movzx の密ループを更に速く。速度のみ。

### 再開用コマンド
```
dotnet build
# 現状確認(どの領域を実行中か):
./bin/Debug/net10.0/Emu86.exe --resume --snapshot cold.snap --notrace --limit <count+300M>
# Unicorn 照合(実CPU オラクル、PAE ページング):
/c/Python314/python <scratchpad>/uc_lock.py cold.snap 3000000 uc.log
```
- crypto を抜けたら次のブロッカー(デバイスドライバ、ルートFS マウント、init 起動)へ。

## マイルストーン: カーネルが root マウント段階まで完走(2026-07-06)

Release ビルド(~8-12M/s)で **12B 命令まで到達**。printk ログを厳密パース(dmesg2.py、
log_buf は phys 0x1a779e4)した結果、カーネルは crypto 自己テスト → 全ドライバ初期化
(serial/agpgart/i8042/rtc/NET/IPv6/microcode/X.509/zswap/AppArmor)を**完走**し、
**VFS root マウントで Kernel panic**:
```
List of all partitions:
No filesystem could mount root, tried:
Kernel panic - not syncing: VFS: Unable to mount root fs on unknown-block(0,0)
  mount_block_root → mount_root → prepare_namespace → kernel_init_freeable
```

### root マウント失敗の二大原因(次のブロッカー)
1. **PCI にデバイスが見えない**: dmesg で `PCI: Probing PCI hardware (bus 00)` の後、
   デバイス列挙ゼロ(`busn_res: [bus 00-ff] end is updated to 00`)。エミュレータは
   ATA PIO ポート(0x1F0-0x1F7,0x3F6、primary のみ、`env.Ata`)は実装済みだが、
   **PCI コンフィグ空間(0xCF8/0xCFC)未実装**。Debian の `ata_piix`(libata)は PCI 上の
   PIIX IDE(8086:7010)を要求するため bind せず、ATA ポートを一切叩かない → ディスク未検出。
2. **initramfs 未ロード**: dmesg に `Unpacking initramfs` が無く `mount_block_root` 直行。
   `root=UUID=350f9256-...` は initramfs(blkid/udev)でしか解決できない。GRUB が initrd を
   ロードしていない(emulator の GRUB が initrd コマンド未対応の可能性)。

### 次の一手(ストレージ・ブリングアップ)
- **Stage 1**: PCI config type-1(0xCF8/0xCFC)を実装し、最小デバイスツリーを露出:
  440FX host bridge(8086:1237)/ PIIX3 ISA(8086:7000)/ **PIIX3 IDE(8086:7010, class 0101,
  prog-if=legacy)**。ata_piix が bind → 既存 ATA ポート(0x1F0/0x170)を探索 → `/dev/sda` 出現。
  注意: ata_piix は BMDMA(BAR4)を仮定するので、PIO フォールバックか BM レジスタ最小実装が要る。
- **Stage 2**: root 解決。initramfs をロードさせるか、emulator に cmdline パッチ機能を足して
  `root=UUID=...` → `root=/dev/sdaN`(要パーティション確認)に書換える(cmdline は phys 0x9e3a0 付近)。
- 検証: dmesg2.py で `ata_piix`/`scsi host`/`ata1.00: ATA-...`/`sda: sda1 sda2` を確認。

## ストレージ Stage 1 実装済み(2026-07-06)

Linux にディスクを見せるため以下を実装(未コミット):
- `Pci.cs`(新規): PCI コンフィグ機構 #1(0xCF8/0xCFC)。bus0 に i440FX(8086:1237)/
  PIIX3 ISA(8086:7000, multifunction)/ **PIIX3 IDE(8086:7010, prog-if=0x80, BAR全0=BMDMA無→libata PIO)**。
- `Environment.cs`: 0xCF8-0xCFF を EnvInPortN/EnvOutPortN で処理。セカンダリ IDE(0x170-0x177/0x376)は 0xFF。
- **BIOS 上端エイリアス(重要修正)**: 実機は BIOS ROM を 4GB 上端にもマップ。SeaBIOS の 32bit flat は
  自身を高位エイリアス(0xffff1edd = 低位 0xf1edd)で call するため未マップだと暴走。`EnvAlias(addr)` で
  物理アクセス時 `addr>=0xFFF00000 ? addr-0xFFF00000` と先頭1MBへ折り返す。PCI 無しの旧経路は未通過で露見せず。
- **WBINVD/INVD(0F 09/08)を NOP 追加**(SeaBIOS PCI init が使用)。`CacheInvd_0F08_09`。
- **ATA IRQ14 配送**: `AtaDevice.IrqPending`(コマンド完了/データ準備で立て、0x1F7 読みで降ろす)を
  ランナーが IRQ14(vec=PicSlaveBase+6)として配送。カスケード(マスタ入力2)も in-service。
  `PicMasterBase>=0x20` でゲート(SeaBIOS 除外)。
- **libata 互換コマンド**: SET FEATURES/FLUSH/READ VERIFY/INIT DEV PARAMS/READ・WRITE EXT/MULTIPLE を
  成功・単一扱い。DMA は abort → libata は PIO へ後退。

回帰の教訓: 修正前は冷起動が命令 2732 で `0008:ffff1edd`(BIOS 高位エイリアス未マップ)へ暴走。
BIOS エイリアス + WBINVD で 150M 無停止、GRUB→カーネル展開(~3B)まで到達。完全冷起動で `/dev/sda`
検出を検証中(boot2.snap)。**注意**: 完全起動が AVHDX を排他ロックするため fast/slow パリティは起動完了後に。

## 決定的発見: ドライバは全てモジュール → initramfs 必須、RAM 32MB では不足(2026-07-06)

PCI 実装後も root マウントは同じ panic(空の "tried:" リスト)。dmesg に `ata_piix`/`ata1`/`sda` が皆無。
**boot2.snap のカーネルRAM像を文字列検索**した結果が決定打:
- `ata_piix` / `libata` / `scsi_mod` / `sd_mod` / `ext4` の文字列が **1個も無い** → **全てモジュール**(組み込みでない)。
- よってこれらは **initramfs からロードするしかない**。initramfs 無しではストレージドライバも ext4 も無く、
  空の "tried:" で panic(完全に符合)。
- しかも dmesg に「Unpacking initramfs」の実出力が無い = **initramfs が全くロードされていない**。
- 原因は **RAM 32MB が小さすぎる**こと。`Memory: 20568K available`(空き ~20MB)に対し Debian の
  initrd.img は ~25-30MB。GRUB が initrd をロードできず(容量不足)、カーネルだけ起動 → panic。

### 対応: RAM を 256MB へ増設
- `Environment.cs` の `RamSize` を `32*1024*1024` → `256*1024*1024` に変更(1箇所)。
- CMOS メモリ報告(0x34/0x35 の 64KB 単位)は RamSize から自動計算されるので SeaBIOS/e820 へ自動反映。
  ext64 = (256-16)MB/64KB = 3840(16bit に収まる)。BIOS 上端(0xfffc0000)とも非重複。
- スナップショットは 256MB になり旧 32MB スナップは非互換(冷起動前提なので可)。dmesg の log_buf は
  phys 0x1a779e4(32MB 内)のままなので dmesg2.py 系はそのまま使える。
- 期待: GRUB が initrd をロード → カーネルが initramfs 展開 → ata_piix/ext4 等をロード →
  root=UUID を blkid/udev で解決 → mount → switch_root → userspace(/sbin/init)。
- 検証: dmesg で「Unpacking initramfs」「ata_piix」「ata1.00: ATA」「sda: sda1..」「EXT4-fs」
  「VFS: Mounted root」「Run /sbin/init」を確認(boot256.snap、limit 20B、~30分)。

### 中断時点の状態(2026-07-06、ここから再開)
- コード変更は**ビルド成功済み・未コミット→本コミットに含める**: `Pci.cs`(新規)、`Environment.cs`
  (PCI 配線・BIOS 上端エイリアス `EnvAlias`・RamSize=256MB・`env.Pci`)、`Disk.cs`(ATA IRQ/追加コマンド)、
  `Program.cs`(`CacheInvd_0F08_09`)、`Program.OpcodeTable.cs`(0F 08/09)、`Program.Runner.cs`(IRQ14 配送)。
- 256MB 冷起動を実行し **2.3B(GRUB がカーネルをロード中、cs=0010)まで到達して中断**。
  まだ kernel の dmesg 出力前(log_buf 未初期化、records=0)なので **initramfs ロードの成否は未確認**。
- **再開手順**:
  ```
  dotnet build -c Release
  ./bin/Release/net10.0/Emu86.exe --snapshot boot256.snap --notrace --limit 20000000000  # 冷起動(~30分)
  # 途中で dmesg 確認(別プロセスは AVHDX を排他ロックするので起動完了/停止後に):
  /c/Python314/python <scratchpad>/dmesg2.py boot256.snap   # log_buf phys 0x1a779e4
  ```
- **次に見るべき分岐**:
  1. 「Unpacking initramfs」が出れば → GRUB が initrd ロード成功。ata_piix/ext4 がロードされ root マウントへ。
     その先の新ブロッカー(ATA の多セクタ PIO 割り込み挙動、ext4 read、userspace)を追う。
  2. 出なければ → GRUB がまだ initrd をロードできていない。GRUB の initrd コマンド動作/メモリ配置を調査
     (RAM 不足以外の原因: emulator の GRUB 経路、boot_params.ramdisk 設定など)。必要なら emulator 側で
     initrd 注入(ただし initrd.img は ext4 上にあり、raw セクタからの取り出しは ext4 パーサが要る)。
- 未実施の回帰: **fast/slow パリティ**(AVHDX ロックのため起動と同時不可)。起動プロセス停止後に
  40M 程度で `--slow` と比較(PCI init 区間まで含める)。fast コアは無改変だが port I/O 共有ロジックを変更したため要確認。

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
