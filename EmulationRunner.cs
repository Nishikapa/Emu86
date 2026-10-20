using static Emu86.Ext;

namespace Emu86;

internal static class EmulationRunner
{
    const long IrqPeriod = 10_000;
    const long SnapshotInterval = 100_000_000;

    public static long Run(RunOptions options, EmuEnvironment env, CPU cpu, long count = 0,
        TextWriter trace = null, TextWriter output = null, TextWriter error = null,
        Action<long, CPU, EmuEnvironment> saveSnapshot = null, long snapshotInterval = SnapshotInterval,
        CancellationToken stop = default, EmulationControl control = null, BootAutomation bootAutomation = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapshotInterval);
        output ??= Console.Out;
        error ??= Console.Error;
        EnvSyncPaging(env, cpu);
        var diagnostics = new RunDiagnostics(options.Diagnostics, env, cpu, output, trace, count);
        var executor = new InstructionExecutor(useFast: !options.Slow);
        var swatch = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = count;
        long fastCount = 0;
        if (env.NextIrq == 0) env.NextIrq = count + IrqPeriod;
        long nextSnapshot = count + snapshotInterval;
        long nextService = count + IrqPeriod;
        while (count < options.Limit)
        {
            // ホストから注入されたスキャンコードを 8042 の出力バッファへ移し、表示ウィンドウが
            // 閉じられたなどの停止要求もタイマ周期で確認する(命令ごとに見るほどの精度は要らない)。
            if (count >= nextService)
            {
                nextService = count + IrqPeriod;
                env.Keyboard.Pump();
                env.Com1.Pump();
                env.Com2.Pump();
                bootAutomation?.Step(env, count, output);
                control?.WaitIfPaused(cpu, count, stop);
                if (stop.IsCancellationRequested)
                {
                    output.WriteLine($"stopped by request at {cpu.cs:x4}:{cpu.eip:x8} (instr {count})");
                    break;
                }
                // システムリセット(8042 の 0xFE/出力ポート、ACPI/PIIX4 の 0xCF9)。呼び出し側が環境を作り直して
                // リセットベクタから再開する。直前の状態は保存しない(再開してもリセット待ちのまま止まるだけなので)。
                if (env.ResetRequested || env.Keyboard.ResetRequested)
                {
                    env.ResetRequested = true;
                    output.WriteLine($"[system] reset requested at {cpu.cs:x4}:{cpu.eip:x8} (instr {count})");
                    diagnostics.ReportStop(count);
                    control?.Stopped(cpu, count);
                    return count;
                }
            }

            // 仮想タイマ割り込み(IRQ0): 一定命令数ごとに要求を立て、配送できる状態(IF=1 など)になるまで
            // 保留する。実機の 8259 と同じく要求は消えないので、IF=1 の区間が短いポーリングループ
            // (SeaBIOS の sti; nop; cli)でもティックが進む。リアルモードは IVT 経由で SeaBIOS の handle_08 が
            // BDA ティックを進め、プロテクトモードは IDT 経由で Linux の jiffies が進む。
            if (!options.NoIrq)
            {
                if (count >= env.NextIrq)
                {
                    env.NextIrq = count + IrqPeriod;
                    env.TimerIrqPending = true;
                }
                // ハードウェア割り込み(タイマ・キーボード・シリアル・マウス・ATA)の配送。IF・PIC のマスク・
                // in-service、プロテクトモードでは IDT の準備を確認し、優先順で 1 本だけ配送する。
                var line = IrqDelivery.Select(env, cpu);
                if (line >= 0)
                {
                    try
                    {
                        var irq = executor.Execute(env, cpu, IrqDelivery.Pending(line));
                        if (irq.IsSuccess) cpu = irq.cpu;
                    }
                    catch (PageFaultException pf)
                    {
                        output.WriteLine($"FAULT during {IrqDelivery.Name(line)} IRQ delivery: lin={pf.Linear:x8} err={pf.ErrorCode:x} at {cpu.cs:x4}:{cpu.eip:x8} esp={cpu.esp:x8} (instr {count})");
                        break;
                    }
                }
            }

            var beforeCs = cpu.cs;
            var beforeEip = cpu.eip;
            if (diagnostics.BeforeInstruction(count)) break;
            var instructionCompleted = true;
            try
            {
                // まず高速コアで 1 命令実行し、未対応の命令だけモナド版へフォールバックする。
                var result = executor.Step(env, cpu);
                if (executor.UsedFastPath) fastCount++;
                if (!result.IsSuccess && VgaBiosStub.TryService(env, cpu))
                {
                    // VGA BIOS スタブ(C000:0060 の UD2)のサービス呼び出し。C# 側で処理済み。
                }
                else if (!result.IsSuccess)
                {
                    // 未定義/未対応命令。プロテクトモードで IDT が整っていれば #UD(ベクタ 6)として
                    // 命令開始時の状態から配送する(Virtual PC のハイパーコール命令 0F C7 C8 のように、
                    // OS 側が例外を前提に実行する不正命令がある)。診断のため先頭数十回はログに残す。
                    if (!cpu.pe || cpu.idt_limit < 6 * 8 + 7)
                        break;
                    diagnostics.UndefinedInstruction(count);
                    var ud = executor.Execute(env, cpu, Interrupt(6));
                    if (!ud.IsSuccess) { output.WriteLine("#UD delivery failed"); break; }
                    cpu = ud.cpu;
                }
                else
                    cpu = result.cpu;
            }
            catch (PageFaultException pf)
            {
                // 命令前状態へ巻き戻してから #PF を IDT 経由で配送する。
                // IDT が未整備(プロテクトモードでない/limit 不足)なら停止する。
                if (diagnostics.PageFault(pf, count)) break;
                if (!cpu.pe || cpu.idt_limit < 14 * 8 + 7)
                {
                    output.WriteLine($"EXCEPTION at {cpu.cs:x4}:{cpu.eip:x8}: {pf.Message}");
                    break;
                }
                try
                {
                    var pfr = executor.Execute(env, cpu, PageFault(pf.Linear, pf.ErrorCode));
                    if (!pfr.IsSuccess) { output.WriteLine($"#PF delivery failed at {cpu.cs:x4}:{cpu.eip:x8}"); break; }
                    cpu = pfr.cpu;
                }
                catch (PageFaultException pf2)
                {
                    // 配送中の再フォルト = ダブルフォルト相当。診断を出して停止する。
                    output.WriteLine($"DOUBLE FAULT: #PF delivery faulted lin={pf2.Linear:x8} err={pf2.ErrorCode:x}");
                    output.WriteLine($"  original #PF: lin={pf.Linear:x8} err={pf.ErrorCode:x} at {cpu.cs:x4}:{cpu.eip:x8} esp={cpu.esp:x8} (instr {count})");
                    break;
                }
                instructionCompleted = false;
            }
            catch (Exception ex)
            {
                output.WriteLine($"EXCEPTION at {cpu.cs:x4}:{cpu.eip:x8}: {ex.GetType().Name}: {ex.Message}");
                break;
            }
            count++;
            env.Tsc = (ulong)count; // RDTSC の代用カウンタ
            if (instructionCompleted && diagnostics.AfterInstruction(beforeCs, beforeEip, count)) break;
            if (swatch.ElapsedMilliseconds >= 5000)
            {
                var rate = (count - lastReport) * 1000.0 / swatch.ElapsedMilliseconds;
                error.WriteLine($"[progress] {count:N0} instr, {rate / 1e6:F2}M/s, cs={cpu.cs:x4} eip={cpu.eip:x8}");
                lastReport = count;
                swatch.Restart();
            }
            if (count >= nextSnapshot)
            {
                saveSnapshot?.Invoke(count, cpu, env);
                nextSnapshot = count + snapshotInterval;
            }
        }

        if (count >= options.Limit) output.WriteLine("instruction limit reached");
        saveSnapshot?.Invoke(count, cpu, env);
        if (!options.Slow && count > 0)
            output.WriteLine($"fast-path coverage: {fastCount:N0}/{count:N0} ({100.0 * fastCount / count:F2}%)");
        diagnostics.ReportStop(count);
        control?.Stopped(cpu, count);
        return count;
    }
}
