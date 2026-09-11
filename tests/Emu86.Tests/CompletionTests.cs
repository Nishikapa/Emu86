using Emu86;
using static Emu86.Ext;

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
        BitConverter.GetBytes(value).CopyTo(env.OneMegaMemory_, address);

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
        fault.CopyTo(env.OneMegaMemory_, 0x100);
        handler.CopyTo(env.OneMegaMemory_, 0x400);
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

    public static void Run()
    {
        foreach (var slow in new[] { false, true }) { FaultAtLimit(slow); HandlerExecution(slow); }
        EmptyBudget(0, 0); EmptyBudget(8, 8); EmptyBudget(9, 8);
        FailedDelivery();
        Console.WriteLine($"Completion checks passed: {assertions} assertions");
    }
}
