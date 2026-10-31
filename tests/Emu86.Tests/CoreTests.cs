using Emu86;
using System.Security.Cryptography;
using static Emu86.Conversions;
using static Emu86.DataAccess;
using static Emu86.Mmu;
using static Emu86.Decoder;
using static Assert;

static class CoreTests
{
    static readonly State<Unit> SlowStep = Instructions.Execute2;
    static readonly EmuEnvironment SlowEnv = CreateEnvironment();
    // 比較対象: IR 解釈器で 1 命令ずつ(docs/adr/0003 で高速コアを置き換えた)。
    static readonly EmuEnvironment FastEnv = CreateEnvironment(ir: true);
    static readonly InstructionExecutor IrStep = new(true);
    static readonly MemoryStream Results = new();
    static readonly BinaryWriter Writer = new(Results);
    static int cases;

    static EmuEnvironment CreateEnvironment(bool ir = false)
    {
        var env = new EmuEnvironment(memory: new byte[0x10000]);
        if (ir) Ir.SingleStep(env);
        return env;
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
        Array.Clear(env.Ram);
        code.CopyTo(env.Ram, 0);
        env.Translations.InvalidateAll(); // コードを直接書き換えたので、前のケースの IR を捨てる
        env.Paging.Enabled = false;
        env.Paging.Pae = false;
        env.Paging.WriteProtect = false;
        env.Paging.Flush();
        env.Diagnostics = null;
    }

    static byte[] CpuBytes(CPU cpu)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        cpu.WriteTo(writer);
        return stream.ToArray();
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
        Equal(true, IrStep.Step(FastEnv, fastCpu).IsSuccess, "IR success");
        Equal(true, IrStep.UsedTranslation, $"IR used for {Convert.ToHexString(code)}");
        Equal(Convert.ToHexString(CpuBytes(slowCpu)), Convert.ToHexString(CpuBytes(fastCpu)), $"CPU {Convert.ToHexString(code)}");
        Equal(true, SlowEnv.Ram.AsSpan().SequenceEqual(FastEnv.Ram), $"memory {Convert.ToHexString(code)}");
        if (diagnostics)
        {
            Equal(SlowEnv.Diagnostics?.WatchTriggered, FastEnv.Diagnostics?.WatchTriggered, "watchpoint");
            Equal(string.Join("|", SlowEnv.Diagnostics?.WriteLog ?? []), string.Join("|", FastEnv.Diagnostics?.WriteLog ?? []), "write log");
        }
        expected?.Invoke(slowCpu, SlowEnv);
        expected?.Invoke(fastCpu, FastEnv);
        Writer.Write(CpuBytes(slowCpu));
        Writer.Write(SHA256.HashData(SlowEnv.Ram));
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
        BitConverter.GetBytes(0xA003u).CopyTo(env.Ram, 0x9000);
        BitConverter.GetBytes(0x0003u).CopyTo(env.Ram, 0xA000);
        BitConverter.GetBytes(0x5003u).CopyTo(env.Ram, 0xA004);
        if (secondPresent) BitConverter.GetBytes(0x7003u).CopyTo(env.Ram, 0xA008);
        env.Paging.Cr3Base = 0x9000;
        env.Paging.Enabled = true;
        env.Paging.WriteProtect = true;
    }

    static byte[] Store(int type) => type == 0 ? [0x88, 0x03] : type == 1 ? [0x66, 0x89, 0x03] : [0x89, 0x03];

    static void Memory(bool extended)
    {
        for (var type = 0; type < 3; type++)
        {
            foreach (var address in new uint[] { 0x3000, 0x3fff, 0xffff, 0x10000 })
                Check(Store(type), CreateCpu(0x87654321, address));
            Check(Store(type), CreateCpu(0x87654321, 0x1fff), env => MapPages(env), (_, env) => Equal((byte)0x21, env.Ram[0x5fff], "paged write"));
            if (extended)
            {
                foreach (var address in new uint[] { 0xfff03000, 0xffffffff })
                    Check(Store(type), CreateCpu(0x87654321, address));
                Check(Store(type), CreateCpu(0x87654321, 0x1fff), env => { MapPages(env); env.Diagnostics = new() { WatchLo = 0x5fff, WatchHi = 0x6000, WriteLogLo = 0x5000, WriteLogHi = 0x8000, WriteLog = [] }; }, (_, env) => Equal(true, env.Diagnostics.WatchTriggered, "paged watch"), diagnostics: true);
                Check(Store(type), CreateCpu(0x87654321, 0x1000), env => { MapPages(env); env.Diagnostics = new() { WatchLo = 0x5000, WatchHi = 0x5001, WriteLogLo = 0x5000, WriteLogHi = 0x5001, WriteLog = [] }; }, diagnostics: true);
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
                    if (fast) IrStep.Step(env, cpu);
                    else if (moffs) MovMoffsFromAcc(2, cpu.ebx)(env, cpu, null);
                    else SetMemOrRegData((true, cpu.ebx), cpu.eax.ToTypeData())(env, cpu, null);
                    throw new Exception("expected page fault");
                }
                catch (PageFaultException fault)
                {
                    Equal(0x2000u, fault.Linear, "page fault address");
                    Equal(2u, fault.ErrorCode, "page fault code");
                    Equal((byte)0, env.Ram[0x5fff], "no partial write before the fault (both pages are checked first)");
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
            env.Ram[0x5005] = 0xAB; env.Ram[0x1234] = 0xCD; env.Ram[0x7000] = 0xEF;
            var cpu = CreateCpu();
            void Entry(int address, uint value, int size) { BitConverter.GetBytes(value).CopyTo(env.Ram, address); if (size == 8) BitConverter.GetBytes(0u).CopyTo(env.Ram, address + 4); }
            uint Raw(int address) => BitConverter.ToUInt32(env.Ram, address);
            int directory, table, readOnlyDirectory, large, missingDirectory, size;
            uint largeLinear, missingDirectoryLinear, readOnlyDirectoryLinear;
            if (!pae)
            {
                size = 4; directory = 0x9000; table = 0xA000; readOnlyDirectory = 0xB000;
                Entry(directory + 0 * 4, 0xA003, size);          // lin 0x000000-0x3FFFFF -> PT 0xA000
                Entry(directory + 1 * 4, 0x00000083, size);      // lin 0x400000-0x7FFFFF -> 4MB ページ(物理 0)
                Entry(directory + 2 * 4, 0xB001, size);          // lin 0x800000-: 読み取り専用 PDE -> PT 0xB000
                large = directory + 1 * 4; largeLinear = 0x401234; missingDirectoryLinear = 0xC00000; readOnlyDirectoryLinear = 0x800000; missingDirectory = directory + 3 * 4;
                env.Paging.Pae = false; env.Paging.Cr3Base = 0x9000;
            }
            else
            {
                size = 8; directory = 0xD000; table = 0xE000; readOnlyDirectory = 0xB000;
                Entry(0xC000, 0xD001, size);                     // PDPTE[0] -> PD 0xD000(PDPTE は A/D を更新しない)
                Entry(directory + 0 * 8, 0xE003, size);          // lin 0x000000-0x1FFFFF -> PT 0xE000
                Entry(directory + 1 * 8, 0x00000083, size);      // lin 0x200000-0x3FFFFF -> 2MB ページ(物理 0)
                Entry(directory + 2 * 8, 0xB001, size);          // lin 0x400000-: 読み取り専用 PDE -> PT 0xB000
                large = directory + 1 * 8; largeLinear = 0x201234; missingDirectoryLinear = 0x600000; readOnlyDirectoryLinear = 0x400000; missingDirectory = directory + 3 * 8;
                env.Paging.Pae = true; env.Paging.Cr3Base = 0xC000;
            }
            Entry(table + 2 * size, 0x5003, size);               // lin 0x2000 -> 0x5000(R/W)
            Entry(table + 3 * size, 0x6001, size);               // lin 0x3000 -> 0x6000(読み取り専用)
            Entry(readOnlyDirectory + 0 * size, 0x7003, size);   // 読み取り専用 PDE の下の R/W PTE -> 0x7000
            env.Paging.Enabled = true; env.Paging.WriteProtect = false; env.Paging.Flush();
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
            Equal(0x80000002u, env.Paging.TagR[2], $"{label} read fills the read TLB");
            Equal(0u, env.Paging.TagW[2], $"{label} read leaves the write TLB empty");
            Write(0x2006, 0xCD);
            Equal((byte)0xCD, env.Ram[0x5006], $"{label} 4K write lands in the mapped page");
            Equal(0x60u, Raw(table + 2 * size) & 0x60, $"{label} write sets Dirty on the page entry");
            Equal(0x20u, Raw(directory) & 0x60, $"{label} write leaves the directory entry without Dirty");
            Equal(0x80000002u, env.Paging.TagW[2], $"{label} write fills the write TLB");
            Write(0x3000, 0x11);
            Equal((byte)0x11, env.Ram[0x6000], $"{label} ring0 writes a read-only page while CR0.WP=0");
            Equal(0x60u, Raw(table + 3 * size) & 0x60, $"{label} WP=0 write marks the read-only page dirty");
            env.Paging.WriteProtect = true; env.Paging.Flush();
            Equal((0x3001u, 3u), Fault(() => Write(0x3001, 0x22)), $"{label} WP=1 write to a read-only page faults with protection+write");
            Equal((byte)0x11, EnvGetMemoryData8(env, 0x3000), $"{label} read-only page still readable");
            Equal((readOnlyDirectoryLinear, 3u), Fault(() => Write(readOnlyDirectoryLinear, 0x33)), $"{label} read-only PDE overrides a R/W PTE");
            Equal((byte)0xEF, EnvGetMemoryData8(env, readOnlyDirectoryLinear), $"{label} read through a read-only PDE");
            Equal(0x20u, Raw(readOnlyDirectory) & 0x60, $"{label} read-only PDE path sets Accessed on the page entry");
            Equal((0x4000u, 0u), Fault(() => EnvGetMemoryData8(env, 0x4000)), $"{label} not-present page entry on read");
            Equal((0x4000u, 2u), Fault(() => Write(0x4000, 0x44)), $"{label} not-present page entry on write");
            Equal((missingDirectoryLinear, 0u), Fault(() => EnvGetMemoryData8(env, missingDirectoryLinear)), $"{label} not-present directory entry");
            Equal(0u, Raw(missingDirectory), $"{label} missing directory entry untouched");
            env.Paging.WriteProtect = false; env.Paging.Flush();
            Equal((byte)0xCD, EnvGetMemoryData8(env, largeLinear), $"{label} large page read");
            Equal(0x20u, Raw(large) & 0x60, $"{label} large page read sets Accessed only");
            Write(largeLinear + 1, 0x55);
            Equal((byte)0x55, env.Ram[0x1235], $"{label} large page write");
            Equal(0x60u, Raw(large) & 0x60, $"{label} large page write sets Dirty");
            env.Paging.WriteProtect = true; env.Paging.Flush();
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

    // IR(docs/adr/0002): ALU/MOV/TEST/CMP/INC/DEC/LEA/MOVZX/PUSH/POP/Jcc、シフト/ローテート、XCHG、NOT/NEG/MUL/IMUL、SETcc、
    // CBW/CWD 系、フラグ命令、CALL/RET/JMP 間接/LEAVE のレジスタ形式とメモリ形式について、IR の解釈器とコンパイル済みデリゲートが
    // 通常コアと同じ CPU 状態(レジスタとフラグ)とメモリを作ること。1 命令あたりの時間も表示する。
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
            // シフト/ローテート(即値、1、CL、0 と 32 のカウント、8/16 ビット、メモリ)
            [0xC1, 0xE0, 0x04], [0xC1, 0xE8, 0x01], [0xC1, 0xF8, 0x1F], [0xC1, 0xC0, 0x08], [0xC1, 0xC8, 0x21], [0xC1, 0xD0, 0x01], [0xC1, 0xD8, 0x01], [0xC1, 0xE0, 0x00], [0xC1, 0xE0, 0x20],
            [0xD1, 0xE3], [0xD1, 0xFB], [0xD3, 0xE8], [0xD3, 0xC3], [0xD3, 0xD3], [0xC0, 0xE8, 0x03], [0xC0, 0xC4, 0x01], [0x66, 0xC1, 0xE8, 0x04], [0x66, 0xD3, 0xC0],
            [0xD1, 0x2B], [0xD3, 0x23], [0xC1, 0x0B, 0x05], [0xD0, 0x0B], [0xC0, 0x1B, 0x01],
            // XCHG、CBW/CWDE/CWD/CDQ、NOP
            [0x87, 0xD8], [0x86, 0xE0], [0x87, 0x03], [0x86, 0x03], [0x93], [0x66, 0x93], [0x91], [0x94], [0x98], [0x66, 0x98], [0x99], [0x66, 0x99], [0x90],
            // Group3: TEST imm、NOT、NEG、MUL、IMUL(8/16/32 ビット、メモリ)
            [0xF7, 0xC0, 0xFF, 0x00, 0xFF, 0x00], [0xF6, 0xC4, 0x80], [0xF7, 0x03, 0x01, 0x00, 0x00, 0x80], [0xF7, 0xD0], [0xF7, 0xD8], [0xF6, 0xDB], [0xF7, 0x13], [0xF7, 0x1B],
            [0xF7, 0xE3], [0xF7, 0xEB], [0xF6, 0xE3], [0xF6, 0xEB], [0x66, 0xF7, 0xE3], [0x66, 0xF7, 0xEB], [0xF7, 0x23], [0xF7, 0x2B],
            // IMUL 2/3 オペランド
            [0x0F, 0xAF, 0xC3], [0x66, 0x0F, 0xAF, 0xC3], [0x0F, 0xAF, 0x03], [0x69, 0xC3, 0x78, 0x56, 0x34, 0x12], [0x6B, 0xC3, 0x80], [0x66, 0x6B, 0xC3, 0x7F], [0x66, 0x69, 0xC3, 0x00, 0x80],
            // SETcc、フラグ命令
            [0x0F, 0x94, 0xC0], [0x0F, 0x9F, 0xC4], [0x0F, 0x92, 0x03], [0x0F, 0x9C, 0xC3], [0xF5], [0xF8], [0xF9], [0xFA], [0xFB], [0xFC], [0xFD],
            // CALL/JMP(相対・間接)、RET、LEAVE、PUSH r/m
            [0xE8, 0x10, 0x00, 0x00, 0x00], [0x66, 0xE8, 0x10, 0x00], [0xFF, 0xD3], [0xFF, 0xD4], [0xFF, 0x13], [0x66, 0xFF, 0xD3], [0xFF, 0xE3], [0xFF, 0x23], [0x66, 0xFF, 0xE3],
            [0xC3], [0xC2, 0x08, 0x00], [0x66, 0xC3], [0x66, 0xC2, 0x02, 0x00], [0xC9], [0x66, 0xC9], [0xFF, 0x33], [0xFF, 0xF3], [0xFF, 0x34, 0x24], [0xFF, 0xF4],
            // docs/adr/0003 段階 1: PUSHF/POPF、SAHF/LAHF、PUSHA/POPA、POP r/m、LOOP/JCXZ、HLT、XLAT、BSWAP
            [0x9C], [0x66, 0x9C], [0x9D], [0x66, 0x9D], [0x9E], [0x9F], [0x60], [0x66, 0x60], [0x61], [0x66, 0x61],
            [0x8F, 0xC3], [0x8F, 0x03], [0x66, 0x8F, 0xC3], [0x8F, 0x04, 0x24], [0xE2, 0x10], [0xE1, 0x10], [0xE0, 0xF0], [0xE3, 0x10], [0xF4], [0xD7],
            [0x0F, 0xC8], [0x0F, 0xCB],
            // CMOVcc、XADD、CMPXCHG
            [0x0F, 0x44, 0xC3], [0x0F, 0x45, 0x03], [0x66, 0x0F, 0x4C, 0xC3], [0x0F, 0x4F, 0x03],
            [0x0F, 0xC1, 0xD8], [0x0F, 0xC1, 0xC0], [0x0F, 0xC1, 0x03], [0x0F, 0xC0, 0xE0], [0x66, 0x0F, 0xC1, 0x03],
            [0x0F, 0xB1, 0xD8], [0x0F, 0xB1, 0x03], [0x0F, 0xB0, 0xE0], [0x66, 0x0F, 0xB1, 0xC3], [0x0F, 0xB1, 0xC3],
            // BT/BTS/BTR/BTC(レジスタ、即値、メモリのビット列アドレッシング)、BSF/BSR、SHLD/SHRD
            [0x0F, 0xA3, 0xD8], [0x0F, 0xAB, 0xD8], [0x0F, 0xB3, 0xC3], [0x0F, 0xBB, 0xC3], [0x66, 0x0F, 0xA3, 0xD8], [0x0F, 0xA3, 0x03], [0x0F, 0xAB, 0x03], [0x0F, 0xBB, 0x03],
            [0x0F, 0xBA, 0xE0, 0x05], [0x0F, 0xBA, 0xE8, 0x1F], [0x0F, 0xBA, 0xF0, 0x21], [0x0F, 0xBA, 0xF8, 0x00], [0x0F, 0xBA, 0x2B, 0x21], [0x0F, 0xBA, 0x23, 0x07], [0x66, 0x0F, 0xBA, 0xE0, 0x11],
            [0x0F, 0xBC, 0xC3], [0x0F, 0xBD, 0xC3], [0x0F, 0xBC, 0x03], [0x0F, 0xBD, 0x03], [0x66, 0x0F, 0xBC, 0xC3], [0x66, 0x0F, 0xBD, 0x03],
            [0x0F, 0xA4, 0xD8, 0x04], [0x0F, 0xAC, 0xD8, 0x01], [0x0F, 0xA5, 0x03], [0x0F, 0xAD, 0xC3], [0x66, 0x0F, 0xA4, 0xD8, 0x04], [0x0F, 0xA4, 0xD8, 0x00], [0x0F, 0xA4, 0xD8, 0x20], [0x66, 0x0F, 0xAD, 0x03],
            // BCD
            [0x37], [0x3F], [0x27], [0x2F], [0xD4, 0x0A], [0xD5, 0x0A], [0xD4, 0x07], [0xD5, 0xFF],
            // 文字列命令(単発、REP/REPNE、16 ビット幅、16 ビットアドレッシング、セグメントオーバーライド、I/O)と REP の無視(PAUSE、非文字列命令)
            [0xA4], [0xA5], [0x66, 0xA5], [0xAA], [0xAB], [0xAC], [0xAD], [0xA6], [0xA7], [0xAE], [0xAF], [0x6C], [0x6D], [0x6E], [0x6F],
            [0xF3, 0xA4], [0xF3, 0xA5], [0x66, 0xF3, 0xAB], [0xF3, 0xAD], [0xF3, 0xA6], [0xF3, 0xA7], [0xF2, 0xAE], [0xF2, 0xAF], [0xF3, 0x6D], [0xF3, 0x6F],
            [0x67, 0xF3, 0xA4], [0x67, 0xAB], [0x26, 0xF3, 0xA5], [0x64, 0xA4], [0xF3, 0x90], [0xF3, 0x01, 0xD8], [0xF2, 0x0F, 0xBC, 0xC3],
            // docs/adr/0003 段階 2: セグメントレジスタ(MOV/PUSH/POP/LES 系)、IN/OUT、far JMP/CALL/RET、INT/INTO/IRET、ENTER、RDTSC/CPUID/FWAIT
            [0x8E, 0xD8], [0x8E, 0xC3], [0x8E, 0x1B], [0x8E, 0xE0], [0x8E, 0xE8], [0x8E, 0xD0], [0x66, 0x8E, 0xD8],
            [0x8C, 0xD8], [0x8C, 0x03], [0x66, 0x8C, 0xC8], [0x8C, 0xE3],
            [0x06], [0x07], [0x0E], [0x16], [0x17], [0x1E], [0x1F], [0x66, 0x06], [0x66, 0x1F], [0x0F, 0xA0], [0x0F, 0xA1], [0x0F, 0xA8], [0x0F, 0xA9], [0x66, 0x0F, 0xA1],
            [0xC4, 0x03], [0xC5, 0x03], [0x66, 0xC5, 0x03], [0x0F, 0xB2, 0x03], [0x0F, 0xB4, 0x03], [0x0F, 0xB5, 0x03],
            [0xE4, 0x60], [0xE5, 0x60], [0xEC], [0xED], [0x66, 0xED], [0xE6, 0x80], [0xE7, 0x80], [0xEE], [0xEF], [0x66, 0xEF],
            [0xEA, 0x00, 0x10, 0x00, 0x00, 0x00, 0x10], [0x66, 0xEA, 0x00, 0x10, 0x00, 0x10], [0x9A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x10], [0x66, 0x9A, 0x00, 0x10, 0x00, 0x10],
            [0xFF, 0x2B], [0xFF, 0x1B], [0x66, 0xFF, 0x2B], [0x66, 0xFF, 0x1B], [0xCB], [0xCA, 0x08, 0x00], [0x66, 0xCB], [0x66, 0xCA, 0x04, 0x00],
            [0xCD, 0x21], [0xCC], [0xCE], [0xCF], [0x66, 0xCF], [0xC8, 0x10, 0x00, 0x00], [0xC8, 0x08, 0x00, 0x01], [0xC8, 0x04, 0x00, 0x02],
            [0x0F, 0x31], [0x0F, 0xA2], [0x9B],
        ];
        uint[] values = [0, 1, 0x7F, 0x80, 0xFF, 0x7FFF, 0x8000, 0xFFFF, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0x12345678, 0x3000];
        static void Prepare(EmuEnvironment env, byte[] code)
        {
            Reset(env, code);
            for (var offset = 0; offset < 32; offset++) env.Ram[0x3000 + offset] = (byte)(0x80 + offset);
        }
        static string Memory(EmuEnvironment env) => Convert.ToHexString(SHA256.HashData(env.Ram));
        Prepare(reference, [0x01, 0xD8]);
        var add = Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true }));
        Equal(true, add.Statements.Count == 2
            && add.Statements[0] is SetRegStmt { Reg: 0, Type: 2, Value: AluExpr { Kind: 0, Left: RegExpr { Reg: 0, Type: 2 }, Right: RegExpr { Reg: 3, Type: 2 } } }
            && add.Statements[1] is AdvanceStmt { Length: 2 }, "ADD eax, ebx lowers to a register write of an ALU expression and an EIP advance");
        Prepare(reference, [0x75, 0x10]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) is { Statements: [IfStmt { Condition: 5, Then: SetEipStmt { Value: BranchTargetExpr { Length: 2, Displacement: 0x10 } }, Else: AdvanceStmt { Length: 2 } }] },
            "JNE lowers to a conditional EIP assignment");
        Prepare(reference, [0xF7, 0xF3]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) is { Statements: [DivideStmt { Type: 2, Signed: false, Divisor: RegExpr { Reg: 3 } }, AdvanceStmt { Length: 2 }] }, "DIV lowers to a divide statement");
        Prepare(reference, [0xD4, 0x00]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) == null, "AAM 0 stays outside the IR (#DE in the reference core)");
        Prepare(reference, [0xF4]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) is { Statements: [HaltStmt, AdvanceStmt] } && Semantics.StopsChain(DecodeInstruction(reference, new CPU { code32 = true })), "HLT lowers and stops chaining");
        Prepare(reference, [0xFF, 0xD3]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) is { Statements: [LetStmt { Slot: 0, Value: RegExpr { Reg: 3, Type: 2 } }, PushStmt { Type: 2, Value: ReturnAddressExpr { Length: 2 } }, SetEipStmt { Value: NearTargetExpr { Value: TempExpr { Slot: 0 }, Operand32: true } }] },
            "CALL ebx binds the target before the push and sets EIP from the bound value");
        Prepare(reference, [0xF3, 0xAB]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) is { Statements: [StringStmt { Op: 0xAB, Size: 4, Address32: true, Repeat: 1 }, AdvanceStmt { Length: 2 }] }, "REP STOSD lowers to a repeated string statement");
        Prepare(reference, [0xF2, 0x66, 0xAF]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) is { Statements: [StringStmt { Op: 0xAF, Size: 2, Repeat: 2 }, AdvanceStmt { Length: 3 }] }, "REPNE SCASW lowers with the 16-bit width");
        Prepare(reference, [0xF3, 0x6D]);
        Equal(true, Semantics.EndsBlock(DecodeInstruction(reference, new CPU { code32 = true })), "REP INSD ends a block (port input can raise an interrupt)");
        Prepare(reference, [0x8E, 0xC8]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) == null, "MOV CS stays outside the IR");
        Prepare(reference, [0xC4, 0xC3]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) == null, "LES with a register operand stays outside the IR (#UD in the reference core)");
        Prepare(reference, [0xCD, 0x21]);
        Equal(true, Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true })) is { Statements: [StateStmt { Name: "INT" }] } && Semantics.EndsBlock(DecodeInstruction(reference, new CPU { code32 = true })), "INT n runs the reference core's delivery and ends the block");
        Prepare(reference, [0xEC]);
        Equal(true, Semantics.StopsChain(DecodeInstruction(reference, new CPU { code32 = true })), "IN al, dx stops chaining");
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
        // DIV/IDIV(0 除算は両方とも例外になるので、除数が 0 でない組だけ)。メモリ形式は [ebx] = 0x3000 の 0x83828180 で割る。
        byte[][] divisions = [[0xF7, 0xF3], [0xF7, 0xFB], [0xF6, 0xF3], [0xF6, 0xFB], [0x66, 0xF7, 0xF3], [0x66, 0xF7, 0xFB], [0xF7, 0x33], [0xF7, 0x3B], [0x66, 0xF7, 0x3B]];
        foreach (var code in divisions)
        {
            Prepare(reference, code);
            var block = Semantics.Lower(DecodeInstruction(reference, new CPU { code32 = true }));
            var run = Semantics.Compile(block);
            var memoryForm = code[^1] is 0x33 or 0x3B;
            var divisorMask = Mask(code[0] == 0xF6 ? 0 : code[0] == 0x66 ? 1 : 2);
            foreach (var left in values)
            foreach (var right in values.Where(value => memoryForm ? value == 0x3000 : (value & divisorMask) != 0))
            {
                var expected = CreateCpu(left, right);
                Prepare(reference, code);
                Equal(true, SlowStep(reference, expected, null).IsSuccess, $"reference {Convert.ToHexString(code)}");
                var interpreted = CreateCpu(left, right);
                Prepare(interpreterEnv, code);
                Semantics.Interpret(block, interpreterEnv, interpreted);
                Equal(Convert.ToHexString(CpuBytes(expected)), Convert.ToHexString(CpuBytes(interpreted)), $"interpreter {Convert.ToHexString(code)} {left:x} {right:x}");
                var compiled = CreateCpu(left, right);
                Prepare(compilerEnv, code);
                run(compilerEnv, compiled);
                Equal(Convert.ToHexString(CpuBytes(expected)), Convert.ToHexString(CpuBytes(compiled)), $"compiled {Convert.ToHexString(code)} {left:x} {right:x}");
                cases++;
            }
        }
        // HLT は CPU のバイト列に含まれない Halted を立てる。
        Prepare(interpreterEnv, [0xF4]);
        var halted = CreateCpu();
        Semantics.Interpret(Semantics.Lower(DecodeInstruction(interpreterEnv, new CPU { code32 = true })), interpreterEnv, halted);
        Equal((true, 1u), (halted.Halted, halted.eip), "HLT sets Halted and advances");
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
        var interpreterTime = Measure(() => Semantics.Interpret(block2, reference, cpu));
        var compiledTime = Measure(() => compiledAdd(reference, cpu));
        Console.WriteLine($"ADD eax, ebx ns/op: IR interpreter {interpreterTime:F1}, IR compiled {compiledTime:F1}");
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
        Loop(0x3000).CopyTo(env.Ram, 0x100);
        Loop(0x3000).CopyTo(mirror.Ram, 0x100);
        var fast = new InstructionExecutor(true);
        env.Translations.Enabled = true;
        env.Translations.Chain = false; // まず 1 ブロックずつ見る(連結は後で)
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
        Equal(1L, env.Translations.Blocks, "one multi-instruction block lowered (every site is first lowered as a single instruction)");
        Equal(true, blockSteps >= 70, "most iterations ran as blocks");
        Equal(0x100u, cpu.eip, "block ends after the taken branch");
        Equal(true, env.Ram.AsSpan().SequenceEqual(mirror.Ram), "memory after the loop");
        // 予算 3: ブロックの 3 命令目で止まり、次の Step は途中(別のブロックの先頭)から続く。
        fast.Budget = 3;
        count = Advance(count, "budget");
        Equal((3, 0x108u), (fast.Executed, cpu.eip), "budget stops the block after three instructions");
        fast.Budget = int.MaxValue;
        while (cpu.eip != 0x100) count = Advance(count, "resume");
        // 自己書き換え(別の場所から): ループ先頭の ADD を SUB に書き換える。
        byte[] patch = [0xC6, 0x05, 0x00, 0x01, 0x00, 0x00, 0x29]; // mov byte [0x100], 0x29
        patch.CopyTo(env.Ram, 0x200);
        patch.CopyTo(mirror.Ram, 0x200);
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
        self.CopyTo(env.Ram, 0x300);
        self.CopyTo(mirror.Ram, 0x300);
        cpu.eip = 0x300; expected.eip = 0x300; cpu.eax = 0x41; expected.eax = 0x41; cpu.ecx = 0; expected.ecx = 0;
        var invalidationsBefore = env.Translations.Invalidations;
        for (var iteration = 0; iteration < 40; iteration++)
            while (true) { count = Advance(count, "self-modifying"); if (cpu.eip == 0x300) break; }
        Equal(true, env.Translations.Invalidations > invalidationsBefore + 5, "in-block writes invalidate the block");
        Equal(0u, cpu.ecx, "ecx returns to zero after 40 alternating iterations");
        // STI はブロックを終える: 保留中の割り込みが 1 命令ずつの実行と同じ境界(STI の直後)で配送されるため。
        // cli ; inc eax ; sti ; inc eax ; inc eax ; jmp 0x400 -> 先頭からのブロックは 3 命令で止まり、残りは次のブロック。
        // 予算が十分でも STI で止まり、次の Step は残りのブロックから分岐先のブロックへ連結して、また STI で止まる。
        byte[] sti = [0xFA, 0x40, 0xFB, 0x40, 0x40, 0xEB, 0xF9];
        sti.CopyTo(env.Ram, 0x400);
        sti.CopyTo(mirror.Ram, 0x400);
        cpu.eip = 0x400; expected.eip = 0x400;
        for (var iteration = 0; iteration < 12; iteration++)
            while (true) { count = Advance(count, "sti"); if (cpu.eip == 0x400) break; }
        count = Advance(count, "sti block");
        Equal((true, 3, 0x403u), (fast.UsedTranslation, fast.Executed, cpu.eip), "the block ends right after STI");
        Equal(true, cpu.jf, "IF is set when the block ends");
        env.Translations.Chain = true;
        count = Advance(count, "after sti");
        Equal((true, 6, 0x403u), (fast.UsedTranslation, fast.Executed, cpu.eip), "the rest chains into the next block and stops at its STI");
        // 連結: ループ(5 命令 × 100 回)は予算の範囲で 1 回の Step で回り切り、ループを抜けた先の HLT(初めて実行される番地なので
        // まだ下ろされていない)の手前で止まる。
        env.Ram[0x110] = 0xF4; mirror.Ram[0x110] = 0xF4;
        // 自己書き換えのテストが同じページのループのブロックも捨てているので、連結せずに翻訳し直してから連結する。
        env.Translations.Chain = false;
        cpu.eip = 0x100; expected.eip = 0x100; cpu.ebx = 0; expected.ebx = 0;
        for (var iteration = 0; iteration < 12; iteration++)
            while (true) { count = Advance(count, "rewarm"); if (cpu.eip == 0x100) break; }
        env.Translations.Chain = true;
        cpu.ebx = 0; expected.ebx = 0;
        fast.Budget = 10_000;
        count = Advance(count, "chain");
        Equal((true, 500, 0x110u, 0x10Eu), (fast.UsedTranslation, fast.Executed, cpu.eip, fast.LastEip), "the whole loop runs as one chained step");
        fast.Budget = 123;
        cpu.eip = 0x100; expected.eip = 0x100; cpu.ebx = 0; expected.ebx = 0;
        count = Advance(count, "chain budget");
        Equal((123, 0x108u), (fast.Executed, cpu.eip), "the chain stops at the budget inside a block");
        // ページング有効で、データページを翻訳後に外す: ブロック 2 命令目の MOV [data] が #PF を投げ、
        // 完了数は 1(ADD)、CPU は MOV の先頭の状態。
        using var paged = new EmuEnvironment(memory: new byte[0x10000]);
        Loop(0x1800).CopyTo(paged.Ram, 0x100);
        MapPages(paged);
        var pagedCpu = Start();
        CPU._cr0.setter(pagedCpu)(0x80000001); CPU._cr3.setter(pagedCpu)(0x9000);
        paged.Paging.Sync(pagedCpu);
        var pagedExecutor = new InstructionExecutor(true);
        paged.Translations.Enabled = true;
        paged.Translations.Chain = false;
        for (var step = 0; step < 60; step++) Equal(true, pagedExecutor.Step(paged, pagedCpu).IsSuccess, $"paged step {step}");
        Equal(true, pagedExecutor.UsedTranslation && pagedCpu.eip == 0x100, "paged loop runs as a block");
        BitConverter.GetBytes(0u).CopyTo(paged.Ram, 0xA004); // 線形 0x1000-0x1FFF を不在に
        paged.Paging.Flush();
        var eaxBefore = pagedCpu.eax;
        var faulted = false;
        try { pagedExecutor.Step(paged, pagedCpu); }
        catch (PageFaultException fault) { faulted = true; Equal((0x1800u, 2u), (fault.Linear, fault.ErrorCode), "store fault from the block"); }
        Equal(true, faulted, "translated store faults");
        Equal((1, 0x102u, 0x100u), (pagedExecutor.Executed, pagedCpu.eip, pagedExecutor.LastEip), "one instruction completed before the fault");
        Equal(eaxBefore + pagedCpu.ebx, pagedCpu.eax, "ADD completed, MOV did not");
        cases++;
        // 実行器を通した 1 命令あたりの時間(5 命令のループ): 通常コアのみ、翻訳ブロック(コンパイル済み段階)、1 命令ずつの IR。
        using var timing = new EmuEnvironment(memory: new byte[0x10000]);
        Loop(0x3000).CopyTo(timing.Ram, 0x100);
        timing.Ram[0x110] = 0xF4; // ループを抜けた先の HLT で連結が止まる
        double PerInstruction(bool translate, int blockLimit = 64)
        {
            timing.Translations.Enabled = translate;
            timing.Translations.BlockLimit = blockLimit;
            timing.Translations.Chain = blockLimit > 1;
            timing.Translations.InvalidateAll();
            var executor = new InstructionExecutor(true) { Budget = 1000 };
            var state = Start();
            long executed = 0;
            void Run(long target)
            {
                executed = 0;
                // ループを抜けた先の HLT は、高速コアでは次の Step、翻訳では連結の最後に実行される(eip 0x111、Halted)。どちらでも先頭へ戻す。
                while (executed < target) { executor.Step(timing, state); executed += executor.Executed; if (state.eip is 0x110 or 0x111) { state.eip = 0x100; state.Halted = false; } }
            }
            Run(500_000);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Run(5_000_000);
            return watch.Elapsed.TotalMilliseconds * 1_000_000.0 / executed;
        }
        Console.WriteLine($"executor ns/instruction over a 5-instruction loop: reference core {PerInstruction(false):F1}, translated block {PerInstruction(true):F1}, one instruction per step {PerInstruction(true, 1):F1}");
        // デコードと IR への下ろしの費用(ADD eax, ebx / MOV [ebx+8], eax / Jcc を順に)。
        byte[] sample = [0x01, 0xD8, 0x89, 0x43, 0x08, 0x75, 0x10];
        sample.CopyTo(timing.Ram, 0x200);
        double LowerCost(bool lower)
        {
            var decodeCpu = new CPU { code32 = true, eip = 0x200 };
            const int rounds = 100_000;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var round = 0; round < rounds; round++)
                for (var offset = 0; offset < 3; offset++)
                {
                    decodeCpu.eip = offset == 0 ? 0x200u : offset == 1 ? 0x202u : 0x205u;
                    var instruction = DecodeInstruction(timing, decodeCpu);
                    if (lower) Semantics.Lower(instruction);
                }
            return watch.Elapsed.TotalMilliseconds * 1_000_000.0 / (rounds * 3);
        }
        var decodeOnly = LowerCost(false);
        Console.WriteLine($"decode + lower ns/instruction: decode {decodeOnly:F0}, lower {LowerCost(true) - decodeOnly:F0}");
    }

    // FPU: 全エンコーディング(メモリ形式は [ebx]、レジスタ形式は ST(1) など)を 3 通りの初期状態(フラグ・丸めモード)で
    // 通常コアに流し、成否と CPU・メモリの結果のハッシュを固定値と照合する(表とレンズへの書き換え前に採取)。
    static void FpuRegression()
    {
        List<byte[]> codes = [];
        foreach (var op in new byte[] { 0xD8, 0xDA, 0xDC, 0xDE })
            for (var reg = 0; reg < 8; reg++) { codes.Add([op, (byte)(reg << 3 | 3)]); codes.Add([op, (byte)(0xC0 | reg << 3 | 1)]); }
        foreach (var reg in new[] { 0, 2, 3, 4, 5, 6, 7 }) codes.Add([0xD9, (byte)(reg << 3 | 3)]);
        foreach (var reg in new[] { 0, 2, 3, 5, 7 }) codes.Add([0xDB, (byte)(reg << 3 | 3)]);
        foreach (var reg in new[] { 0, 2, 3, 4, 6, 7 }) codes.Add([0xDD, (byte)(reg << 3 | 3)]);
        foreach (var reg in new[] { 0, 2, 3, 5, 7 }) codes.Add([0xDF, (byte)(reg << 3 | 3)]);
        foreach (var (reg, rm) in new[] { (0, 1), (1, 1), (2, 0), (2, 1), (3, 1), (4, 0), (4, 1), (4, 4), (4, 5), (4, 6) })
            codes.Add([0xD9, (byte)(0xC0 | reg << 3 | rm)]);
        for (var rm = 0; rm < 8; rm++) foreach (var reg in new[] { 5, 6, 7 }) codes.Add([0xD9, (byte)(0xC0 | reg << 3 | rm)]);
        foreach (var (reg, rm) in new[] { (0, 1), (1, 1), (2, 1), (3, 1), (4, 1), (5, 1), (5, 0), (6, 1) })
            codes.Add([0xDA, (byte)(0xC0 | reg << 3 | rm)]);
        foreach (var (reg, rm) in new[] { (0, 1), (1, 1), (2, 1), (3, 1), (4, 2), (4, 3), (4, 0), (5, 1), (6, 1), (7, 1) })
            codes.Add([0xDB, (byte)(0xC0 | reg << 3 | rm)]);
        foreach (var (reg, rm) in new[] { (0, 1), (1, 1), (2, 1), (3, 1), (4, 1), (5, 1), (6, 1), (7, 1) })
            codes.Add([0xDD, (byte)(0xC0 | reg << 3 | rm)]);
        foreach (var (reg, rm) in new[] { (3, 1), (3, 2) }) codes.Add([0xDE, (byte)(0xC0 | reg << 3 | rm)]);
        foreach (var (reg, rm) in new[] { (4, 0), (4, 1), (5, 1), (6, 1), (0, 1), (7, 1) })
            codes.Add([0xDF, (byte)(0xC0 | reg << 3 | rm)]);
        using var results = new MemoryStream();
        using var writer = new BinaryWriter(results);
        var (mant, se) = (0x7000000000000000UL, (ushort)0x4000); // 80 ビット: 3.5
        foreach (var variant in new (uint flags, ushort cw)[] { (0x202, 0x037F), (0x247, 0x037F), (0x202, 0x0F7F) })
        foreach (var code in codes)
        {
            Reset(SlowEnv, code);
            var memory = SlowEnv.Ram;
            BitConverter.GetBytes(1.5f).CopyTo(memory, 0x3000);
            BitConverter.GetBytes(2.25).CopyTo(memory, 0x3008);
            BitConverter.GetBytes((short)-7).CopyTo(memory, 0x3010);
            BitConverter.GetBytes(100000).CopyTo(memory, 0x3014);
            BitConverter.GetBytes(-5L).CopyTo(memory, 0x3018);
            BitConverter.GetBytes(mant).CopyTo(memory, 0x3020);
            BitConverter.GetBytes(se).CopyTo(memory, 0x3028);
            for (var offset = 0; offset < 108; offset++) memory[0x3100 + offset] = (byte)(offset * 7 + 1);
            var cpu = CreateCpu(0x12345678, 0x3000, variant.flags);
            cpu.fpu_cw = variant.cw; cpu.fpu_top = 5; cpu.fpu_valid = 0xE0;
            cpu.fpu_st[5] = 2.0; cpu.fpu_st[6] = -0.5; cpu.fpu_st[7] = 8.0;
            var ok = SlowStep(SlowEnv, cpu, null).IsSuccess;
            writer.Write(ok);
            if (ok) { writer.Write(CpuBytes(cpu)); writer.Write(SHA256.HashData(memory.AsSpan(0x3000, 0x200))); }
            cases++;
        }
        writer.Flush();
        var hash = Convert.ToHexString(SHA256.HashData(results.ToArray()));
        Console.WriteLine($"FPU cases: {codes.Count * 3}; SHA256={hash}");
        Equal("6FD342247873F2894F0F6C9FDF28118FE6FE00168F15C782D3FF7F40909DD888", hash, "FPU results match the pre-table implementation");
    }

    // VERR/VERW/LAR/LSL: GDT の記述子(コード、読み書きデータ、読み取り専用データ、実行専用コード、DPL3 データ、TSS、
    // コンフォーミングコード)を CPL0 から照合する。ヌル・RPL が DPL を超えるものは否。LAR はアクセス権、LSL は G 展開したリミット。
    static void DescriptorChecks()
    {
        var env = SlowEnv;
        void Descriptor(int selector, uint lo, uint hi)
        {
            BitConverter.GetBytes(lo).CopyTo(env.Ram, 0x500 + selector);
            BitConverter.GetBytes(hi).CopyTo(env.Ram, 0x504 + selector);
        }
        (bool zf, uint eax) Run(byte[] code, ushort selector)
        {
            Reset(env, code);
            Descriptor(8, 0x0000FFFF, 0x00CF9A00);  // コード、読み出し可、DPL0、4GB
            Descriptor(16, 0x0000FFFF, 0x00CF9200); // データ、読み書き、DPL0、4GB
            Descriptor(24, 0x00001000, 0x00009000); // データ、読み取り専用、リミット 0x1000(G=0)
            Descriptor(32, 0x0000FFFF, 0x00CF9800); // コード、実行専用
            Descriptor(40, 0x0000FFFF, 0x00CFF200); // データ、読み書き、DPL3
            Descriptor(48, 0x00000067, 0x00008900); // TSS(利用可能)
            Descriptor(56, 0x0000FFFF, 0x00CF9E00); // コンフォーミングコード、読み出し可
            var cpu = CreateCpu(0x11111111, selector);
            cpu.gdt_base = 0x500; cpu.gdt_limit = 0x3F;
            CPU._cr0.setter(cpu)(1);
            Equal(true, SlowStep(env, cpu, null).IsSuccess, $"descriptor check {Convert.ToHexString(code)} sel={selector:x}");
            cases++;
            return (cpu.zf, cpu.eax);
        }
        byte[] verr = [0x0F, 0x00, 0xE3], verw = [0x0F, 0x00, 0xEB], lar = [0x0F, 0x02, 0xC3], lsl = [0x0F, 0x03, 0xC3]; // ..., bx / eax, bx
        Equal(true, Run(verr, 8).zf, "VERR: readable code");
        Equal(false, Run(verw, 8).zf, "VERW: code is not writable");
        Equal(true, Run(verw, 16).zf, "VERW: writable data");
        Equal(true, Run(verr, 24).zf, "VERR: read-only data is readable");
        Equal(false, Run(verw, 24).zf, "VERW: read-only data");
        Equal(false, Run(verr, 32).zf, "VERR: execute-only code");
        Equal(false, Run(verr, 48).zf, "VERR: a TSS is not a segment");
        Equal(false, Run(verr, 0).zf, "VERR: null selector");
        Equal(false, Run(verr, 16 | 3).zf, "VERR: RPL 3 exceeds DPL 0");
        Equal(true, Run(verr, 40 | 3).zf, "VERR: RPL 3 on DPL 3 data");
        Equal(true, Run(verr, 56 | 3).zf, "VERR: conforming code ignores RPL");
        Equal(false, Run(verr, 0x3F8).zf, "VERR: beyond the GDT limit");
        Equal((true, 0x00CF9200u), Run(lar, 16), "LAR: access rights of the data segment");
        Equal((true, 0x00008900u), Run(lar, 48), "LAR: a TSS is allowed");
        Equal((false, 0x11111111u), Run(lar, 16 | 3), "LAR: privilege failure leaves the register");
        Equal((true, 0xFFFFFFFFu), Run(lsl, 16), "LSL: 4GB limit with granularity");
        Equal((true, 0x00001000u), Run(lsl, 24), "LSL: byte-granular limit");
        Equal((true, 0x00000067u), Run(lsl, 48), "LSL: TSS limit");
        Equal((false, 0x11111111u), Run(lsl, 0), "LSL: null selector");
    }

    // 文字列命令以外に付いた REP/REPNE は無視される(`rep ret`、`repne nop`、`rep` + MOV)。通常コアで検証(高速コアは委譲する)。
    static void RepPrefixOnOtherInstructions()
    {
        var env = SlowEnv;
        (uint eip, uint eax, uint esp) Run(byte[] code)
        {
            Reset(env, code);
            BitConverter.GetBytes(0x1234u).CopyTo(env.Ram, 0x8000);
            var cpu = CreateCpu(0x11111111, 0x22222222);
            Equal(true, SlowStep(env, cpu, null).IsSuccess, $"rep-prefixed {Convert.ToHexString(code)} executes");
            cases++;
            return (cpu.eip, cpu.eax, cpu.esp);
        }
        Equal((0x1234u, 0x11111111u, 0x8004u), Run([0xF3, 0xC3]), "rep ret returns");
        Equal((0x1234u, 0x11111111u, 0x8004u), Run([0xF2, 0xC3]), "repne ret returns");
        Equal((2u, 0x11111111u, 0x8000u), Run([0xF3, 0x90]), "rep nop (pause) advances");
        Equal((3u, 0x22222222u, 0x8000u), Run([0xF3, 0x89, 0xD8]), "rep mov eax, ebx moves");
    }

    // グループ命令の表に無い reg 値(0F 01 /4 SMSW、/5、/6 LMSW、FE /2 など)は失敗(#UD)になり、ホスト側の例外にはならない。
    static void UndefinedGroupMembers()
    {
        var env = SlowEnv;
        foreach (var code in new byte[][] { [0x0F, 0x01, 0xE0], [0x0F, 0x01, 0xE8], [0x0F, 0x01, 0xF0], [0xFE, 0xD0], [0x0F, 0x00, 0xF0], [0x0F, 0x00, 0xF8] })
        {
            Reset(env, code);
            var cpu = CreateCpu();
            CPU._cr0.setter(cpu)(1);
            Equal(false, SlowStep(env, cpu, null).IsSuccess, $"undefined group member {Convert.ToHexString(code)} fails instead of throwing");
            cases++;
        }
    }

    static void Integration()
    {
        foreach (var code in new byte[][] { [0xF3, 0xA5], [0x66, 0xF3, 0xAB], [0xF3, 0xAA], [0xA6], [0xA7], [0xAE], [0xAF], [0xAD], [0x50], [0x66, 0x50], [0x58], [0x66, 0x58] })
            Check(code, CreateCpu(0x87654321), env => Array.Fill(env.Ram, (byte)0x5a, 0x3000, 16));
        // 文字列命令のセグメントオーバーライド: 源(DS:SI)だけが差し替わり、先(ES:DI)は固定。REP の前後どちらの位置でも効く。
        foreach (var code in new byte[][] { [0x2E, 0xA4], [0x64, 0xF3, 0xA5], [0xF3, 0x65, 0xA4], [0x26, 0xAC], [0x36, 0xF3, 0xA6], [0x2E, 0x6E], [0x64, 0xF3, 0x6F], [0x26, 0xAA], [0x64, 0xAE] })
        {
            var segmented = CreateCpu(0x87654321);
            segmented.cs_base = 0x100; segmented.ss_base = 0x200; segmented.fs_base = 0x300; segmented.gs_base = 0x400; segmented.es_base = 0x1000;
            segmented.edx = 0x300;
            Check(code, segmented, env =>
            {
                for (var offset = 0; offset < 0x800; offset++) env.Ram[0x3000 + offset] = (byte)(offset * 3 + 1);
                for (var offset = 0; offset < 16; offset++) env.Ram[0x4000 + offset] = 0x5a;
            });
        }
        // INS/OUTS(単発と REP、8/16/32 ビット、DF): ポート 0x300.. は素通しの IoPort 配列なので両コアで同じ値を読む。
        foreach (var code in new byte[][] { [0x6C], [0x6D], [0x66, 0x6D], [0x6E], [0x6F], [0x66, 0x6F], [0xF3, 0x6D], [0x66, 0xF3, 0x6D], [0xF3, 0x6C], [0xF3, 0x6F], [0x66, 0xF3, 0x6E] })
        foreach (var direction in new uint[] { 0, 0x400 })
        {
            var io = CreateCpu(0x87654321, flags: 0x202 | direction);
            io.edx = 0x300; io.edi = 0x3008; io.esi = 0x3008;
            Check(code, io, env => { Array.Fill(env.Ram, (byte)0x5a, 0x3000, 16); for (var i = 0; i < 4; i++) env.IoPort[0x300 + i] = (byte)(0x11 * (i + 1)); },
                (cpu, _) => Equal(code.Contains((byte)0xF3) ? 0u : 3u, cpu.ecx, $"REP INS/OUTS count {Convert.ToHexString(code)}"));
        }
        for (var type = 1; type < 3; type++)
            Check(Store(type), CreateCpu(0x12345678, 0x1000), env => { MapPages(env); BitConverter.GetBytes(3u).CopyTo(env.Ram, 0xA004); env.Diagnostics = new() { WatchLo = 0, WatchHi = 1 }; }, (_, env) => Equal(true, env.Diagnostics.WatchTriggered, "watch at physical zero"), diagnostics: true);
        Reset(SlowEnv, [0xD9, 0xE8, 0xDD, 0x1B]);
        var cpu = CreateCpu(right: 0xfff03000);
        Equal(true, SlowStep(SlowEnv, cpu, null).IsSuccess, "FLD1");
        Equal(true, SlowStep(SlowEnv, cpu, null).IsSuccess, "FSTP");
        Equal(1.0, BitConverter.ToDouble(SlowEnv.Ram, 0x3000), "FPU alias store");
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
                    env.Diagnostics = new() { WriteLog = logWrites ? [] : null, WriteLogLo = 0, WriteLogHi = 0x10000, WatchLo = 0x5FFF, WatchHi = 0x6000 };
                }, diagnostics: true);
            }
        }
    }

    // ALU・メモリ書き込みの結果列を、ALU 整理前に採取したハッシュと照合する(両コアが同時に変わっても差分を検出する)。
    static void Baseline()
    {
        Arithmetic();
        Memory(false);
        Writer.Flush();
        var baselineHash = Convert.ToHexString(SHA256.HashData(Results.ToArray()));
        Equal("E956129AF77B2263DAB0B3463C62E6310B7114FB93E814B91FA01BC22DA1140F", baselineHash, "pre-refactor results");
        Console.WriteLine($"Baseline-compatible cases: {cases}; SHA256={baselineHash}");
    }

    // 名前付きのケース(ランナーが順に実行し、失敗したケース名を報告する)。
    public static (string Name, Action Run)[] Cases =>
    [
        ("baseline", Baseline),
        ("memory-diagnostics", () => Memory(true)),
        ("page-walk", PageWalk),
        ("ir", SemanticsIr),
        ("translation-cache", TranslationCacheExecution),
        ("fpu", FpuRegression),
        ("descriptors", DescriptorChecks),
        ("rep-prefix", RepPrefixOnOtherInstructions),
        ("undefined-groups", UndefinedGroupMembers),
        ("integration", Integration),
        ("operand-transfers", OperandTransfers),
    ];
}
