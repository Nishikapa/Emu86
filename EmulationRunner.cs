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
        // 命令ごとに CPU を見る診断(全命令トレース・ブレークポイント・監視点)があるときは、ブロックをまとめて実行できない。
        env.Translations.Enabled = options.Translate && !options.Slow && !options.Diagnostics.PerInstruction;
        var swatch = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = count;
        long fastCount = 0;
        long translatedCount = 0;
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
                    catch (CpuFaultException exception)
                    {
                        output.WriteLine($"FAULT during {IrqDelivery.Name(line)} IRQ delivery: {exception.Fault.Describe()} at {cpu.cs:x4}:{cpu.eip:x8} esp={cpu.esp:x8} (instr {count})");
                        break;
                    }
                }
            }

            var beforeCs = cpu.cs;
            if (diagnostics.BeforeInstruction(count)) break;
            // 翻訳ブロックの予算: 次のイベント(タイマ周期・サービス周期・スナップショット・上限)までの命令数。
            // これでブロック実行の境界が 1 命令ずつの実行と一致する。
            if (env.Translations.Enabled)
            {
                var budget = Math.Min((long)TranslationCache.MaxBlock, options.Limit - count);
                if (!options.NoIrq) budget = Math.Min(budget, env.NextIrq - count);
                budget = Math.Min(budget, Math.Min(nextService - count, nextSnapshot - count));
                executor.Budget = (int)Math.Max(1, budget);
            }
            // 命令が完了せずフォルトになった場合はここに入る(#UD は実行器の失敗、#PF/#GP は例外)。
            Fault? fault = null;
            var completed = true;
            try
            {
                // まず高速コアで 1 命令実行し、未対応の命令だけモナド版へフォールバックする。
                var result = executor.Step(env, cpu);
                if (executor.UsedFastPath) fastCount += executor.Executed;
                if (executor.UsedTranslation) translatedCount += executor.Executed;
                if (result.IsSuccess)
                    cpu = result.cpu;
                else
                {
                    completed = false;
                    // VGA BIOS スタブ(C000:0060 の UD2)なら C# 側で処理済み。それ以外の未定義/未対応命令は #UD(ベクタ 6)。
                    // Virtual PC のハイパーコール命令 0F C7 C8 のように、OS 側が例外を前提に実行する不正命令がある。
                    if (!VgaBiosStub.TryService(env, cpu)) fault = Fault.UndefinedOpcode;
                }
            }
            catch (CpuFaultException exception)
            {
                completed = false;
                fault = exception.Fault;
            }
            catch (Exception ex)
            {
                output.WriteLine($"EXCEPTION at {cpu.cs:x4}:{cpu.eip:x8}: {ex.GetType().Name}: {ex.Message}");
                break;
            }
            if (fault is Fault raised)
            {
                // 命令開始時の状態(InstructionExecutor が巻き戻し済み)から IDT 経由で配送する。
                // IDT が未整備(プロテクトモードでない/limit 不足)なら停止する。
                // 翻訳ブロックの途中で起きた場合、完了した命令の分を数えてから診断する。
                count += executor.Executed;
                if (diagnostics.Fault(raised, count)) break;
                if (!raised.IdtReady(cpu))
                {
                    if (raised.Vector != 6) output.WriteLine($"EXCEPTION at {cpu.cs:x4}:{cpu.eip:x8}: {raised.Message}");
                    break;
                }
                try
                {
                    var delivered = executor.Execute(env, cpu, Raise(raised));
                    if (!delivered.IsSuccess) { output.WriteLine($"{raised.Name} delivery failed at {cpu.cs:x4}:{cpu.eip:x8}"); break; }
                    cpu = delivered.cpu;
                }
                catch (CpuFaultException second)
                {
                    // 配送中の再フォルト = ダブルフォルト相当。診断を出して停止する。
                    output.WriteLine($"DOUBLE FAULT: {raised.Name} delivery faulted {second.Fault.Describe()}");
                    output.WriteLine($"  original {raised.Name}: {raised.Describe()} at {cpu.cs:x4}:{cpu.eip:x8} esp={cpu.esp:x8} (instr {count})");
                    break;
                }
            }
            // 完了した命令数(翻訳ブロックは複数。フォルト時は上で加算済み)に、完了しなかった 1 命令(#UD・フォルト・VGA BIOS スタブ)を足す。
            count += (fault == null ? executor.Executed : 0) + (completed ? 0 : 1);
            env.Tsc = (ulong)count; // RDTSC の代用カウンタ
            // #PF の配送は命令完了トレースに含めない(#UD/#GP の配送は従来どおり命令の完了として扱う)。
            // ブロックの途中の命令は順次実行なので、最後に完了した命令だけを見れば分岐トレースは同じになる。
            if (fault?.Vector != 14 && diagnostics.AfterInstruction(beforeCs, executor.LastEip, count)) break;
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
        if (env.Translations.Enabled && count > 0)
            error.WriteLine($"[translation] {env.Translations.Translated:N0} sites lowered, {env.Translations.Compiled:N0} compiled; {translatedCount:N0}/{count:N0} instructions ({100.0 * translatedCount / count:F2}%: {env.Translations.Interpreted:N0} interpreted, {env.Translations.Executed:N0} compiled); {env.Translations.Invalidations:N0} page invalidations, {env.Translations.CompileMilliseconds:F0} ms compiling");
        diagnostics.ReportStop(count);
        control?.Stopped(cpu, count);
        return count;
    }
}
