using static Emu86.Conversions;
using static Emu86.Mmu;
using static Emu86.Ports;

namespace Emu86;

internal sealed class RunDiagnostics
{
    readonly DiagnosticOptions options;
    readonly EmuEnvironment env;
    readonly CPU cpu;
    readonly TextWriter output;
    readonly TextWriter trace;
    const int BrN = 64;
    readonly (ushort cs, uint eip)[] brFrom = new (ushort, uint)[BrN];
    readonly (ushort cs, uint eip)[] brTo = new (ushort, uint)[BrN];
    int brIdx;
    uint beforeEsp;
    int entryLogLeft = 400;

    // 停止規則: 命令の前(ブレークポイント)と後(監視点・トラップ・分岐履歴・トレース)。各規則は止めるなら報告して true を返す。
    // オプションから組み立て、順に試す(順序が報告の優先順)。
    readonly record struct Step(ushort BeforeCs, uint BeforeEip, long Count);
    readonly List<Func<Step, bool>> breakpoints = [];
    readonly List<Func<Step, bool>> traps = [];
    int udLogLeft = 40;
    bool wasInRange;

    public RunDiagnostics(DiagnosticOptions options, EmuEnvironment env, CPU cpu, TextWriter output, TextWriter trace, long count)
    {
        this.options = options;
        this.env = env;
        this.cpu = cpu;
        this.output = output;
        this.trace = options.Trace ? trace : null;
        if (env.Ata != null) env.Ata.Log = options.AtaLog;
        env.Pci.Log = options.PciLog;
        env.Pci.Caller = () => WindowsDiagnostics.Caller(env, cpu);
        env.PicLog = options.PicLog;
        env.Vga.Log = options.VgaLog;
        env.IntLog = options.IntLog ? [] : null;
        env.WriteLog = options.WriteLog != null ? [] : null;
        env.WLogLo = options.WriteLog?.Lo ?? 0;
        env.WLogHi = options.WriteLog?.Hi ?? 0;
        env.WatchLo = options.Watch.Lo;
        env.WatchHi = options.Watch.Hi;
        env.WatchTriggered = false;
        if (count > 0) this.trace?.WriteLine($"\n--- resumed at instruction {count} ---");

        if (options.BreakEip != 0)
            breakpoints.Add(_ => cpu.eip == options.BreakEip && (options.BreakEax == null || cpu.eax == options.BreakEax) && Break());
        if (options.BreakRange.Hi != 0)
            breakpoints.Add(_ => cpu.pe && options.BreakRange.Contains(cpu.eip) && Break());
        if (options.BreakAt != 0)
            breakpoints.Add(step => step.Count >= options.BreakAt && Break());

        traps.Add(step =>
        {
            if (!env.WatchTriggered) return false;
            env.WatchTriggered = false;
            return Stop($"WATCH: write into [{env.WatchLo:x8},{env.WatchHi:x8}) by instruction at {step.BeforeCs:x4}:{step.BeforeEip:x8} (instr {step.Count})");
        });
        // ESP がカーネルのエントリコード領域を指す不正値になった瞬間を捕捉する。
        if (options.EspTrap)
            traps.Add(step =>
            {
                var trapped = WindowsDiagnostics.IsKernelEntryStack(cpu.esp) && !WindowsDiagnostics.IsKernelEntryStack(beforeEsp);
                if (!trapped) beforeEsp = cpu.esp;
                return trapped && Stop($"ESPTRAP: esp={cpu.esp:x8} set by {step.BeforeCs:x4}:{step.BeforeEip:x8} (instr {step.Count})");
            });
        if (options.WatchValue is uint watchValue)
            traps.Add(step =>
                (cpu.eax == watchValue || cpu.ebx == watchValue || cpu.ecx == watchValue || cpu.edx == watchValue
                    || cpu.esi == watchValue || cpu.edi == watchValue || cpu.ebp == watchValue)
                && Stop($"WATCHVAL {watchValue:x8} appeared after instruction at {step.BeforeCs:x4}:{step.BeforeEip:x8} (instr {step.Count}): eax={cpu.eax:x8} ebx={cpu.ebx:x8} ecx={cpu.ecx:x8} edx={cpu.edx:x8} esi={cpu.esi:x8} edi={cpu.edi:x8} ebp={cpu.ebp:x8} esp={cpu.esp:x8}"));
        if (options.BranchHistory)
            traps.Add(step =>
            {
                // 制御転送(順次進行を外れた)命令を記録する。
                if (!Sequential(step))
                {
                    brFrom[brIdx] = (step.BeforeCs, step.BeforeEip);
                    brTo[brIdx] = (cpu.cs, cpu.eip);
                    brIdx = (brIdx + 1) % BrN;
                }
                // プロテクトモードで CS が NULL セレクタになった瞬間を捕捉する。
                return cpu.pe && cpu.cs == 0 && step.BeforeCs != 0
                    && Stop($"CSTRAP: cs=0 loaded by instruction at {step.BeforeCs:x4}:{step.BeforeEip:x8} -> eip={cpu.eip:x8} (instr {step.Count})");
            });
        if (this.trace != null)
            traps.Add(step =>
            {
                // 制御転送(分岐/ジャンプ/CALL/RET/割り込み)が起きた命令だけ記録する。
                if (options.TraceAll || !Sequential(step))
                    this.trace.WriteLine(options.RegTrace
                        ? $"{cpu.cs:x4}:{cpu.eip:x8} {cpu.eax:x8} {cpu.ebx:x8} {cpu.ecx:x8} {cpu.edx:x8} {cpu.esi:x8} {cpu.edi:x8} {cpu.ebp:x8} {cpu.esp:x8} {cpu.eflags:x8}"
                        : $"{cpu.cs:x4}:{cpu.eip:x8}");
                return false;
            });
    }

    // 順次進行なら EIP は開始位置 +1〜+15。それを外れた/CS が変わった = 分岐。
    bool Sequential(Step step) => cpu.cs == step.BeforeCs && cpu.eip > step.BeforeEip && cpu.eip <= step.BeforeEip + 15;

    bool Stop(string message)
    {
        output.WriteLine(message);
        return true;
    }

    // ブレークポイント: レジスタとスタックの先頭を報告して止める。
    bool Break()
    {
        output.WriteLine($"BREAKEIP {cpu.eip:x8}: eax={cpu.eax:x8} ecx={cpu.ecx:x8} edx={cpu.edx:x8} ebx={cpu.ebx:x8} esi={cpu.esi:x8} edi={cpu.edi:x8} ebp={cpu.ebp:x8} esp={cpu.esp:x8}");
        try
        {
            for (var offset = 0u; offset < 0x40; offset += 4)
                output.WriteLine($"  [esp+{offset:x2}] = {WindowsDiagnostics.ReadDword(env, cpu.ss_base + cpu.esp + offset):x8}");
        }
        catch (CpuFaultException) { output.WriteLine("  (stack unreadable)"); }
        return true;
    }

    public bool AfterInstruction(ushort beforeCs, uint beforeEip, long count)
    {
        var step = new Step(beforeCs, beforeEip, count);
        return traps.Any(rule => rule(step));
    }

    // 連結したブロックの途中で起きた制御転送をトレースに書く(Sequential と同じ規則。CS は変わらない)。
    public void TraceTransfers(ushort cs, List<(uint From, uint To)> transfers)
    {
        if (trace == null) return;
        foreach (var (from, to) in transfers)
            if (!(to > from && to <= from + 15)) trace.WriteLine($"{cs:x4}:{to:x8}");
    }

    public bool BeforeInstruction(long count)
    {
        env.CurEip = cpu.eip;
        if (options.EntryLog.Hi != 0 && cpu.pe)
        {
            var inRange = options.EntryLog.Contains(cpu.eip);
            if (inRange && !wasInRange && entryLogLeft > 0)
            {
                entryLogLeft--;
                WindowsDiagnostics.LogEntry(env, cpu, count, output);
            }
            wasInRange = inRange;
        }
        var step = new Step(cpu.cs, cpu.eip, count);
        if (breakpoints.Any(rule => rule(step))) return true;
        if (env.Ata != null) env.Ata.LogReads = env.PagingOn;
        env.Pci.Eip = cpu.eip;
        return false;
    }

    // フォルト発生時の診断。true を返すと実行を止める(配送前に呼ばれる)。
    public bool Fault(Fault fault, long count)
    {
        switch (fault.Vector)
        {
            case 6:
                // 配送できる #UD は先頭数十回をログに残す(診断用)。
                if (fault.IdtReady(cpu) && udLogLeft > 0)
                {
                    udLogLeft--;
                    output.WriteLine($"[cpu] #UD at {cpu.cs:x4}:{cpu.eip:x8} opcode {OpcodeBytes()} (instr {count}) -> IDT vector 6");
                }
                return false;
            case 14:
                var linear = fault.Linear ?? 0;
                var errorCode = fault.ErrorCode ?? 0;
                if (options.UserPageFaultMin is long minimum && (cpu.cs & 3) == 3 && count >= minimum)
                    output.WriteLine($"[upf] {count} cr3={cpu.cr3:x8} {cpu.cs:x4}:{cpu.eip:x8} cr2={linear:x8} err={errorCode:x} esp={cpu.esp:x8}");
                if (!fault.IdtReady(cpu)) return false;
                if (!options.PfTrap || (errorCode & 1) == 0) return false;
                output.WriteLine($"PFTRAP: protection #PF lin={linear:x8} err={errorCode:x} at {cpu.cs:x4}:{cpu.eip:x8} (instr {count})");
                WriteMemoryLog();
                return true;
            default:
                return false;
        }
    }

    string OpcodeBytes()
    {
        var address = GetCodeAddr(cpu).addr;
        try
        {
            return string.Join(" ", Enumerable.Range(0, 6).Select(offset => EnvGetMemoryData8(env, address + (uint)offset).ToString("x2")));
        }
        catch (CpuFaultException) { return "(unmapped)"; }
    }

    void WriteMemoryLog()
    {
        if (env.WriteLog == null) return;
        output.WriteLine($"--- writes to [{env.WLogLo:x8},{env.WLogHi:x8}) (last 30 of {env.WriteLog.Count}) ---");
        foreach (var entry in env.WriteLog.TakeLast(30)) output.WriteLine("  " + entry);
    }

    public void ReportStop(long count)
    {
        output.WriteLine($"STOP at {cpu.cs:x4}:{cpu.eip:x8} after {count} instructions, opcode: {OpcodeBytes()}");
        WriteMemoryLog();
        if (env.IntLog != null)
        {
            output.WriteLine($"--- protected-mode interrupt deliveries (total {env.IntLog.Count}) ---");
            output.WriteLine("[first 30]");
            foreach (var entry in env.IntLog.Take(30))
                output.WriteLine("  " + entry);
            output.WriteLine($"[last {options.IntLogLast}]");
            foreach (var entry in env.IntLog.TakeLast(options.IntLogLast))
                output.WriteLine("  " + entry);
            if (env.IntLogV86.Count > 0)
            {
                output.WriteLine($"--- deliveries from virtual-8086 mode (total {env.IntLogV86.Count}) ---");
                foreach (var entry in env.IntLogV86.TakeLast(200))
                    output.WriteLine("  " + entry);
            }
        }
        if (options.BranchHistory)
        {
            output.WriteLine("--- last branches (from -> to) ---");
            for (var index = 0; index < BrN; index++)
            {
                var position = (brIdx + index) % BrN;
                if (brTo[position] == default) continue;
                output.WriteLine($"  {brFrom[position].cs:x4}:{brFrom[position].eip:x8} -> {brTo[position].cs:x4}:{brTo[position].eip:x8}");
            }
        }
    }
}
