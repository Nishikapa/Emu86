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

    // IR(docs/adr/0002): ALU/MOV/TEST/CMP/INC/DEC/LEA/MOVZX/PUSH/POP/Jcc のレジスタ形式とメモリ形式について、IR の解釈器と
    // コンパイル済みデリゲートが通常コアと同じ CPU 状態(レジスタとフラグ)とメモリを作ること。1 命令あたりの時間も表示する。
    static void SemanticsIr()
    {
        var reference = SlowEnv;
        var interpreterEnv = FastEnv;
        using var compilerEnv = CreateEnvironment();
        byte[][] encodings =
        [
            [0x01, 0xD8], [0x66, 0x29, 0xC3], [0x02, 0xC3], [0x1A, 0xDC], [0x3B, 0xC3], [0x33, 0xD8], [0x66, 0x21, 0xC3],
            [0x05, 0x78, 0x56, 0x34, 0x12], [0x66, 0x2D, 0x01, 0x80], [0x14, 0xFF], [0x3C, 0x80],
            [0x40], [0x4B], [0x66, 0x48], [0x66, 0x43],
            [0x81, 0xF3, 0xFF, 0x00, 0xFF, 0x00], [0x83, 0xE8, 0x80], [0x80, 0xC4, 0x7F], [0x66, 0x83, 0xD0, 0xFF],
            [0x89, 0xC3], [0x8A, 0xE0], [0x66, 0x8B, 0xD8], [0x88, 0xDC],
            [0xB0, 0x5A], [0xB8, 0x78, 0x56, 0x34, 0x12], [0x66, 0xBB, 0x34, 0x12], [0xB7, 0x80],
            // メモリオペランド([ebx]、[ebx+disp8]、SIB、moffs、セグメントオーバーライド、16 ビットアドレッシング)
            [0x01, 0x03], [0x03, 0x03], [0x66, 0x29, 0x03], [0x3B, 0x03], [0x38, 0x03], [0x8B, 0x03], [0x89, 0x03], [0x8A, 0x43, 0x10], [0x88, 0x43, 0x10],
            [0x83, 0x03, 0x10], [0x81, 0x03, 0x78, 0x56, 0x34, 0x12], [0x80, 0x03, 0x7F], [0x85, 0x03], [0x84, 0x03], [0xA9, 0x01, 0x00, 0x00, 0x80],
            [0xFF, 0x03], [0xFF, 0x0B], [0xFE, 0x03], [0xC7, 0x03, 0x78, 0x56, 0x34, 0x12], [0xC6, 0x03, 0x5A], [0x66, 0xC7, 0x03, 0x34, 0x12],
            [0x8D, 0x44, 0x98, 0xF0], [0x66, 0x8D, 0x43, 0x10],
            [0xA1, 0x00, 0x30, 0x00, 0x00], [0xA3, 0x00, 0x30, 0x00, 0x00], [0xA0, 0x01, 0x30, 0x00, 0x00], [0xA2, 0x02, 0x30, 0x00, 0x00],
            [0x0F, 0xB6, 0x03], [0x0F, 0xBE, 0x03], [0x0F, 0xB7, 0xC3], [0x0F, 0xBF, 0x03], [0x66, 0x0F, 0xB6, 0xC3],
            [0x64, 0x8B, 0x03], [0x36, 0x8B, 0x03], [0x67, 0x8B, 0x07],
            // スタックと分岐
            [0x50], [0x53], [0x66, 0x50], [0x58], [0x66, 0x5B], [0x68, 0x78, 0x56, 0x34, 0x12], [0x6A, 0x80], [0x66, 0x68, 0x34, 0x12],
            [0x74, 0x10], [0x75, 0x10], [0x7C, 0xF0], [0x72, 0x7F], [0x0F, 0x87, 0x00, 0x01, 0x00, 0x00], [0x0F, 0x82, 0xF0, 0xFF, 0xFF, 0xFF], [0xEB, 0x05], [0xE9, 0xFF, 0xFF, 0xFF, 0xFF],
        ];
        uint[] values = [0, 1, 0x7F, 0x80, 0xFF, 0x7FFF, 0x8000, 0xFFFF, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0x12345678, 0x3000];
        static void Prepare(EmuEnvironment env, byte[] code)
        {
            Reset(env, code);
            for (var offset = 0; offset < 32; offset++) env.OneMegaMemory_[0x3000 + offset] = (byte)(0x80 + offset);
        }
        static string Memory(EmuEnvironment env) => Convert.ToHexString(SHA256.HashData(env.OneMegaMemory_));
        Prepare(reference, [0x01, 0xD8]);
        var add = Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true }));
        Equal(true, add.Statements.Count == 2
            && add.Statements[0] is SetRegStmt { Reg: 0, Type: 2, Value: AluExpr { Kind: 0, Left: RegExpr { Reg: 0, Type: 2 }, Right: RegExpr { Reg: 3, Type: 2 } } }
            && add.Statements[1] is AdvanceStmt { Length: 2 }, "ADD eax, ebx lowers to a register write of an ALU expression and an EIP advance");
        Prepare(reference, [0x75, 0x10]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) is { Statements: [IfStmt { Condition: 5, Then: SetEipStmt { Value: BranchTargetExpr { Length: 2, Displacement: 0x10 } }, Else: AdvanceStmt { Length: 2 } }] },
            "JNE lowers to a conditional EIP assignment");
        Prepare(reference, [0x0F, 0xAF, 0xC3]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) == null, "IMUL is outside the IR");
        Prepare(reference, [0xF3, 0xAB]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) == null, "REP is outside the IR");
        foreach (var code in encodings)
        {
            Prepare(reference, code);
            var block = Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true }));
            Equal(true, block != null, $"lowered {Convert.ToHexString(code)}");
            var run = Semantics.Compile(block);
            foreach (var left in values)
            foreach (var right in values)
            foreach (var carry in new uint[] { 0, 1 })
            {
                var flags = 0x202 | carry | (left == right ? 0x40u : 0) | (left > right ? 0x80u : 0);
                var expected = CreateCpu(left, right, flags);
                Prepare(reference, code);
                Equal(true, SlowStep(reference, expected, null).IsSuccess, $"reference {Convert.ToHexString(code)}");
                var expectedBytes = Convert.ToHexString(CpuBytes(expected));
                var expectedMemory = Memory(reference);
                var interpreted = CreateCpu(left, right, flags);
                Prepare(interpreterEnv, code);
                Semantics.Interpret(block, interpreterEnv, interpreted);
                Equal(expectedBytes, Convert.ToHexString(CpuBytes(interpreted)), $"interpreter {Convert.ToHexString(code)} {left:x} {right:x} cf={carry}");
                Equal(expectedMemory, Memory(interpreterEnv), $"interpreter memory {Convert.ToHexString(code)} {left:x} {right:x}");
                var compiled = CreateCpu(left, right, flags);
                Prepare(compilerEnv, code);
                run(compilerEnv, compiled);
                Equal(expectedBytes, Convert.ToHexString(CpuBytes(compiled)), $"compiled {Convert.ToHexString(code)} {left:x} {right:x} cf={carry}");
                Equal(expectedMemory, Memory(compilerEnv), $"compiled memory {Convert.ToHexString(code)} {left:x} {right:x}");
                cases++;
            }
        }
        // 1 命令あたりの時間(ADD eax, ebx): 高速コア、IR 解釈器、IR コンパイル済み。
        Prepare(reference, [0x01, 0xD8]);
        var block2 = Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true }));
        var compiledAdd = Semantics.Compile(block2);
        const int iterations = 5_000_000;
        var cpu = CreateCpu(1, 2);
        double Measure(Action step)
        {
            for (var warm = 0; warm < 100_000; warm++) { step(); cpu.eip = 0; }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var index = 0; index < iterations; index++) { step(); cpu.eip = 0; }
            return watch.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations;
        }
        var fast = Measure(() => FastStep(reference, cpu));
        var interpreterTime = Measure(() => Semantics.Interpret(block2, reference, cpu));
        var compiledTime = Measure(() => compiledAdd(reference, cpu));
        Console.WriteLine($"ADD eax, ebx ns/op: fast core {fast:F1}, IR interpreter {interpreterTime:F1}, IR compiled {compiledTime:F1}");
    }

    // 翻訳ブロック: 実行器の 1 回の Step が分岐までの命令列をまとめて完了し(Executed)、結果は通常コアと同じ。
    // 予算で途中で止まること、翻訳済みコードのあるページへの書き込み(自己書き換え。ブロック内からの書き込みも)で
    // ブロックが捨てられること、ブロック途中の #PF で完了した命令数と CPU が失敗した命令の先頭になることを確かめる。
    static void TranslationCacheExecution()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        using var mirror = new EmuEnvironment(memory: new byte[0x10000]);
        // ループ: add eax, ebx ; mov [data], eax ; inc ebx ; cmp ebx, 100 ; jne loop
        static byte[] Loop(uint data) => [0x01, 0xD8, 0xA3, (byte)data, (byte)(data >> 8), 0x00, 0x00, 0x43, 0x81, 0xFB, 0x64, 0x00, 0x00, 0x00, 0x75, 0xF0];
        static CPU Start() => new() { code32 = true, stack32 = true, eip = 0x100, esp = 0x8000, eflags = 2 };
        static string Hex(CPU cpu) => Convert.ToHexString(CpuBytes(cpu));
        Loop(0x3000).CopyTo(env.OneMegaMemory_, 0x100);
        Loop(0x3000).CopyTo(mirror.OneMegaMemory_, 0x100);
        var fast = new InstructionExecutor(true);
        env.Translations.Enabled = true;
        var slow = new InstructionExecutor(false);
        var cpu = Start();
        var expected = Start();
        // 1 回の Step が完了した命令数だけ通常コアを進めて比べる。
        long Advance(long instructions, string label)
        {
            Equal(true, fast.Step(env, cpu).IsSuccess, $"{label}: translated step");
            Equal(true, fast.Executed >= 1, $"{label}: a step completes at least one instruction");
            for (var index = 0; index < fast.Executed; index++) Equal(true, slow.Step(mirror, expected).IsSuccess, $"{label}: reference step");
            Equal(Hex(expected), Hex(cpu), $"{label}: state after {instructions + fast.Executed} instructions");
            return instructions + fast.Executed;
        }
        long count = 0;
        var blockSteps = 0;
        var largest = 0;
        while (count < 400)
        {
            count = Advance(count, "loop");
            if (fast.UsedTranslation) blockSteps++;
            largest = Math.Max(largest, fast.Executed);
        }
        Equal(5, largest, "the loop body runs as one block of five instructions");
        Equal(1L, env.Translations.Translated, "one block lowered");
        Equal(true, blockSteps >= 70, "most iterations ran as blocks");
        Equal(0x100u, cpu.eip, "block ends after the taken branch");
        Equal(true, env.OneMegaMemory_.AsSpan().SequenceEqual(mirror.OneMegaMemory_), "memory after the loop");
        // 予算 3: ブロックの 3 命令目で止まり、次の Step は途中(別のブロックの先頭)から続く。
        fast.Budget = 3;
        count = Advance(count, "budget");
        Equal((3, 0x108u), (fast.Executed, cpu.eip), "budget stops the block after three instructions");
        fast.Budget = int.MaxValue;
        while (cpu.eip != 0x100) count = Advance(count, "resume");
        // 自己書き換え(別の場所から): ループ先頭の ADD を SUB に書き換える。
        byte[] patch = [0xC6, 0x05, 0x00, 0x01, 0x00, 0x00, 0x29]; // mov byte [0x100], 0x29
        patch.CopyTo(env.OneMegaMemory_, 0x200);
        patch.CopyTo(mirror.OneMegaMemory_, 0x200);
        cpu.eip = 0x200; expected.eip = 0x200;
        count = Advance(count, "patch");
        Equal(1L, env.Translations.Invalidations, "code page invalidated by the write");
        var loweredBeforePatch = env.Translations.Translated; // ウォームアップ中に訪れた途中の番地(0x108 など)もブロックになりうる
        cpu.eip = 0x100; expected.eip = 0x100; cpu.ebx = 0; expected.ebx = 0;
        var patched = 0L;
        while (patched < 400) { count = Advance(count, "patched"); patched += fast.Executed; }
        Equal(true, env.Translations.Translated > loweredBeforePatch, "loop lowered again after the patch");
        // 自己書き換え(ブロック内から): 先頭の命令が次の命令のオペコードを AL で書き換え(INC ECX と DEC ECX が交互)、
        // 世代の変化でブロックがそこで止まり、次の Step が書き換え後の命令を実行する。
        byte[] self = [0x88, 0x05, 0x06, 0x03, 0x00, 0x00, 0x40, 0x34, 0x08, 0xEB, 0xF5]; // mov [0x306], al ; inc eax ; xor al, 8 ; jmp
        self.CopyTo(env.OneMegaMemory_, 0x300);
        self.CopyTo(mirror.OneMegaMemory_, 0x300);
        cpu.eip = 0x300; expected.eip = 0x300; cpu.eax = 0x41; expected.eax = 0x41; cpu.ecx = 0; expected.ecx = 0;
        var invalidationsBefore = env.Translations.Invalidations;
        for (var iteration = 0; iteration < 40; iteration++)
            while (true) { count = Advance(count, "self-modifying"); if (cpu.eip == 0x300) break; }
        Equal(true, env.Translations.Invalidations > invalidationsBefore + 5, "in-block writes invalidate the block");
        Equal(0u, cpu.ecx, "ecx returns to zero after 40 alternating iterations");
        // ページング有効で、データページを翻訳後に外す: ブロック 2 命令目の MOV [data] が #PF を投げ、
        // 完了数は 1(ADD)、CPU は MOV の先頭の状態。
        using var paged = new EmuEnvironment(memory: new byte[0x10000]);
        Loop(0x1800).CopyTo(paged.OneMegaMemory_, 0x100);
        MapPages(paged);
        var pagedCpu = Start();
        CPU._cr0.setter(pagedCpu)(0x80000001); CPU._cr3.setter(pagedCpu)(0x9000);
        EnvSyncPaging(paged, pagedCpu);
        var pagedExecutor = new InstructionExecutor(true);
        paged.Translations.Enabled = true;
        for (var step = 0; step < 60; step++) Equal(true, pagedExecutor.Step(paged, pagedCpu).IsSuccess, $"paged step {step}");
        Equal(true, pagedExecutor.UsedTranslation && pagedCpu.eip == 0x100, "paged loop runs as a block");
        BitConverter.GetBytes(0u).CopyTo(paged.OneMegaMemory_, 0xA004); // 線形 0x1000-0x1FFF を不在に
        paged.FlushTlb();
        var eaxBefore = pagedCpu.eax;
        var faulted = false;
        try { pagedExecutor.Step(paged, pagedCpu); }
        catch (PageFaultException fault) { faulted = true; Equal((0x1800u, 2u), (fault.Linear, fault.ErrorCode), "store fault from the block"); }
        Equal(true, faulted, "translated store faults");
        Equal((1, 0x102u, 0x100u), (pagedExecutor.Executed, pagedCpu.eip, pagedExecutor.LastEip), "one instruction completed before the fault");
        Equal(eaxBefore + pagedCpu.ebx, pagedCpu.eax, "ADD completed, MOV did not");
        cases++;
        // 実行器を通した 1 命令あたりの時間(5 命令のループ): 翻訳ブロック(コンパイル済み段階)と高速コアのみ。
        using var timing = new EmuEnvironment(memory: new byte[0x10000]);
        Loop(0x3000).CopyTo(timing.OneMegaMemory_, 0x100);
        double PerInstruction(bool translate)
        {
            timing.Translations.Enabled = translate;
            timing.Translations.InvalidateAll();
            var executor = new InstructionExecutor(true);
            var state = Start();
            long executed = 0;
            void Run(long target)
            {
                executed = 0;
                while (executed < target) { executor.Step(timing, state); executed += executor.Executed; if (state.eip == 0x110) state.eip = 0x100; }
            }
            Run(500_000);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Run(5_000_000);
            return watch.Elapsed.TotalMilliseconds * 1_000_000.0 / executed;
        }
        Console.WriteLine($"executor ns/instruction over a 5-instruction loop: fast core {PerInstruction(false):F1}, translated block {PerInstruction(true):F1}");
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
        SemanticsIr();
        TranslationCacheExecution();
        Integration();
        OperandTransfers();
        Console.WriteLine($"Core cases passed: {cases}");
    }
}
