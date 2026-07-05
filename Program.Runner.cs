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
        var step = Execute2;
        var timerIrq = Interrupt(8);
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
                // 仮想タイマ割り込み(IRQ0): 一定命令数ごとに、リアルモードかつ IF=1 のとき
                // INT 08h を注入する。SeaBIOS の handle_08 が BDA のティックカウンタを進める。
                if (count >= nextIrq && !cpu.pe && cpu.jf)
                {
                    nextIrq = count + IrqPeriod;
                    var irq = timerIrq(env, cpu, default);
                    if (irq.IsSuccess)
                        cpu = irq.cpu;
                }

                var beforeCs = cpu.cs;
                var beforeEip = cpu.eip;
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
                catch (Exception ex)
                {
                    WriteLine($"EXCEPTION at {cpu.cs:x4}:{cpu.eip:x8}: {ex.GetType().Name}: {ex.Message}");
                    break;
                }
                count++;
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
        WriteLine($"STOP at {cpu.cs:x4}:{cpu.eip:x8} after {count} instructions, opcode: " +
            string.Join(" ", env.OneMegaMemory_.Skip((int)addr).Take(6).Select(b => b.ToString("x2"))));
    }
}
