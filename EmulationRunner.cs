using static Emu86.Conversions;
using static Emu86.Interrupts;
using static Emu86.Mmu;

namespace Emu86;

internal static class EmulationRunner
{
    // サービス周期(キーボードの Pump、停止要求の確認、自動操作)。時間ではなく命令数の区切り。タイマ IRQ の周期は PIT が決める。
    const long ServicePeriod = 10_000;
    const long SnapshotInterval = 100_000_000;

    public static long Run(RunOptions options, EmuEnvironment env, CPU cpu, long count = 0,
        TextWriter trace = null, TextWriter output = null, TextWriter error = null,
        Action<long, CPU, EmuEnvironment> saveSnapshot = null, long snapshotInterval = SnapshotInterval,
        CancellationToken stop = default, EmulationControl control = null, BootAutomation bootAutomation = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapshotInterval);
        return new Session(options, env, cpu, count, trace, output ?? Console.Out, error ?? Console.Error,
            saveSnapshot, snapshotInterval, stop, control, bootAutomation).Run();
    }

    // 1 回の実行。ループの状態(命令数、次のサービス・保存の時刻、統計)を持ち、周期ごとのサービス、割り込みの配送、1 Step の実行、
    // フォルトの配送、進捗と定期保存、停止後の報告を分けて書く。各段は続行できないとき false(停止)を返す。
    sealed class Session
    {
        readonly RunOptions options;
        readonly EmuEnvironment env;
        readonly TextWriter output, error;
        readonly Action<long, CPU, EmuEnvironment> saveSnapshot;
        readonly long snapshotInterval;
        readonly CancellationToken stop;
        readonly EmulationControl control;
        readonly BootAutomation bootAutomation;
        readonly RunDiagnostics diagnostics;
        readonly InstructionExecutor executor;
        readonly System.Diagnostics.Stopwatch swatch = System.Diagnostics.Stopwatch.StartNew();
        // 翻訳ブロックの外(通常コア)で実行した命令の先頭 2 バイト別の回数。IR に無い命令の調査用([outside] に上位を出す)。
        readonly Dictionary<ushort, long> outsideReference = [];
        CPU cpu;
        long count;
        long lastReport;
        long translatedCount;
        long haltSkipped, halts;
        long nextSnapshot, nextService;

        public Session(RunOptions options, EmuEnvironment env, CPU cpu, long count, TextWriter trace, TextWriter output, TextWriter error,
            Action<long, CPU, EmuEnvironment> saveSnapshot, long snapshotInterval, CancellationToken stop, EmulationControl control, BootAutomation bootAutomation)
        {
            this.options = options;
            this.env = env;
            this.cpu = cpu;
            this.count = count;
            this.output = output;
            this.error = error;
            this.saveSnapshot = saveSnapshot;
            this.snapshotInterval = snapshotInterval;
            this.stop = stop;
            this.control = control;
            this.bootAutomation = bootAutomation;
            env.Paging.Sync(cpu);
            diagnostics = new RunDiagnostics(options.Diagnostics, env, cpu, output, trace, count);
            executor = new InstructionExecutor(useTranslation: !options.Slow);
            // 命令ごとに CPU を見る診断(全命令トレース・ブレークポイント・監視点)があるときは、ブロックをまとめて実行できない。
            // 翻訳キャッシュは --slow 以外では常に使う(IR の対象外の命令だけ通常コア)。基本ブロックと連結は、--no-translate と
            // 命令ごとに CPU を見る診断では切る(1 命令ずつ: Step = 1 命令)。
            env.Translations.Enabled = !options.Slow;
            var blocks = options.Translate && !options.Diagnostics.PerInstruction;
            env.Translations.BlockLimit = blocks ? 64 : 1;
            env.Translations.Chain = blocks;
            // 連結したブロックの途中の制御転送をトレース行にするため、トレースが有効なときは記録してもらう。
            env.Translations.Transfers = env.Translations.Enabled && trace != null && options.Diagnostics.Trace ? [] : null;
            lastReport = count;
            if (env.NextIrq == 0 || env.Pit.Changed) { env.NextIrq = env.Pit.NextTimerIrq((ulong)count); env.Pit.Changed = false; }
            // 定期保存なし(間隔 = long.MaxValue)のときは、再開後の count との加算が溢れないように飽和させる。
            nextSnapshot = NextSnapshot(count);
            nextService = count + ServicePeriod;
        }

        long NextSnapshot(long from) => snapshotInterval > long.MaxValue - from ? long.MaxValue : from + snapshotInterval;

        enum Outcome { Continue, Stop, Reset }

        public long Run()
        {
            while (count < options.Limit)
            {
                if (count >= nextService)
                {
                    var outcome = ServiceDevices();
                    if (outcome == Outcome.Reset) return Stopped();
                    if (outcome == Outcome.Stop) break;
                }
                if (!options.NoIrq && !DeliverInterrupt()) break;
                var beforeCs = cpu.cs;
                if (diagnostics.BeforeInstruction(count)) break;
                SetBudget();
                if (!Step(beforeCs)) break;
                Report();
            }
            return Finish();
        }

        // サービス周期: ホストから注入されたスキャンコードを 8042 の出力バッファへ移し、表示ウィンドウが閉じられたなどの停止要求も
        // ここで確認する(命令ごとに見るほどの精度は要らない)。
        Outcome ServiceDevices()
        {
            nextService = count + ServicePeriod;
            env.Keyboard.Pump();
            env.Com1.Pump();
            env.Com2.Pump();
            bootAutomation?.Step(env, count, output);
            if (bootAutomation is { Completed: true, StopWhenCompleted: true })
            {
                output.WriteLine($"automation completed at {cpu.cs:x4}:{cpu.eip:x8} (instr {count})");
                return Outcome.Stop;
            }
            control?.WaitIfPaused(cpu, count, stop);
            if (stop.IsCancellationRequested)
            {
                output.WriteLine($"stopped by request at {cpu.cs:x4}:{cpu.eip:x8} (instr {count})");
                return Outcome.Stop;
            }
            // システムリセット(8042 の 0xFE/出力ポート、ACPI/PIIX4 の 0xCF9)。呼び出し側が環境を作り直して
            // リセットベクタから再開する。直前の状態は保存しない(再開してもリセット待ちのまま止まるだけなので)。
            if (env.ResetRequested || env.Keyboard.ResetRequested)
            {
                env.ResetRequested = true;
                output.WriteLine($"[system] reset requested at {cpu.cs:x4}:{cpu.eip:x8} (instr {count})");
                return Outcome.Reset;
            }
            // 電源断(ACPI の S5): ゲストはこの先止まったままなので、実行を終えて最終状態を保存する(通常の停止と同じ)。
            if (env.Acpi.PowerOffRequested)
            {
                output.WriteLine($"[system] power off requested at {cpu.cs:x4}:{cpu.eip:x8} (instr {count})");
                return Outcome.Stop;
            }
            return Outcome.Continue;
        }

        // 仮想タイマ割り込み(IRQ0): PIT の周期で要求を立て、配送できる状態(IF=1 など)になるまで保留する。実機の 8259 と同じく
        // 要求は消えないので、IF=1 の区間が短いポーリングループ(SeaBIOS の sti; nop; cli)でもティックが進む。
        // ハードウェア割り込み(タイマ・キーボード・シリアル・マウス・ATA)は IF・PIC のマスク・in-service、プロテクトモードでは
        // IDT の準備を確認し、優先順で 1 本だけ配送する。配送中のフォルトは停止。
        bool DeliverInterrupt()
        {
            // PIT チャネル 0 の設定が変わっていたら次の IRQ0 の時刻を取り直す(ポート書き込みはブロックの終端なので Step の境界で見える)。
            if (env.Pit.Changed) { env.NextIrq = env.Pit.NextTimerIrq((ulong)count); env.Pit.Changed = false; }
            if (count >= env.NextIrq)
            {
                env.NextIrq = env.Pit.NextTimerIrq((ulong)count);
                env.TimerIrqPending = true;
            }
            var line = IrqDelivery.Select(env, cpu);
            if (line < 0) return true;
            try
            {
                var irq = executor.Execute(env, cpu, IrqDelivery.Pending(line));
                if (irq.IsSuccess) cpu = irq.cpu;
                return true;
            }
            catch (CpuFaultException exception)
            {
                output.WriteLine($"FAULT during {IrqDelivery.Name(line)} IRQ delivery: {exception.Fault.Describe()} at {cpu.cs:x4}:{cpu.eip:x8} esp={cpu.esp:x8} (instr {count})");
                return false;
            }
        }

        // 翻訳ブロックの予算: 次のイベント(タイマ周期・サービス周期・スナップショット・上限)までの命令数。
        // これでブロック実行の境界が 1 命令ずつの実行と一致する。
        void SetBudget()
        {
            if (!env.Translations.Enabled) return;
            var budget = options.Limit - count;
            if (!options.NoIrq) budget = Math.Min(budget, env.NextIrq - count);
            budget = Math.Min(budget, Math.Min(nextService - count, nextSnapshot - count));
            executor.Budget = (int)Math.Max(1, budget);
        }

        // 1 Step: 翻訳キャッシュ(IR)で実行し、IR の対象外の命令だけ通常コアで実行する。命令が完了せずフォルトになった場合は配送する
        // (#UD は実行器の失敗、#PF/#GP は例外)。完了した命令数を数え、HLT なら仮想時間を進め、命令後の診断で止めるなら false。
        bool Step(ushort beforeCs)
        {
            Fault? fault = null;
            var completed = true;
            try
            {
                var result = executor.Step(env, cpu);
                if (executor.UsedTranslation) translatedCount += executor.Executed;
                else if (env.Translations.Enabled && result.IsSuccess) CountOutside(outsideReference, env, cpu.cs_base + executor.LastEip);
                if (result.IsSuccess)
                    cpu = result.cpu;
                else
                {
                    completed = false;
                    // 未定義/未対応命令は #UD(ベクタ 6)。Virtual PC のハイパーコール命令 0F C7 C8 のように、
                    // OS 側が例外を前提に実行する不正命令がある。
                    fault = Fault.UndefinedOpcode;
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
                return false;
            }
            // 連結したブロックの途中の制御転送(最後のブロックの分は下の AfterInstruction が見る)。
            if (executor.UsedTranslation && env.Translations.Transfers is { Count: > 0 } transfers) diagnostics.TraceTransfers(cpu.cs, transfers);
            if (fault is Fault raised && !Deliver(raised)) return false;
            // 完了した命令数(翻訳ブロックは複数。フォルト時は Deliver で加算済み)に、完了しなかった 1 命令(#UD・フォルト・VGA BIOS スタブ)を足す。
            count += (fault == null ? executor.Executed : 0) + (completed ? 0 : 1);
            SkipHalt();
            env.Tsc = (ulong)count; // RDTSC の代用カウンタ
            // #PF の配送は命令完了トレースに含めない(#UD/#GP の配送は従来どおり命令の完了として扱う)。
            // ブロックの途中の命令は順次実行なので、最後に完了した命令だけを見れば分岐トレースは同じになる。
            return fault?.Vector == 14 || !diagnostics.AfterInstruction(beforeCs, executor.LastEip, count);
        }

        // フォルトの配送: 命令開始時の状態(InstructionExecutor が巻き戻し済み)から IDT 経由で配送する。
        // IDT が未整備(プロテクトモードでない/limit 不足)なら停止する。翻訳ブロックの途中で起きた場合、完了した命令の分を数えてから診断する。
        bool Deliver(Fault raised)
        {
            raised = raised.ForPrivilege(cpu); // #PF の U/S ビット(CPL 3 からのアクセス)
            count += executor.Executed;
            if (diagnostics.Fault(raised, count)) return false;
            if (!raised.IdtReady(cpu))
            {
                if (raised.Vector != 6) output.WriteLine($"EXCEPTION at {cpu.cs:x4}:{cpu.eip:x8}: {raised.Message}");
                return false;
            }
            try
            {
                var delivered = executor.Execute(env, cpu, Raise(raised));
                if (!delivered.IsSuccess) { output.WriteLine($"{raised.Name} delivery failed at {cpu.cs:x4}:{cpu.eip:x8}"); return false; }
                cpu = delivered.cpu;
                return true;
            }
            catch (CpuFaultException second)
            {
                // 配送中の再フォルト = ダブルフォルト相当。診断を出して停止する。
                output.WriteLine($"DOUBLE FAULT: {raised.Name} delivery faulted {second.Fault.Describe()}");
                output.WriteLine($"  original {raised.Name}: {raised.Describe()} at {cpu.cs:x4}:{cpu.eip:x8} esp={cpu.esp:x8} (instr {count})");
                return false;
            }
        }

        // HLT: 配送できる割り込みが無ければ、次のイベント(タイマ周期・サービス周期・保存・上限)まで仮想時間を進める。
        // ゲストの時刻は命令数が基準なので、実機が割り込みまで止まっているのと同じことになる。
        // IF=0(割り込みで起きられない)や --noirq のときは従来どおり次の命令へ進む。
        void SkipHalt()
        {
            if (!cpu.Halted) return;
            cpu.Halted = false;
            if (options.NoIrq || !cpu.jf || IrqDelivery.Select(env, cpu) >= 0) return;
            var wake = Math.Min(Math.Min(env.NextIrq, nextService), Math.Min(nextSnapshot, options.Limit));
            if (wake > count) { halts++; haltSkipped += wake - count; env.IdleInstructions += wake - count; count = wake; }
        }

        // 進捗(5 秒ごと)と定期保存。
        void Report()
        {
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
                nextSnapshot = NextSnapshot(count);
            }
        }

        // 停止後: 最終状態の保存と統計の報告。
        long Finish()
        {
            if (count >= options.Limit) output.WriteLine("instruction limit reached");
            // 自動操作が終わらないまま止まったときは、何を待っていたか分かるように画面を出す。
            if (bootAutomation is { Completed: false }) { output.WriteLine($"[automation] not completed (status {bootAutomation.Status}); screen at stop:"); BootAutomation.DumpScreen(env, output); }
            saveSnapshot?.Invoke(count, cpu, env);
            if (halts > 0)
                error.WriteLine($"[halt] {halts:N0} waits skipped {haltSkipped:N0} instructions of idle time ({100.0 * haltSkipped / Math.Max(1, count):F1}% of the count)");
            if (!options.Slow && count > 0)
                output.WriteLine($"fast-path coverage: {translatedCount:N0}/{count:N0} ({100.0 * translatedCount / count:F2}%)");
            if (env.Translations.Enabled && count > 0)
                error.WriteLine($"[translation] {env.Translations.Translated:N0} sites lowered ({env.Translations.Blocks:N0} as blocks), {env.Translations.Compiled:N0} compiled; {translatedCount:N0}/{count:N0} instructions ({100.0 * translatedCount / count:F2}%: {env.Translations.Interpreted:N0} interpreted, {env.Translations.Executed:N0} compiled); {env.Translations.Runs:N0} block runs ({(env.Translations.Runs == 0 ? 0 : (double)translatedCount / env.Translations.Runs):F1} instructions each) in {env.Translations.Steps:N0} steps; {env.Translations.Invalidations:N0} page invalidations, {env.Translations.LowerMilliseconds:F0} ms lowering, {env.Translations.CompileMilliseconds:F0} ms compiling");
            if (env.Translations.Enabled && count > 0)
                error.WriteLine($"[outside] reference core {outsideReference.Values.Sum():N0}: {Top(outsideReference)}");
            if (env.Paging.Walks > 0)
                error.WriteLine($"[paging] {env.Paging.Flushes:N0} TLB flushes, {env.Paging.Walks:N0} page walks ({(env.Paging.Flushes == 0 ? 0 : (double)env.Paging.Walks / env.Paging.Flushes):F1} per flush)");
            return Stopped();
        }

        // 停止診断と中断・再開の窓口への通知(リセット要求で抜けるときは保存せずにここだけ)。
        long Stopped()
        {
            diagnostics.ReportStop(count);
            control?.Stopped(cpu, count);
            return count;
        }
    }

    // 副作用なしに命令の先頭 2 バイトを読む: ページング中は TLB に載っている(直前にフェッチした)ページだけ、RAM を直接読む
    // (共通経路で読むとページテーブルの Accessed ビットや VGA のラッチが変わり、1 命令ずつの実行と状態が食い違う)。
    static void CountOutside(Dictionary<ushort, long> histogram, EmuEnvironment env, uint linear)
    {
        uint physical;
        if (env.Paging.Enabled) { if (!env.Paging.TryLookup(linear, false, out physical)) return; }
        else physical = Mmu.EnvAlias(linear);
        if (physical + 1 >= (uint)env.Ram.Length) return;
        var key = (ushort)(env.Ram[physical] | (env.Ram[physical + 1] << 8));
        histogram[key] = histogram.GetValueOrDefault(key) + 1;
    }

    static string Top(Dictionary<ushort, long> histogram) =>
        string.Join(", ", histogram.OrderByDescending(entry => entry.Value).Take(8).Select(entry => $"{entry.Key & 0xFF:x2} {entry.Key >> 8:x2}={entry.Value:N0}"));
}
