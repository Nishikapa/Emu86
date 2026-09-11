using static Emu86.Ext;

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
        env.IntLog = options.IntLog ? [] : null;
        env.WriteLog = options.WriteLog != null ? [] : null;
        env.WLogLo = options.WriteLog?.Lo ?? 0;
        env.WLogHi = options.WriteLog?.Hi ?? 0;
        env.WatchLo = options.Watch.Lo;
        env.WatchHi = options.Watch.Hi;
        env.WatchTriggered = false;
        if (count > 0) this.trace?.WriteLine($"\n--- resumed at instruction {count} ---");
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
        if ((options.BreakEip != 0 && cpu.eip == options.BreakEip && (options.BreakEax == null || cpu.eax == options.BreakEax))
            || (cpu.pe && options.BreakRange.Contains(cpu.eip)) || (options.BreakAt != 0 && count >= options.BreakAt))
        {
            output.WriteLine($"BREAKEIP {cpu.eip:x8}: eax={cpu.eax:x8} ecx={cpu.ecx:x8} edx={cpu.edx:x8} ebx={cpu.ebx:x8} esi={cpu.esi:x8} edi={cpu.edi:x8} ebp={cpu.ebp:x8} esp={cpu.esp:x8}");
            try
            {
                for (var offset = 0u; offset < 0x40; offset += 4)
                    output.WriteLine($"  [esp+{offset:x2}] = {WindowsDiagnostics.ReadDword(env, cpu.ss_base + cpu.esp + offset):x8}");
            }
            catch (PageFaultException) { output.WriteLine("  (stack unreadable)"); }
            return true;
        }
        if (env.Ata != null) env.Ata.LogReads = env.PagingOn;
        env.Pci.Eip = cpu.eip;
        return false;
    }

    public bool AfterInstruction(ushort beforeCs, uint beforeEip, long count)
    {
        if (env.WatchTriggered)
        {
            output.WriteLine($"WATCH: write into [{env.WatchLo:x8},{env.WatchHi:x8}) by instruction at {beforeCs:x4}:{beforeEip:x8} (instr {count})");
            env.WatchTriggered = false;
            return true;
        }
        // ESP がカーネルのエントリコード領域を指す不正値になった瞬間を捕捉する。
        if (options.EspTrap && WindowsDiagnostics.IsKernelEntryStack(cpu.esp) && !WindowsDiagnostics.IsKernelEntryStack(beforeEsp))
        {
            output.WriteLine($"ESPTRAP: esp={cpu.esp:x8} set by {beforeCs:x4}:{beforeEip:x8} (instr {count})");
            return true;
        }
        beforeEsp = cpu.esp;
        if (options.WatchValue is uint watchVal && (cpu.eax == watchVal || cpu.ebx == watchVal || cpu.ecx == watchVal || cpu.edx == watchVal
            || cpu.esi == watchVal || cpu.edi == watchVal || cpu.ebp == watchVal))
        {
            output.WriteLine($"WATCHVAL {watchVal:x8} appeared after instruction at {beforeCs:x4}:{beforeEip:x8} (instr {count}): eax={cpu.eax:x8} ebx={cpu.ebx:x8} ecx={cpu.ecx:x8} edx={cpu.edx:x8} esi={cpu.esi:x8} edi={cpu.edi:x8} ebp={cpu.ebp:x8} esp={cpu.esp:x8}");
            return true;
        }
        if (options.BranchHistory)
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
                output.WriteLine($"CSTRAP: cs=0 loaded by instruction at {beforeCs:x4}:{beforeEip:x8} -> eip={cpu.eip:x8} (instr {count})");
                return true;
            }
        }
        if (trace != null)
        {
            // 制御転送(分岐/ジャンプ/CALL/RET/割り込み)が起きた命令だけ記録する。
            // 順次進行なら EIP は開始位置 +1〜+15。それを外れた/CS が変わった = 分岐。
            var seq = cpu.cs == beforeCs && cpu.eip > beforeEip && cpu.eip <= beforeEip + 15;
            if (options.TraceAll || !seq)
                trace.WriteLine(options.RegTrace
                    ? $"{cpu.cs:x4}:{cpu.eip:x8} {cpu.eax:x8} {cpu.ebx:x8} {cpu.ecx:x8} {cpu.edx:x8} {cpu.esi:x8} {cpu.edi:x8} {cpu.ebp:x8} {cpu.esp:x8} {cpu.eflags:x8}"
                    : $"{cpu.cs:x4}:{cpu.eip:x8}");
        }
        return false;
    }

    public void UndefinedInstruction(long count)
    {
        if (udLogLeft <= 0) return;
        udLogLeft--;
        output.WriteLine($"[cpu] #UD at {cpu.cs:x4}:{cpu.eip:x8} opcode {OpcodeBytes()} (instr {count}) -> IDT vector 6");
    }

    public bool PageFault(PageFaultException fault, long count)
    {
        if (options.UserPageFaultMin is long minimum && (cpu.cs & 3) == 3 && count >= minimum)
            output.WriteLine($"[upf] {count} cr3={cpu.cr3:x8} {cpu.cs:x4}:{cpu.eip:x8} cr2={fault.Linear:x8} err={fault.ErrorCode:x} esp={cpu.esp:x8}");
        if (!cpu.pe || cpu.idt_limit < 14 * 8 + 7) return false;
        if (!options.PfTrap || (fault.ErrorCode & 1) == 0) return false;
        output.WriteLine($"PFTRAP: protection #PF lin={fault.Linear:x8} err={fault.ErrorCode:x} at {cpu.cs:x4}:{cpu.eip:x8} (instr {count})");
        WriteMemoryLog();
        return true;
    }

    string OpcodeBytes()
    {
        var address = GetCodeAddr(cpu).addr;
        try
        {
            return string.Join(" ", Enumerable.Range(0, 6).Select(offset => EnvGetMemoryData8(env, address + (uint)offset).ToString("x2")));
        }
        catch (PageFaultException) { return "(unmapped)"; }
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
            output.WriteLine("[last 4]");
            foreach (var entry in env.IntLog.TakeLast(4))
                output.WriteLine("  " + entry);
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
