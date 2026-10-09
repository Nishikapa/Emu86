using Emu86;
using static Emu86.Ext;

static class StateTests
{
    static int assertions;

    static void Equal<T>(T expected, T actual, string label)
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label}: expected {expected}, got {actual}");
    }

    static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new Exception($"{label}: expected {typeof(T).Name}");
    }

    static void CpuUpdates()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var original = new CPU { eax = 10 };
        var replacement = new CPU { eax = 20 };
        var calls = 0;
        var update = SetCpu(cpu => { Equal(original, cpu, "input CPU"); calls++; return replacement; });
        Equal(0, calls, "CPU update is deferred");
        var result = update(env, original, null);
        Equal(true, result.IsSuccess, "CPU update success");
        Equal(replacement, result.cpu, "replacement CPU returned");
        Equal(string.Empty, result.log, "CPU update log");
        Equal(1, calls, "CPU update runs once");
        var contextual = SetCpu((context, cpu) =>
        {
            Equal(env, context, "environment passed through");
            Equal(original, cpu, "contextual input CPU");
            calls++;
            return replacement;
        });
        Equal(replacement, contextual(env, original, null).cpu, "contextual replacement CPU");
        Equal(2, calls, "contextual update runs once");
        var read = GetDataFromCpu(cpu => { calls++; return cpu.eax; });
        Equal(2, calls, "CPU read is deferred");
        var readResult = read(env, replacement, null);
        Equal(20u, readResult.value, "CPU read value");
        Equal(replacement, readResult.cpu, "CPU read retains instance");
        Equal(3, calls, "CPU read runs once");
        var redirect = new Accessor<CPU, uint>(cpu => cpu.eax, cpu => value =>
        {
            Equal(original, cpu, "batch first CPU");
            replacement.eax = value;
            return replacement;
        });
        var accumulate = new Accessor<CPU, uint>(cpu => cpu.eax, cpu => value =>
        {
            Equal(replacement, cpu, "batch threads replacement CPU");
            cpu.eax += value;
            return cpu;
        });
        var batch = SetCpu((redirect, 100u), (accumulate, 23u));
        Equal(20u, replacement.eax, "batch is deferred");
        Equal(replacement, batch(env, original, null).cpu, "batch CPU result");
        Equal(123u, replacement.eax, "batch updates in order");
        Equal(10u, original.eax, "batch original CPU unchanged");
        Equal(original, SetCpu<uint>()(env, original, null).cpu, "empty batch");
        var failing = new Accessor<CPU, uint>(cpu => cpu.eax, cpu => value => throw new InvalidOperationException());
        Throws<InvalidOperationException>(() => SetCpu((CPU._eax, 42u), (failing, 1u), (CPU._eax, 99u))(env, original, null), "batch stops on exception");
        Equal(42u, original.eax, "completed batch updates retained");
        original.eip = 0;
        Equal(original, IpInc(-1)(env, original, null).cpu, "IP update retains CPU");
        Equal(uint.MaxValue, original.eip, "IP decrement wraps");
        IpInc(2)(env, original, null);
        Equal(1u, original.eip, "IP increment wraps");
    }

    static void OperandWidths()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = new CPU();
        for (var type = 0; type < 3; type++)
        {
            var reads = new int[3];
            State<byte> readByte = (context, state, opcodes) => { reads[0]++; return (true, 0x12, state, "byte"); };
            State<ushort> readWord = (context, state, opcodes) => { reads[1]++; return (true, 0x1234, state, "word"); };
            State<uint> readDword = (context, state, opcodes) => { reads[2]++; return (true, 0x12345678, state, "dword"); };
            var result = Choice(type, readByte, readWord, readDword)(env, cpu, null);
            Equal(type, result.value.type, "operand width");
            Equal(new uint[] { 0x12, 0x1234, 0x12345678 }[type], result.value.Value(), "operand value");
            Equal(new[] { "byte", "word", "dword" }[type], result.log, "selected state log");
            Equal(cpu, result.cpu, "selected state CPU");
            Equal(1, reads[type], "selected state runs once");
            Equal(1, reads.Sum(), "unselected states do not run");
            Array.Clear(reads);
            var convert = GetTypeData_<uint>(type,
                value => { reads[0]++; return (byte)value; },
                value => { reads[1]++; return (ushort)value; },
                value => { reads[2]++; return value; });
            Equal(0, reads.Sum(), "conversion deferred");
            Equal(0x87654321u & Mask(type), convert(0x87654321).Value(), "conversion width");
            Equal(1, reads[type], "selected converter runs once");
            Equal(1, reads.Sum(), "unselected converters do not run");
        }
        foreach (var invalid in new[] { -1, 3 })
        {
            Throws<IndexOutOfRangeException>(() => Choice(invalid, ((byte)0).ToState(), ((ushort)0).ToState(), 0u.ToState()), "invalid operand width");
            Throws<IndexOutOfRangeException>(() => GetTypeData_<uint>(invalid, value => (byte)value, value => (ushort)value, value => value), "invalid conversion width");
        }
    }

    static void ControlRegisters()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = new CPU();
        foreach (var entry in new[] { (Register: 0, Value: 0x80010001u), (Register: 2, Value: 0x12345678u),
            (Register: 3, Value: 0x9000u), (Register: 4, Value: 0x20u) })
        {
            env.TlbTagR[0] = env.TlbTagW[0] = uint.MaxValue;
            var result = SetCrReg(entry.Register, entry.Value)(env, cpu, null);
            Equal(true, result.IsSuccess, "control register write");
            Equal(cpu, result.cpu, "control register CPU");
            Equal(entry.Value, GetCrReg(entry.Register)(env, cpu, null).value, "control register readback");
            Equal(0u, env.TlbTagR[0], "read TLB flushed");
            Equal(0u, env.TlbTagW[0], "write TLB flushed");
        }
        Equal(true, env.PagingOn && env.WpOn && env.PaeOn, "paging state synchronized");
        Equal(0x9000u, env.Cr3Base, "page table base synchronized");
        foreach (var invalid in new[] { -1, 1, 5, 8 })
        {
            Throws<KeyNotFoundException>(() => GetCrReg(invalid), "invalid control register read");
            Throws<KeyNotFoundException>(() => SetCrReg(invalid, 0), "invalid control register write");
        }
    }

    public static void Run()
    {
        CpuUpdates();
        OperandWidths();
        ControlRegisters();
        Console.WriteLine($"State helper assertions passed: {assertions}");
    }
}
