# 用語集(Emu86 ドメインモデル)

ADR やコードコメントで使う語の定義。「現状」は 2026-10-12 時点のコード。

| 用語 | 定義 | 現状のコード上の対応 |
|------|------|---------------------|
| **vCPU(仮想 CPU)** | ゲストから見える 1 個のプロセッサ。アーキテクチャ状態(汎用/セグメント/制御レジスタ、EFLAGS、FPU)を持つ | `CPU` クラス。1 個固定 |
| **BSP / AP** | Bootstrap Processor(リセットベクタから起動する CPU)/ Application Processor(INIT-SIPI で起こされる CPU) | BSP のみ存在 |
| **CPU 固有状態** | 実機では CPU ごとに独立している状態: ページング状態、TLB、TSC、MSR、DR0-7、Local APIC | TSC/MSR/DR/TLB/ページング状態は `EmuEnvironment` にある(要移動) |
| **環境(EmuEnvironment)** | 物理メモリ、I/O ポート、デバイス群、ディスクなど、全 vCPU が共有するもの | `EmuEnvironment` |
| **実行ループ** | vCPU を進め、割り込みを配送し、デバイスをポンプし、停止条件を見る中枢 | `EmulationRunner.Run` |
| **量子(クォンタム)** | 時分割方式で 1 つの vCPU を連続実行する命令数 | 未定義(候補: `IrqPeriod` = 1 万命令) |
| **ゲスト時刻** | ゲストが観測する時間。TSC、PIT、ACPI PM タイマ、IRQ0 周期の基準 | 実行済み命令数(100 万命令 = 1 秒) |
| **ホスト時刻** | 実時間(Stopwatch)。進捗表示のみに使用 | `swatch` |
| **PIC** | 8259 割り込みコントローラ(マスタ/スレーブ)。単一 CPU 向け | `env.PicMaster*`/`PicSlave*` |
| **Local APIC(LAPIC)** | CPU ごとの割り込みコントローラ。IPI 送信(ICR)、ローカルタイマ、EOI。MMIO 0xFEE00000 | 未実装 |
| **I/O APIC** | 共有の割り込みルータ。外部 IRQ を任意の CPU/ベクタへ配送。MMIO 0xFEC00000 | 未実装 |
| **IPI** | Inter-Processor Interrupt。LAPIC ICR 経由で他 CPU へ送る割り込み(INIT、SIPI、TLB シュートダウン等) | 未実装 |
| **INIT-SIPI** | AP を起動する手順。INIT でリセット状態にし、SIPI(Startup IPI)でベクタ VV を渡し `VV00:0000` から実行開始 | 未実装 |
| **MADT / MP テーブル** | ACPI の Multiple APIC Description Table / Intel MP Specification テーブル。OS が CPU 数と APIC を知る手段 | SeaBIOS が生成(現在は CPU 1 個) |
| **MMIO** | メモリ空間に置かれたデバイスレジスタ。LAPIC/IOAPIC が必要とする | 未実装(RAM 外は 0xFF/無視) |
| **LOCK プレフィックス / アトミック RMW** | `LOCK` 付き命令、XCHG(メモリ)、CMPXCHG、XADD、CMPXCHG8B は他 CPU から分断されない読み書きを保証する | 無視・非アトミック |
| **分断(トーン)書き込み** | 複数バイトの store が途中で他スレッドから観測されること | 32 ビット store は 4 回の byte store |
| **HLT** | 割り込みが来るまで CPU を止める命令。SMP のアイドルループが前提とする | NOP |
| **決定論** | 同じ入力(スナップショット + 注入イベント)から同じ命令列が再現されること。Unicorn 照合と再現デバッグの前提 | 成立(単一スレッド) |
| **時分割 SMP** | 1 ホストスレッドで n vCPU を量子ごとに切り替える方式。決定論を保つ | 選択肢 A |
| **並列 SMP** | vCPU ごとに 1 ホストスレッド。決定論を失う | 選択肢 B |
| **HAL** | Windows の Hardware Abstraction Layer。halacpi.dll(ACPI 単一 CPU、PIC)/ halaacpi.dll(ACPI 単一 CPU、APIC)/ halmacpi.dll(ACPI マルチプロセッサ、APIC) | XP イメージは halacpi.dll |
| **IR / モナド版（通常コア）** | 同一意味論を持つ 2 つの命令実行器。IR（`Semantics`）は翻訳キャッシュが解釈・コンパイルし、IR の対象外の命令はモナド版（`Instructions`）で実行する。かつての手書きの高速コアは ADR-0003 で IR に吸収して削除 | `TranslationCache` / `Instructions.Execute2` |
| **スナップショット** | CPU・RAM・デバイス・ディスク差分の保存形式。現在 v10 | `SnapshotStore` |
