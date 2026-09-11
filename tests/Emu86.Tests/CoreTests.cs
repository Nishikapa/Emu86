using Emu86;
using System.Security.Cryptography;
using static Emu86.Ext;

static class CoreTests
{
    static readonly State<Unit> SlowStep = Emu86.Program.Execute2;
    static readonly EmuEnvironment SlowEnv = CreateEnvironment();
    static readonly EmuEnvironment FastEnv = CreateEnvironment();
    static readonly MemoryStream Results = new();
    static readonly BinaryWriter Writer = new(Results);
    static int cases;

    static EmuEnvironment CreateEnvironment()
    {
        return new EmuEnvironment(memory: new byte[0x10000]);
    }

    static CPU CreateCpu(uint left = 0, uint right = 0, uint flags = 0x202) =>
        new() { code32 = true, stack32 = true, eax = left, ebx = right, ecx = 3, esi = 0x3000, edi = 0x4000, esp = 0x8000, eflags = flags };

    static byte[] Instruction(int type, byte opcode, uint immediate)
    {
        var bytes = new List<byte>();
        if (type == 1) bytes.Add(0x66);
        bytes.Add(opcode);
        for (var byteIndex = 0; byteIndex < 1 << type; byteIndex++)
            bytes.Add((byte)(immediate >> (8 * byteIndex)));
        return bytes.ToArray();
    }

    static void Reset(EmuEnvironment env, byte[] code)
    {
        Array.Clear(env.OneMegaMemory_);
        code.CopyTo(env.OneMegaMemory_, 0);
        env.PagingOn = false;
        env.PaeOn = false;
        env.WpOn = false;
        env.WatchLo = env.WatchHi = 0;
        env.WatchTriggered = false;
        env.WriteLog = null;
        env.FlushTlb();
    }

    static byte[] CpuBytes(CPU cpu)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        cpu.WriteTo(writer);
        return stream.ToArray();
    }

    static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label}: expected {expected}, got {actual}");
    }

    static void Check(byte[] code, CPU initial, Action<EmuEnvironment> setup = null, Action<CPU, EmuEnvironment> expected = null, bool diagnostics = false)
    {
        var slowCpu = CreateCpu();
        var fastCpu = CreateCpu();
        initial.CopyTo(slowCpu);
        initial.CopyTo(fastCpu);
        Reset(SlowEnv, code);
        Reset(FastEnv, code);
        setup?.Invoke(SlowEnv);
        setup?.Invoke(FastEnv);
        var slowResult = SlowStep(SlowEnv, slowCpu, null);
        Equal(true, slowResult.IsSuccess, "slow success");
        Equal(true, FastStep(FastEnv, fastCpu), "fast success");
        Equal(Convert.ToHexString(CpuBytes(slowCpu)), Convert.ToHexString(CpuBytes(fastCpu)), $"CPU {Convert.ToHexString(code)}");
        Equal(true, SlowEnv.OneMegaMemory_.AsSpan().SequenceEqual(FastEnv.OneMegaMemory_), $"memory {Convert.ToHexString(code)}");
        if (diagnostics)
        {
            Equal(SlowEnv.WatchTriggered, FastEnv.WatchTriggered, "watchpoint");
            Equal(string.Join("|", SlowEnv.WriteLog ?? []), string.Join("|", FastEnv.WriteLog ?? []), "write log");
        }
        expected?.Invoke(slowCpu, SlowEnv);
        expected?.Invoke(fastCpu, FastEnv);
        Writer.Write(CpuBytes(slowCpu));
        Writer.Write(SHA256.HashData(SlowEnv.OneMegaMemory_));
        cases++;
    }

    static void Arithmetic()
    {
        for (var type = 0; type < 3; type++)
        {
            var mask = Mask(type);
            uint[] values = [0, 1, 0x0f, 0x10, Msb(type) - 1, Msb(type), mask - 1, mask];
            foreach (var left in values)
            foreach (var right in values)
            foreach (var carry in new uint[] { 0, 1 })
            {
                for (var kind = 0; kind < 8; kind++)
                    Check(Instruction(type, (byte)(kind * 8 + (type == 0 ? 4 : 5)), right), CreateCpu(left, flags: 0xED6 | carry));
                Check(Instruction(type, (byte)(type == 0 ? 0xA8 : 0xA9), right), CreateCpu(left, flags: 0xED6 | carry));
            }
            foreach (var value in values)
            foreach (var carry in new uint[] { 0, 1 })
            {
                foreach (var modrm in new byte[] { 0xC0, 0xC8 })
                    Check(type == 1 ? [0x66, 0xFF, modrm] : [(byte)(type == 0 ? 0xFE : 0xFF), modrm], CreateCpu(value, flags: 0xED6 | carry), expected: (cpu, _) => Equal(carry != 0, cpu.cf, "INC/DEC carry"));
                Check(type == 1 ? [0x66, 0xF7, 0xD8] : [(byte)(type == 0 ? 0xF6 : 0xF7), 0xD8], CreateCpu(value));
            }
        }
        Check([0x14, 0xFF], CreateCpu(0, flags: 0x203), expected: (cpu, _) => { Equal((byte)0, cpu.al, "ADC value"); Equal(true, cpu.cf, "ADC carry"); Equal(true, cpu.zf, "ADC zero"); Equal(true, cpu.af, "ADC auxiliary carry"); });
        Check([0x1C, 0xFF], CreateCpu(0, flags: 0x203), expected: (cpu, _) => { Equal((byte)0, cpu.al, "SBB value"); Equal(true, cpu.cf, "SBB borrow"); });
        for (uint flags = 0; flags < 32; flags++)
        {
            var cpu = CreateCpu();
            cpu.cf = (flags & 1) != 0; cpu.zf = (flags & 2) != 0; cpu.sf = (flags & 4) != 0;
            cpu.of = (flags & 8) != 0; cpu.pf = (flags & 16) != 0;
            for (var condition = 0; condition < 16; condition++)
                Check([(byte)(0x70 + condition), 0x20], cpu);
        }
    }

    static void MapPages(EmuEnvironment env, bool secondPresent = true)
    {
        BitConverter.GetBytes(0xA003u).CopyTo(env.OneMegaMemory_, 0x9000);
        BitConverter.GetBytes(0x0003u).CopyTo(env.OneMegaMemory_, 0xA000);
        BitConverter.GetBytes(0x5003u).CopyTo(env.OneMegaMemory_, 0xA004);
        if (secondPresent) BitConverter.GetBytes(0x7003u).CopyTo(env.OneMegaMemory_, 0xA008);
        env.Cr3Base = 0x9000;
        env.PagingOn = true;
        env.WpOn = true;
    }

    static byte[] Store(int type) => type == 0 ? [0x88, 0x03] : type == 1 ? [0x66, 0x89, 0x03] : [0x89, 0x03];

    static void Memory(bool extended)
    {
        for (var type = 0; type < 3; type++)
        {
            foreach (var address in new uint[] { 0x3000, 0x3fff, 0xffff, 0x10000 })
                Check(Store(type), CreateCpu(0x87654321, address));
            Check(Store(type), CreateCpu(0x87654321, 0x1fff), env => MapPages(env), (_, env) => Equal((byte)0x21, env.OneMegaMemory_[0x5fff], "paged write"));
            if (extended)
            {
                foreach (var address in new uint[] { 0xfff03000, 0xffffffff })
                    Check(Store(type), CreateCpu(0x87654321, address));
                Check(Store(type), CreateCpu(0x87654321, 0x1fff), env => { MapPages(env); env.WatchLo = 0x5fff; env.WatchHi = 0x6000; env.WLogLo = 0x5000; env.WLogHi = 0x8000; env.WriteLog = []; }, (_, env) => Equal(true, env.WatchTriggered, "paged watch"), diagnostics: true);
                Check(Store(type), CreateCpu(0x87654321, 0x1000), env => { MapPages(env); env.WatchLo = 0x5000; env.WatchHi = 0x5001; env.WLogLo = 0x5000; env.WLogHi = 0x5001; env.WriteLog = []; }, diagnostics: true);
            }
        }
        if (extended)
        {
            foreach (var fast in new[] { false, true })
            {
                var env = fast ? FastEnv : SlowEnv;
                Reset(env, Store(2));
                MapPages(env, false);
                var cpu = CreateCpu(0x87654321, 0x1fff);
                try
                {
                    if (fast) FastStep(env, cpu); else SetMemOrRegData((true, cpu.ebx), cpu.eax.ToTypeData())(env, cpu, null);
                    throw new Exception("expected page fault");
                }
                catch (PageFaultException fault)
                {
                    Equal(0x2000u, fault.Linear, "page fault address");
                    Equal(2u, fault.ErrorCode, "page fault code");
                    Equal((byte)0x21, env.OneMegaMemory_[0x5fff], "partial write preserved");
                    cases++;
                }
            }
        }
    }

    static void Integration()
    {
        foreach (var code in new byte[][] { [0xF3, 0xA5], [0x66, 0xF3, 0xAB], [0xF3, 0xAA], [0xA6], [0xA7], [0xAE], [0xAF], [0xAD], [0x50], [0x66, 0x50], [0x58], [0x66, 0x58] })
            Check(code, CreateCpu(0x87654321), env => Array.Fill(env.OneMegaMemory_, (byte)0x5a, 0x3000, 16));
        for (var type = 1; type < 3; type++)
            Check(Store(type), CreateCpu(0x12345678, 0x1000), env => { MapPages(env); BitConverter.GetBytes(3u).CopyTo(env.OneMegaMemory_, 0xA004); env.WatchLo = 0; env.WatchHi = 1; }, (_, env) => Equal(true, env.WatchTriggered, "watch at physical zero"), diagnostics: true);
        Reset(SlowEnv, [0xD9, 0xE8, 0xDD, 0x1B]);
        var cpu = CreateCpu(right: 0xfff03000);
        Equal(true, SlowStep(SlowEnv, cpu, null).IsSuccess, "FLD1");
        Equal(true, SlowStep(SlowEnv, cpu, null).IsSuccess, "FSTP");
        Equal(1.0, BitConverter.ToDouble(SlowEnv.OneMegaMemory_, 0x3000), "FPU alias store");
        cases++;
    }

    public static void Run()
    {
        Arithmetic();
        Memory(false);
        Writer.Flush();
        var baselineHash = Convert.ToHexString(SHA256.HashData(Results.ToArray()));
        Equal("E956129AF77B2263DAB0B3463C62E6310B7114FB93E814B91FA01BC22DA1140F", baselineHash, "pre-refactor results");
        Console.WriteLine($"Baseline-compatible cases: {cases}; SHA256={baselineHash}");
        Memory(true);
        Integration();
        Console.WriteLine($"Core cases passed: {cases}");
    }
}
