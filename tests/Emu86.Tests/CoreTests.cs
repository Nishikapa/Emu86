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
            foreach (var moffs in new[] { false, true })
            {
                var env = fast ? FastEnv : SlowEnv;
                Reset(env, moffs ? [0xA3, 0xFF, 0x1F, 0x00, 0x00] : Store(2));
                MapPages(env, false);
                var cpu = CreateCpu(0x87654321, 0x1fff);
                try
                {
                    if (fast) FastStep(env, cpu);
                    else if (moffs) MovMoffsFromAcc(2, cpu.ebx)(env, cpu, null);
                    else SetMemOrRegData((true, cpu.ebx), cpu.eax.ToTypeData())(env, cpu, null);
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

    // ページテーブルウォークの挙動を固定する: 4K ページと大ページ(PSE 4MB / PAE 2MB)の変換、
    // Accessed/Dirty の更新規則(PDE は A のみ、PTE/大ページは A と D、PDPTE は更新しない)、
    // 各段の不在フォルトのエラーコード、CR0.WP と R/W(PDE と PTE の論理積)、TLB の読み書き別の充填。
    static void PageWalk()
    {
        foreach (var pae in new[] { false, true })
        {
            var env = SlowEnv;
            Reset(env, []);
            env.OneMegaMemory_[0x5005] = 0xAB; env.OneMegaMemory_[0x1234] = 0xCD; env.OneMegaMemory_[0x7000] = 0xEF;
            var cpu = CreateCpu();
            void Entry(int address, uint value, int size) { BitConverter.GetBytes(value).CopyTo(env.OneMegaMemory_, address); if (size == 8) BitConverter.GetBytes(0u).CopyTo(env.OneMegaMemory_, address + 4); }
            uint Raw(int address) => BitConverter.ToUInt32(env.OneMegaMemory_, address);
            int directory, table, readOnlyDirectory, large, missingDirectory, size;
            uint largeLinear, missingDirectoryLinear, readOnlyDirectoryLinear;
            if (!pae)
            {
                size = 4; directory = 0x9000; table = 0xA000; readOnlyDirectory = 0xB000;
                Entry(directory + 0 * 4, 0xA003, size);          // lin 0x000000-0x3FFFFF -> PT 0xA000
                Entry(directory + 1 * 4, 0x00000083, size);      // lin 0x400000-0x7FFFFF -> 4MB ページ(物理 0)
                Entry(directory + 2 * 4, 0xB001, size);          // lin 0x800000-: 読み取り専用 PDE -> PT 0xB000
                large = directory + 1 * 4; largeLinear = 0x401234; missingDirectoryLinear = 0xC00000; readOnlyDirectoryLinear = 0x800000; missingDirectory = directory + 3 * 4;
                env.PaeOn = false; env.Cr3Base = 0x9000;
            }
            else
            {
                size = 8; directory = 0xD000; table = 0xE000; readOnlyDirectory = 0xB000;
                Entry(0xC000, 0xD001, size);                     // PDPTE[0] -> PD 0xD000(PDPTE は A/D を更新しない)
                Entry(directory + 0 * 8, 0xE003, size);          // lin 0x000000-0x1FFFFF -> PT 0xE000
                Entry(directory + 1 * 8, 0x00000083, size);      // lin 0x200000-0x3FFFFF -> 2MB ページ(物理 0)
                Entry(directory + 2 * 8, 0xB001, size);          // lin 0x400000-: 読み取り専用 PDE -> PT 0xB000
                large = directory + 1 * 8; largeLinear = 0x201234; missingDirectoryLinear = 0x600000; readOnlyDirectoryLinear = 0x400000; missingDirectory = directory + 3 * 8;
                env.PaeOn = true; env.Cr3Base = 0xC000;
            }
            Entry(table + 2 * size, 0x5003, size);               // lin 0x2000 -> 0x5000(R/W)
            Entry(table + 3 * size, 0x6001, size);               // lin 0x3000 -> 0x6000(読み取り専用)
            Entry(readOnlyDirectory + 0 * size, 0x7003, size);   // 読み取り専用 PDE の下の R/W PTE -> 0x7000
            env.PagingOn = true; env.WpOn = false; env.FlushTlb();
            var label = pae ? "PAE" : "legacy";

            void Write(uint linear, byte value) => SetMemOrRegData((true, linear), value.ToTypeData())(env, cpu, null);
            (uint linear, uint code)? Fault(Action action)
            {
                try { action(); return null; }
                catch (PageFaultException fault) { return (fault.Linear, fault.ErrorCode); }
            }

            Equal((byte)0xAB, EnvGetMemoryData8(env, 0x2005), $"{label} 4K read");
            Equal(0x20u, Raw(directory) & 0x60, $"{label} read sets Accessed on the directory entry only");
            Equal(0x20u, Raw(table + 2 * size) & 0x60, $"{label} read sets Accessed but not Dirty on the page entry");
            Equal(0x80000002u, env.TlbTagR[2], $"{label} read fills the read TLB");
            Equal(0u, env.TlbTagW[2], $"{label} read leaves the write TLB empty");
            Write(0x2006, 0xCD);
            Equal((byte)0xCD, env.OneMegaMemory_[0x5006], $"{label} 4K write lands in the mapped page");
            Equal(0x60u, Raw(table + 2 * size) & 0x60, $"{label} write sets Dirty on the page entry");
            Equal(0x20u, Raw(directory) & 0x60, $"{label} write leaves the directory entry without Dirty");
            Equal(0x80000002u, env.TlbTagW[2], $"{label} write fills the write TLB");
            Write(0x3000, 0x11);
            Equal((byte)0x11, env.OneMegaMemory_[0x6000], $"{label} ring0 writes a read-only page while CR0.WP=0");
            Equal(0x60u, Raw(table + 3 * size) & 0x60, $"{label} WP=0 write marks the read-only page dirty");
            env.WpOn = true; env.FlushTlb();
            Equal((0x3001u, 3u), Fault(() => Write(0x3001, 0x22)), $"{label} WP=1 write to a read-only page faults with protection+write");
            Equal((byte)0x11, EnvGetMemoryData8(env, 0x3000), $"{label} read-only page still readable");
            Equal((readOnlyDirectoryLinear, 3u), Fault(() => Write(readOnlyDirectoryLinear, 0x33)), $"{label} read-only PDE overrides a R/W PTE");
            Equal((byte)0xEF, EnvGetMemoryData8(env, readOnlyDirectoryLinear), $"{label} read through a read-only PDE");
            Equal(0x20u, Raw(readOnlyDirectory) & 0x60, $"{label} read-only PDE path sets Accessed on the page entry");
            Equal((0x4000u, 0u), Fault(() => EnvGetMemoryData8(env, 0x4000)), $"{label} not-present page entry on read");
            Equal((0x4000u, 2u), Fault(() => Write(0x4000, 0x44)), $"{label} not-present page entry on write");
            Equal((missingDirectoryLinear, 0u), Fault(() => EnvGetMemoryData8(env, missingDirectoryLinear)), $"{label} not-present directory entry");
            Equal(0u, Raw(missingDirectory), $"{label} missing directory entry untouched");
            env.WpOn = false; env.FlushTlb();
            Equal((byte)0xCD, EnvGetMemoryData8(env, largeLinear), $"{label} large page read");
            Equal(0x20u, Raw(large) & 0x60, $"{label} large page read sets Accessed only");
            Write(largeLinear + 1, 0x55);
            Equal((byte)0x55, env.OneMegaMemory_[0x1235], $"{label} large page write");
            Equal(0x60u, Raw(large) & 0x60, $"{label} large page write sets Dirty");
            env.WpOn = true; env.FlushTlb();
            Entry(large, 0x00000081 | 0x60, size); // 読み取り専用の大ページ
            Equal((largeLinear, 3u), Fault(() => Write(largeLinear, 0x66)), $"{label} read-only large page faults on write with WP=1");
            if (pae)
            {
                Equal((0x40000000u, 0u), Fault(() => EnvGetMemoryData8(env, 0x40000000)), "PAE not-present PDPTE");
                Equal(0xD001u, Raw(0xC000), "PAE PDPTE never gets Accessed/Dirty");
            }
            cases++;
        }
        Reset(SlowEnv, []);
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

    static void OperandTransfers()
    {
        for (var type = 0; type < 3; type++)
        {
            for (var source = 0; source < 8; source++)
            for (var destination = 0; destination < 8; destination++)
            foreach (var direction in new[] { 0, 2 })
            {
                var cpu = CreateCpu(0x87654321, 0xFEDCBA98);
                cpu.ecx = 0x11223344; cpu.edx = 0x55667788;
                cpu.esp = 0x10203040; cpu.ebp = 0x50607080;
                cpu.esi = 0x90A0B0C0; cpu.edi = 0xD0E0F001;
                var code = new List<byte>();
                if (type == 1) code.Add(0x66);
                code.Add((byte)((type == 0 ? 0x88 : 0x89) + direction));
                code.Add((byte)(0xC0 | (source << 3) | destination));
                Check(code.ToArray(), cpu);
            }
            foreach (var address in new uint[] { 0x3000, 0x3FFF, 0xFFFF, 0x10000, 0xFFF03000, 0xFFFFFFFF, 0x1FFF })
            foreach (var logWrites in new[] { false, true })
            {
                var code = new List<byte>();
                if (type == 1) code.Add(0x66);
                code.Add((byte)(type == 0 ? 0xA2 : 0xA3));
                code.AddRange(BitConverter.GetBytes(address));
                Check(code.ToArray(), CreateCpu(0x87654321), env =>
                {
                    if (address == 0x1FFF) MapPages(env);
                    env.WriteLog = logWrites ? [] : null;
                    env.WLogLo = 0; env.WLogHi = 0x10000;
                    env.WatchLo = 0x5FFF; env.WatchHi = 0x6000;
                }, diagnostics: true);
            }
        }
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
        PageWalk();
        Integration();
        OperandTransfers();
        Console.WriteLine($"Core cases passed: {cases}");
    }
}
