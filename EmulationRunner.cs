using static Emu86.Ext;

namespace Emu86;

internal static class EmulationRunner
{
    const long IrqPeriod = 10_000;
    const long SnapshotInterval = 100_000_000;

    public static long Run(RunOptions options, EmuEnvironment env, CPU cpu, long count = 0,
        TextWriter trace = null, TextWriter output = null, TextWriter error = null,
        Action<long, CPU, EmuEnvironment> saveSnapshot = null, long snapshotInterval = SnapshotInterval)
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
        while (count < options.Limit)
        {
            // 仮想タイマ割り込み(IRQ0): 一定命令数ごとに、IF=1 かつ PIC でマスクされて
            // いなければ、PIC のベクタベース+0 へ配送する(リアルモードは IVT 経由で
            // SeaBIOS の handle_08 が BDA ティックを進め、プロテクトモードは IDT 経由で
            // Linux の jiffies が進む)。IDT 未設定(limit 不足)の間は配送しない。
            if (!options.NoIrq && count >= env.NextIrq)
            {
                env.NextIrq = count + IrqPeriod;
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
                        var irq = executor.Execute(env, cpu, Interrupt(vec));
                        if (irq.IsSuccess)
                        {
                            cpu = irq.cpu;
                            env.PicMasterIsr |= 1; // IRQ0 を in-service にする(EOI まで再配送しない)
                        }
                    }
                    catch (PageFaultException pf)
                    {
                        output.WriteLine($"FAULT during IRQ delivery: lin={pf.Linear:x8} err={pf.ErrorCode:x} at {cpu.cs:x4}:{cpu.eip:x8} esp={cpu.esp:x8} (instr {count})");
                        break;
                    }
                }
            }

            // ATA(IRQ14, スレーブ PIC 入力6)の配送。IDE ディスクがコマンド完了/データ準備で
            // INTRQ を上げたら、プロテクトモードで IF=1・非マスク・非 in-service のとき配送する。
            // レガシー IDE の IRQ14 はスレーブ経由なので、カスケード(マスタ IRQ2)の
            // in-service も同時に立てる。EOI(0xA0/0x20)で両方降りる。
            if (!options.NoIrq && env.Ata is { IrqPending: true } && cpu.pe && cpu.jf
                && env.PicMasterBase >= 0x20  // OS が PIC を保護モードベースへ再プログラム済み(SeaBIOS は除外)
                && (env.PicSlaveMask & 0x40) == 0 && (env.PicMasterMask & 0x04) == 0
                && (env.PicSlaveIsr & 0x40) == 0)
            {
                var vec = env.PicSlaveBase + 6;
                if (cpu.idt_limit >= (vec + 1) * 8 - 1)
                {
                    try
                    {
                        var irq = executor.Execute(env, cpu, Interrupt(vec));
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
                        output.WriteLine($"FAULT during ATA IRQ delivery: lin={pf.Linear:x8} err={pf.ErrorCode:x} at {cpu.cs:x4}:{cpu.eip:x8} (instr {count})");
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
                if (!result.IsSuccess)
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
        return count;
    }
}
