using Emu86;
using static Emu86.Conversions;

static class CompletionTests
{
    static int assertions;

    static void Equal<T>(T expected, T actual, string name)
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{name}: expected {expected}, got {actual}");
    }

    static void Write32(EmuEnvironment env, int address, uint value) =>
        BitConverter.GetBytes(value).CopyTo(env.Ram, address);

    static CPU Prepare(EmuEnvironment env)
    {
        var cpu = new CPU { eip = 0x100, esp = 0x8000, eax = 5, code32 = true, stack32 = true,
            idt_base = 0x1000, idt_limit = 0x7FF, gdt_base = 0x500, gdt_limit = 23, eflags = 2 };
        CPU._cr0.setter(cpu)(0x80010001);
        CPU._cr3.setter(cpu)(0x9000);
        CPU._cs.setter(cpu)(8); cpu.cs_base = 0;
        CPU._ss.setter(cpu)(16); cpu.ss_base = 0;
        Write32(env, 0x9000, 0xA003);
        for (var page = 0; page < 8; page++) Write32(env, 0xA000 + page * 4, (uint)(page * 0x1000) | 3);
        Write32(env, 0x508, 0x0000FFFF); Write32(env, 0x50C, 0x00CF9A00);
        Write32(env, 0x510, 0x0000FFFF); Write32(env, 0x514, 0x00CF9200);
        Write32(env, 0x1000 + 14 * 8, 0x00080400);
        Write32(env, 0x1004 + 14 * 8, 0x00008E00);
        byte[] fault = [0xA1, 0x00, 0x80, 0x00, 0x00];
        byte[] handler = [0x40, 0xEB, 0xFD];
        fault.CopyTo(env.Ram, 0x100);
        handler.CopyTo(env.Ram, 0x400);
        return cpu;
    }

    static void FaultAtLimit(bool slow)
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = Prepare(env);
        var options = RunOptions.Parse(["--limit", "8", "--breakat", "10", "--trace-all", "--noirq", .. slow ? new[] { "--slow" } : []]);
        using var output = new StringWriter();
        using var trace = new StringWriter();
        var snapshots = new List<(long Count, ulong Tsc, uint Eip)>();
        var count = EmulationRunner.Run(options, env, cpu, 7, trace, output, TextWriter.Null,
            (savedCount, savedCpu, savedEnv) => snapshots.Add((savedCount, savedEnv.Tsc, savedCpu.eip)), snapshotInterval: 1);
        Equal(8L, count, "PF obeys instruction limit");
        Equal(8UL, env.Tsc, "PF advances TSC once");
        Equal(0x400u, cpu.eip, "stop before executing PF handler");
        Equal(5u, cpu.eax, "handler did not execute past limit");
        Equal(0x8000u, cpu.cr2, "fault linear address");
        Equal(0x7FF0u, cpu.esp, "fault frame pushed once");
        Equal(2, snapshots.Count, "periodic and final snapshots on PF");
        Equal((8L, 8UL, 0x400u), snapshots[0], "periodic snapshot after fault delivery");
        Equal((8L, 8UL, 0x400u), snapshots[1], "final snapshot after fault delivery");
        Equal(true, output.ToString().Contains("instruction limit reached"), "limit message after PF");
        Equal(false, trace.ToString().Contains("0008:"), "faulting instruction is not a completed instruction trace");
    }

    static void EmptyBudget(long start, long limit)
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = Prepare(env);
        env.Tsc = (ulong)start;
        var options = RunOptions.Parse(["--limit", limit.ToString(), "--notrace"]);
        var snapshots = new List<long>();
        var count = EmulationRunner.Run(options, env, cpu, start, output: TextWriter.Null, error: TextWriter.Null,
            saveSnapshot: (savedCount, savedCpu, savedEnv) => snapshots.Add(savedCount));
        Equal(start, count, "exhausted budget preserves count");
        Equal((ulong)start, env.Tsc, "exhausted budget preserves TSC");
        Equal(0x100u, cpu.eip, "exhausted budget does not execute");
        Equal(0x8000u, cpu.esp, "exhausted budget does not deliver faults");
        Equal(1, snapshots.Count, "exhausted budget still saves final state");
    }

    // 再開(count > 0)で定期保存なし(間隔 long.MaxValue): 次の保存時刻の計算が溢れて毎命令保存にならないこと。
    static void ResumeWithoutPeriodicSnapshots()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = Prepare(env);
        var snapshots = new List<long>();
        var count = EmulationRunner.Run(RunOptions.Parse(["--limit", "1050", "--notrace", "--noirq"]), env, cpu, 1000,
            output: TextWriter.Null, error: TextWriter.Null,
            saveSnapshot: (savedCount, savedCpu, savedEnv) => snapshots.Add(savedCount), snapshotInterval: long.MaxValue);
        Equal(1050L, count, "resumed run reaches the limit");
        Equal("1050", string.Join(",", snapshots), "only the final snapshot is saved when resuming without periodic snapshots");
    }

    static void HandlerExecution(bool slow)
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = Prepare(env);
        using var trace = new StringWriter();
        var options = RunOptions.Parse(["--limit", "3", "--trace-all", "--noirq", .. slow ? new[] { "--slow" } : []]);
        var snapshots = new List<long>();
        var count = EmulationRunner.Run(options, env, cpu, trace: trace, output: TextWriter.Null, error: TextWriter.Null,
            saveSnapshot: (savedCount, savedCpu, savedEnv) => snapshots.Add(savedCount), snapshotInterval: 1);
        Equal(3L, count, "fault and handler share count");
        Equal(6u, cpu.eax, "handler executes within budget");
        Equal("1,2,3,3", string.Join(',', snapshots), "periodic snapshots on fault and normal paths");
        Equal("0008:00000401\n0008:00000400\n", trace.ToString().Replace("\r\n", "\n"), "only completed handler instructions traced");
    }

    static void FailedDelivery()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = Prepare(env);
        Write32(env, 0xA000 + 7 * 4, 0);
        using var output = new StringWriter();
        var count = EmulationRunner.Run(RunOptions.Parse(["--limit", "1", "--notrace"]), env, cpu,
            output: output, error: TextWriter.Null);
        Equal(0L, count, "failed delivery does not count as success");
        Equal(0UL, env.Tsc, "failed delivery does not advance TSC");
        Equal(true, output.ToString().Contains("DOUBLE FAULT"), "failed delivery reported");
    }

    // HLT: 配送できる割り込みが無ければ次のタイマ周期まで仮想時間を進め、タイマ割り込みで起きる(ハンドラは EOI を送る)。
    // IF=0 のときは待てないので従来どおり次の命令へ進む。
    static void HaltWaitsForInterrupts()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        byte[] idle = [0xFB, 0xF4, 0xEB, 0xFD];                                        // sti ; hlt ; jmp hlt
        byte[] handler = [0xFE, 0x06, 0x00, 0x05, 0xB0, 0x20, 0xE6, 0x20, 0xCF];        // inc byte [0x500] ; mov al,20h ; out 20h,al ; iret
        idle.CopyTo(env.Ram, 0x100); handler.CopyTo(env.Ram, 0x200);
        BitConverter.GetBytes(0x00000200u).CopyTo(env.Ram, 8 * 4);          // IVT 8(タイマ)-> 0000:0200
        var cpu = new CPU { eip = 0x100, esp = 0x8000, eflags = 2 };
        using var error = new StringWriter();
        var count = EmulationRunner.Run(RunOptions.Parse(["--limit", "45000", "--notrace"]), env, cpu, 0, null, TextWriter.Null, error);
        Equal(45000L, count, "the idle loop reaches the limit in virtual time");
        Equal(4, (int)env.Ram[0x500], "the timer woke the idle loop at 10000, 20000, 30000 and 40000");
        Equal(true, error.ToString().Contains("[halt]"), "skipped idle time is reported");
        using var busy = new EmuEnvironment(memory: new byte[0x10000]);
        new byte[] { 0xFA, 0xF4, 0xEB, 0xFD }.CopyTo(busy.Ram, 0x100);     // cli ; hlt ; jmp hlt
        var busyCpu = new CPU { eip = 0x100, esp = 0x8000, eflags = 2 };
        using var busyError = new StringWriter();
        Equal(5000L, EmulationRunner.Run(RunOptions.Parse(["--limit", "5000", "--notrace"]), busy, busyCpu, 0, null, TextWriter.Null, busyError), "HLT with IF=0 just proceeds");
        Equal(false, busyError.ToString().Contains("[halt]"), "no idle time is skipped with IF=0");
    }

    public static void Run()
    {
        foreach (var slow in new[] { false, true }) { FaultAtLimit(slow); HandlerExecution(slow); }
        EmptyBudget(0, 0); EmptyBudget(8, 8); EmptyBudget(9, 8);
        ResumeWithoutPeriodicSnapshots();
        HaltWaitsForInterrupts();
        FailedDelivery();
        Console.WriteLine($"Completion checks passed: {assertions} assertions");
    }
}
