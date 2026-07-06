using static Emu86.CPU;
using static Emu86.Ext;
using static System.Console;

namespace Emu86;

static partial class Program
{
    // 仮想タイマ割り込み(IRQ0)の注入間隔(命令数)。
    const long IrqPeriod = 10_000;

    // 実行する命令数の上限。ブートローダーの展開処理などは1命令ずつの
    // インタプリタ実行では数億命令規模になりうるため、余裕を持たせている。
    const long InstructionLimit = 500_000_000;

    static void Main(string[] args)
    {
        // デバッグ用: --dumplba <開始LBA> <セクタ数> で、エミュレータと同じディスクスタック
        // (ベース + 差分オーバーレイ)からセクタを読み出して lba_dump.bin に書き出す。
        var dumpIdx = Array.IndexOf(args, "--dumplba");
        if (dumpIdx >= 0)
        {
            var start = long.Parse(args[dumpIdx + 1]);
            var cnt = int.Parse(args[dumpIdx + 2]);
            var disk = new DiskImage("sample.avhdx", writable: false);
            var buf = new byte[512];
            using var ofs = new FileStream("lba_dump.bin", FileMode.Create, FileAccess.Write);
            for (var i = 0L; i < cnt; i++)
            {
                disk.ReadSector(start + i, buf, 0);
                ofs.Write(buf);
            }
            WriteLine($"dumped LBA {start}..{start + cnt - 1} to lba_dump.bin");
            return;
        }

        // --snapshot <path> … スナップショットの保存/再開ファイルを指定(既定 snapshot.bin)
        var snapIdx = Array.IndexOf(args, "--snapshot");
        if (snapIdx >= 0 && snapIdx + 1 < args.Length)
            SnapshotPath = args[snapIdx + 1];

        var env = new EmuEnvironment();

        long count;
        CPU cpu;
        if (args.Contains("--resume"))
        {
            (count, cpu) = LoadSnapshot(env);
            WriteLine($"[snapshot] resumed from {SnapshotPath} at instruction {count}");
        }
        else
        {
            var init_cpu1 = new CPU();
            var init_cpu2 = _ip.setter(init_cpu1)(0xFFF0);
            cpu = _cs.setter(init_cpu2)(0xF000);
            count = 0;
        }
        // CR0.PG/CR3 のミラーを初期化(スナップショット復元後は特に必須)。
        EnvSyncPaging(env, cpu);

        // 実行トレースの粒度:
        //   既定       … 分岐(制御転送)が起きたときだけ CS:EIP を記録(命令ごとより桁違いに軽い)
        //   --trace-all … 全命令を記録(詳細デバッグ用、重い)
        //   --notrace  … 記録しない(最速。重い計算フェーズを通過させたいとき)
        // --limit N   … N 命令で停止する(回帰比較などで決定的に打ち切るため)
        // --slow      … 高速コア(FastStep)を使わず全命令をモナド版で実行する(回帰比較の基準用)
        var traceAll = args.Contains("--trace-all");
        var trace = traceAll || !args.Contains("--notrace");
        var slow = args.Contains("--slow");
        var limit = InstructionLimit;
        var limitIdx = Array.IndexOf(args, "--limit");
        if (limitIdx >= 0 && limitIdx + 1 < args.Length)
            limit = long.Parse(args[limitIdx + 1]);
        var swatch = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = count;
        long fastCount = 0;
        var faultSave = new CPU(); // ページフォルト巻き戻し用のレジスタ退避先
        // 直近の分岐履歴(制御転送のみ)のリングバッファ。STOP 時に原因追跡用に出力する。
        // --brhist 指定時のみ記録する(通常実行のオーバーヘッドを避ける)。
        var brHist = args.Contains("--brhist");
        var espTrap = args.Contains("--esptrap");
        uint beforeEsp = 0;
        var noirq = args.Contains("--noirq");
        var pfTrap = args.Contains("--pftrap");
        uint breakEip = 0;
        var beIdx = Array.IndexOf(args, "--breakeip");
        if (beIdx >= 0 && beIdx + 1 < args.Length) breakEip = Convert.ToUInt32(args[beIdx + 1], 16);
        uint breakEax = 0; bool hasBreakEax = false;
        var bxIdx = Array.IndexOf(args, "--breakeax");
        if (bxIdx >= 0 && bxIdx + 1 < args.Length) { breakEax = Convert.ToUInt32(args[bxIdx + 1], 16); hasBreakEax = true; }
        if (args.Contains("--intlog")) env.IntLog = [];
        var wlogIdx = Array.IndexOf(args, "--wlog");
        if (wlogIdx >= 0 && wlogIdx + 2 < args.Length)
        {
            env.WLogLo = Convert.ToUInt32(args[wlogIdx + 1], 16);
            env.WLogHi = Convert.ToUInt32(args[wlogIdx + 2], 16);
            env.WriteLog = [];
        }
        // --watch <physLo> <physHi>: 物理アドレス範囲への書き込みで停止し、犯人命令を出力する。
        var watchIdx = Array.IndexOf(args, "--watch");
        if (watchIdx >= 0 && watchIdx + 2 < args.Length)
        {
            env.WatchLo = Convert.ToUInt32(args[watchIdx + 1], 16);
            env.WatchHi = Convert.ToUInt32(args[watchIdx + 2], 16);
        }
        const int BrN = 64;
        var brFrom = new (ushort cs, uint eip)[BrN];
        var brTo = new (ushort cs, uint eip)[BrN];
        var brIdx = 0;
        var step = Execute2;
        long nextIrq = count + IrqPeriod;
        long nextSnapshot = count + SnapshotInterval;
        // 大きめのバッファでトレースの I/O 負荷を抑える。
        StreamWriter sw = trace
            ? new StreamWriter(new FileStream("trace.log", count > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
            : null;
        try
        {
            if (trace && count > 0)
                sw.WriteLine($"\n--- resumed at instruction {count} ---");
            while (true)
            {
                // 仮想タイマ割り込み(IRQ0): 一定命令数ごとに、IF=1 かつ PIC でマスクされて
                // いなければ、PIC のベクタベース+0 へ配送する(リアルモードは IVT 経由で
                // SeaBIOS の handle_08 が BDA ティックを進め、プロテクトモードは IDT 経由で
                // Linux の jiffies が進む)。IDT 未設定(limit 不足)の間は配送しない。
                if (!noirq && count >= nextIrq)
                {
                    nextIrq = count + IrqPeriod;
                    var vec = env.PicMasterBase + 0;
                    // 配送条件: IF=1、IRQ0 が PIC でマスクされておらず、かつ前回の IRQ0 が
                    // まだ処理中(in-service)でないこと。in-service は実機 8259 と同じく EOI で降りる。
                    // これがないとハンドラ完了前に次を注入して多重ネストし、SAVE_ALL の push が
                    // スタックを .text へ押し下げてコードを破壊する(割り込みストーム)。
                    // プロテクトモードでは OS が PIC を再プログラム(base>=0x20)し IDT ゲートが
                    // 用意されるまで待つ。
                    var deliverable = cpu.jf && (env.PicMasterMask & 1) == 0 && (env.PicMasterIsr & 1) == 0 &&
                        (cpu.pe
                            ? env.PicMasterBase >= 0x20 && cpu.idt_limit >= (vec + 1) * 8 - 1
                            : true);
                    if (deliverable)
                    {
                        try
                        {
                            var irq = Interrupt(vec)(env, cpu, default);
                            if (irq.IsSuccess)
                            {
                                cpu = irq.cpu;
                                env.PicMasterIsr |= 1; // IRQ0 を in-service にする(EOI まで再配送しない)
                            }
                        }
                        catch (PageFaultException pf)
                        {
                            WriteLine($"FAULT during IRQ delivery: lin={pf.Linear:x8} err={pf.ErrorCode:x} at {cpu.cs:x4}:{cpu.eip:x8} esp={cpu.esp:x8} (instr {count})");
                            break;
                        }
                    }
                }

                // ATA(IRQ14, スレーブ PIC 入力6)の配送。IDE ディスクがコマンド完了/データ準備で
                // INTRQ を上げたら、プロテクトモードで IF=1・非マスク・非 in-service のとき配送する。
                // レガシー IDE の IRQ14 はスレーブ経由なので、カスケード(マスタ IRQ2)の
                // in-service も同時に立てる。EOI(0xA0/0x20)で両方降りる。
                if (!noirq && env.Ata is { IrqPending: true } && cpu.pe && cpu.jf
                    && env.PicMasterBase >= 0x20  // OS が PIC を保護モードベースへ再プログラム済み(SeaBIOS は除外)
                    && (env.PicSlaveMask & 0x40) == 0 && (env.PicMasterMask & 0x04) == 0
                    && (env.PicSlaveIsr & 0x40) == 0)
                {
                    var vec = env.PicSlaveBase + 6;
                    if (cpu.idt_limit >= (vec + 1) * 8 - 1)
                    {
                        try
                        {
                            var irq = Interrupt(vec)(env, cpu, default);
                            if (irq.IsSuccess)
                            {
                                cpu = irq.cpu;
                                env.Ata.IrqPending = false;
                                env.PicSlaveIsr |= 0x40;  // スレーブ入力6 in-service
                                env.PicMasterIsr |= 0x04; // カスケード(マスタ入力2)in-service
                            }
                        }
                        catch (PageFaultException pf)
                        {
                            WriteLine($"FAULT during ATA IRQ delivery: lin={pf.Linear:x8} err={pf.ErrorCode:x} at {cpu.cs:x4}:{cpu.eip:x8} (instr {count})");
                            break;
                        }
                    }
                }

                var beforeCs = cpu.cs;
                var beforeEip = cpu.eip;
                env.CurEip = cpu.eip; // 書き込みログの帰属用
                if (breakEip != 0 && cpu.eip == breakEip && (!hasBreakEax || cpu.eax == breakEax))
                {
                    WriteLine($"BREAKEIP {cpu.eip:x8}: eax={cpu.eax:x8} ecx={cpu.ecx:x8} edx={cpu.edx:x8} ebx={cpu.ebx:x8} esi={cpu.esi:x8} edi={cpu.edi:x8} ebp={cpu.ebp:x8} esp={cpu.esp:x8}");
                    var sb2 = cpu.ss_base;
                    for (var k = 0u; k < 0x40; k += 4)
                    {
                        var a = sb2 + cpu.esp + k;
                        var v = (uint)(EnvGetMemoryData8(env, a) | (EnvGetMemoryData8(env, a + 1) << 8)
                            | (EnvGetMemoryData8(env, a + 2) << 16) | (EnvGetMemoryData8(env, a + 3) << 24));
                        WriteLine($"  [esp+{k:x2}] = {v:x8}");
                    }
                    break;
                }
                // ページング有効時は、命令の途中でページフォルトが起きうるため
                // 命令前のレジスタ状態を退避しておき、#PF 配送時に巻き戻す。
                if (env.PagingOn) cpu.CopyTo(faultSave);
                try
                {
                    // まず高速コアで 1 命令実行し、未対応の命令だけモナド版へフォールバックする。
                    if (!slow && FastStep(env, cpu))
                    {
                        fastCount++;
                    }
                    else
                    {
                        var r = step(env, cpu, default);
                        if (!r.IsSuccess)
                            break;
                        cpu = r.cpu;
                    }
                }
                catch (PageFaultException pf)
                {
                    // 命令前状態へ巻き戻してから #PF を IDT 経由で配送する。
                    // IDT が未整備(プロテクトモードでない/limit 不足)なら停止する。
                    faultSave.CopyTo(cpu);
                    if (!cpu.pe || cpu.idt_limit < 14 * 8 + 7)
                    {
                        WriteLine($"EXCEPTION at {cpu.cs:x4}:{cpu.eip:x8}: {pf.Message}");
                        break;
                    }
                    // デバッグ: 最初の保護違反(WP)#PF で停止し、原因を調べられるようにする。
                    if (pfTrap && (pf.ErrorCode & 1) != 0)
                    {
                        WriteLine($"PFTRAP: protection #PF lin={pf.Linear:x8} err={pf.ErrorCode:x} at {cpu.cs:x4}:{cpu.eip:x8} (instr {count})");
                        if (env.WriteLog != null)
                        {
                            WriteLine($"--- writes to [{env.WLogLo:x8},{env.WLogHi:x8}) (last 30 of {env.WriteLog.Count}) ---");
                            foreach (var e in env.WriteLog.TakeLast(30)) WriteLine("  " + e);
                        }
                        break;
                    }
                    try
                    {
                        var pfr = PageFault(pf.Linear, pf.ErrorCode)(env, cpu, default);
                        if (!pfr.IsSuccess) { WriteLine($"#PF delivery failed at {cpu.cs:x4}:{cpu.eip:x8}"); break; }
                        cpu = pfr.cpu;
                    }
                    catch (PageFaultException pf2)
                    {
                        // 配送中の再フォルト = ダブルフォルト相当。診断を出して停止する。
                        WriteLine($"DOUBLE FAULT: #PF delivery faulted lin={pf2.Linear:x8} err={pf2.ErrorCode:x}");
                        WriteLine($"  original #PF: lin={pf.Linear:x8} err={pf.ErrorCode:x} at {cpu.cs:x4}:{cpu.eip:x8} esp={cpu.esp:x8} (instr {count})");
                        break;
                    }
                    count++;
                    env.Tsc = (ulong)count;
                    continue;
                }
                catch (Exception ex)
                {
                    WriteLine($"EXCEPTION at {cpu.cs:x4}:{cpu.eip:x8}: {ex.GetType().Name}: {ex.Message}");
                    break;
                }
                count++;
                env.Tsc = (ulong)count; // RDTSC の代用カウンタ
                if (env.WatchTriggered)
                {
                    WriteLine($"WATCH: write into [{env.WatchLo:x8},{env.WatchHi:x8}) by instruction at {beforeCs:x4}:{beforeEip:x8} (instr {count})");
                    env.WatchTriggered = false;
                    break;
                }
                // ESP がカーネルのエントリコード領域を指す不正値になった瞬間を捕捉する。
                if (espTrap && cpu.esp is >= 0xc16a8000 and < 0xc16aa000 && !(beforeEsp is >= 0xc16a8000 and < 0xc16aa000))
                {
                    WriteLine($"ESPTRAP: esp={cpu.esp:x8} set by {beforeCs:x4}:{beforeEip:x8} (instr {count})");
                    break;
                }
                beforeEsp = cpu.esp;
                if (brHist)
                {
                    // 制御転送(順次進行を外れた)命令を記録する。
                    var seq2 = cpu.cs == beforeCs && cpu.eip > beforeEip && cpu.eip <= beforeEip + 15;
                    if (!seq2)
                    {
                        brFrom[brIdx] = (beforeCs, beforeEip);
                        brTo[brIdx] = (cpu.cs, cpu.eip);
                        brIdx = (brIdx + 1) % BrN;
                    }
                    // プロテクトモードで CS が NULL セレクタになった瞬間を捕捉する。
                    if (cpu.pe && cpu.cs == 0 && beforeCs != 0)
                    {
                        WriteLine($"CSTRAP: cs=0 loaded by instruction at {beforeCs:x4}:{beforeEip:x8} -> eip={cpu.eip:x8} (instr {count})");
                        break;
                    }
                }
                if (trace)
                {
                    // 制御転送(分岐/ジャンプ/CALL/RET/割り込み)が起きた命令だけ記録する。
                    // 順次進行なら EIP は開始位置 +1〜+15。それを外れた/CS が変わった = 分岐。
                    var seq = cpu.cs == beforeCs && cpu.eip > beforeEip && cpu.eip <= beforeEip + 15;
                    if (traceAll || !seq)
                        sw.WriteLine($"{cpu.cs:x4}:{cpu.eip:x8}");
                }
                if (swatch.ElapsedMilliseconds >= 5000)
                {
                    var rate = (count - lastReport) * 1000.0 / swatch.ElapsedMilliseconds;
                    Error.WriteLine($"[progress] {count:N0} instr, {rate / 1e6:F2}M/s, cs={cpu.cs:x4} eip={cpu.eip:x8}");
                    lastReport = count;
                    swatch.Restart();
                }
                if (count >= nextSnapshot)
                {
                    SaveSnapshot(count, cpu, env);
                    nextSnapshot = count + SnapshotInterval;
                }
                if (limit <= count)
                {
                    WriteLine("instruction limit reached");
                    break;
                }
            }
        }
        finally
        {
            sw?.Dispose();
        }

        SaveSnapshot(count, cpu, env);

        if (!slow && count > 0)
            WriteLine($"fast-path coverage: {fastCount:N0}/{count:N0} ({100.0 * fastCount / count:F2}%)");
        var addr = GetCodeAddr(cpu).addr;
        string opbytes;
        try
        {
            opbytes = string.Join(" ", Enumerable.Range(0, 6).Select(i => EnvGetMemoryData8(env, addr + (uint)i).ToString("x2")));
        }
        catch (PageFaultException)
        {
            opbytes = "(unmapped)";
        }
        WriteLine($"STOP at {cpu.cs:x4}:{cpu.eip:x8} after {count} instructions, opcode: {opbytes}");
        if (env.IntLog != null)
        {
            WriteLine($"--- protected-mode interrupt deliveries (total {env.IntLog.Count}) ---");
            WriteLine("[first 30]");
            foreach (var e in env.IntLog.Take(30))
                WriteLine("  " + e);
            WriteLine("[last 4]");
            foreach (var e in env.IntLog.TakeLast(4))
                WriteLine("  " + e);
        }
        if (brHist)
        {
            WriteLine("--- last branches (from -> to) ---");
            for (var k = 0; k < BrN; k++)
            {
                var j = (brIdx + k) % BrN;
                if (brTo[j] == default) continue;
                WriteLine($"  {brFrom[j].cs:x4}:{brFrom[j].eip:x8} -> {brTo[j].cs:x4}:{brTo[j].eip:x8}");
            }
        }
    }
}
