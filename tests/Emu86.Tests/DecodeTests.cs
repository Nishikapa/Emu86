using Emu86;
using static Emu86.Ext;

static class DecodeTests
{
    static int checks;

    static void Equal<T>(T expected, T actual, string label)
    {
        checks++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label}: expected {expected}, got {actual}");
    }

    static CPU CreateCpu(bool code32, bool addressPrefix) => new()
    {
        code32 = code32, address_size_prefix = addressPrefix,
        eax = 0x10203040, ecx = 0x12345678, edx = 0x87654321, ebx = 0xABCDEF00,
        esp = 0x3000, ebp = 0x456789AB, esi = 0x55667788, edi = 0x99AABBCC,
        ds_base = 0x100, ss_base = 0x200, fs_base = 0x300
    };

    static void Addresses()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var slow = new InstructionExecutor(false);
        var fast = new InstructionExecutor(true);
        foreach (var code32 in new[] { false, true })
        foreach (var addressPrefix in new[] { false, true })
        foreach (var segmentPrefix in new[] { false, true })
        for (var mod = 0; mod < 4; mod++)
        for (var register = 0; register < 8; register++)
        for (var sib = 0; sib < (mod != 3 && register == 4 && code32 != addressPrefix ? 256 : 1); sib++)
        foreach (var displacement in new uint[] { 0x12345678, 0xFFFFFF80 })
        {
            var cpu = CreateCpu(code32, addressPrefix);
            cpu.fs_prefix = segmentPrefix;
            uint[] registers = [cpu.eax, cpu.ecx, cpu.edx, cpu.ebx, cpu.esp, cpu.ebp, cpu.esi, cpu.edi];
            uint offset;
            var length = 0;
            var stack = false;
            if (mod == 3) offset = (uint)register;
            else if (code32 != addressPrefix)
            {
                var baseRegister = register == 4 ? sib & 7 : register;
                var noBase = mod == 0 && baseRegister == 5;
                var indexRegister = (sib >> 3) & 7;
                var scaled = register == 4 && indexRegister != 4 ? registers[indexRegister] << (sib >> 6) : 0;
                offset = scaled + (noBase ? 0 : registers[baseRegister]);
                stack = !noBase && baseRegister is 4 or 5;
                if (register == 4) env.OneMegaMemory_[length++] = (byte)sib;
                if (mod == 1)
                {
                    env.OneMegaMemory_[length++] = (byte)displacement;
                    offset = unchecked(offset + (uint)(sbyte)displacement);
                }
                else if (mod == 2 || noBase)
                {
                    BitConverter.GetBytes(displacement).CopyTo(env.OneMegaMemory_, length);
                    length += 4;
                    offset = unchecked(offset + displacement);
                }
            }
            else
            {
                // 16 ビットアドレッシングの実効アドレスは 16 ビットで折り返す(レジスタ和も変位の加算も)。
                uint[] sums = [(ushort)(cpu.bx + cpu.si), (ushort)(cpu.bx + cpu.di), (ushort)(cpu.bp + cpu.si),
                    (ushort)(cpu.bp + cpu.di), cpu.si, cpu.di, cpu.bp, cpu.bx];
                var noBase = mod == 0 && register == 6;
                offset = noBase ? 0 : sums[register];
                stack = !noBase && register is 2 or 3 or 6;
                if (mod == 1)
                {
                    env.OneMegaMemory_[length++] = (byte)displacement;
                    offset = (ushort)(offset + (uint)(sbyte)displacement);
                }
                else if (mod == 2 || noBase)
                {
                    BitConverter.GetBytes(displacement).CopyTo(env.OneMegaMemory_, length);
                    length += 2;
                    offset = (ushort)(offset + (ushort)displacement);
                }
            }
            var result = GetMemOrRegAddr(mod, register)(env, cpu, null);
            var segment = mod == 3 ? 0 : segmentPrefix ? cpu.fs_base : stack ? cpu.ss_base : cpu.ds_base;
            Equal(true, result.IsSuccess, "decode success");
            Equal((mod != 3, unchecked(offset + segment)), result.value, "ModRM address");
            Equal((uint)length, cpu.eip, "ModRM length");
            cpu.eip = 0;
            Equal((mod != 3, offset), GetMemOrRegOffset(mod, register)(env, cpu, null).value, "LEA offset");
            Equal((uint)length, cpu.eip, "LEA length");
            if (mod != 3)
            {
                var code = new List<byte>();
                if (addressPrefix) code.Add(0x67);
                if (segmentPrefix) code.Add(0x64);
                code.Add(0x8D);
                code.Add((byte)((mod << 6) | register));
                code.AddRange(env.OneMegaMemory_.Take(length));
                code.CopyTo(env.OneMegaMemory_);
                var slowCpu = CreateCpu(code32, false);
                var fastCpu = CreateCpu(code32, false);
                Equal(true, slow.Step(env, slowCpu).IsSuccess, "normal LEA success");
                Equal(true, fast.Step(env, fastCpu).IsSuccess, "fast LEA success");
                Equal(true, fast.UsedFastPath, "LEA uses fast decoder");
                Equal(code32 ? offset : (ushort)offset, code32 ? slowCpu.eax : slowCpu.ax, "LEA instruction value");
                Equal(slowCpu.eax, fastCpu.eax, "LEA core agreement");
                Equal((uint)code.Count, slowCpu.eip, "normal LEA instruction length");
                Equal(slowCpu.eip, fastCpu.eip, "fast LEA instruction length");
            }
        }
    }

    static void FetchBoundaries()
    {
        foreach (var useFast in new[] { false, true })
        foreach (var code32 in new[] { false, true })
        foreach (var mappedNextPage in new[] { false, true })
        foreach (var crossing in new[] { false, true })
        foreach (var kind in new[] { 0, 1, 2, 3, 4 })
        {
            using var env = new EmuEnvironment(memory: new byte[0x10000]);
            var code = new List<byte>();
            if (kind < 3)
            {
                if (kind != 0 && code32 != (kind == 2)) code.Add(0x66);
                code.Add((byte)(kind == 0 ? 0xB0 : 0xB8));
                code.AddRange(BitConverter.GetBytes(0x12345678u).Take(1 << kind));
            }
            else
            {
                var address32 = kind == 4;
                if (code32 != address32) code.Add(0x67);
                code.Add(0x8D);
                code.Add((byte)(address32 ? 0x04 : 0x06));
                if (address32) code.Add(0x25);
                code.AddRange(BitConverter.GetBytes(0x1234u).Take(address32 ? 4 : 2));
            }
            var start = (uint)(0x1000 - code.Count + (crossing ? 1 : 0));
            for (var index = 0; index < code.Count; index++)
            {
                var address = start + (uint)index;
                var physical = address < 0x1000 ? address : 0x5000 + (address & 0xFFF);
                env.OneMegaMemory_[physical] = code[index];
            }
            BitConverter.GetBytes(0xA003u).CopyTo(env.OneMegaMemory_, 0x9000);
            BitConverter.GetBytes(3u).CopyTo(env.OneMegaMemory_, 0xA000);
            if (mappedNextPage) BitConverter.GetBytes(0x5003u).CopyTo(env.OneMegaMemory_, 0xA004);
            var cpu = new CPU { code32 = code32, eip = start };
            CPU._cr0.setter(cpu)(0x80000001);
            CPU._cr3.setter(cpu)(0x9000);
            EnvSyncPaging(env, cpu);
            var executor = new InstructionExecutor(useFast);
            var faulted = false;
            try
            {
                Equal(true, executor.Step(env, cpu).IsSuccess, "boundary instruction success");
                Equal(start + (uint)code.Count, cpu.eip, "boundary instruction length");
                var expected = kind == 0 ? 0x78u : kind == 1 ? 0x5678u : kind == 2 ? 0x12345678u : 0x1234u;
                Equal(expected, cpu.eax, "boundary instruction value");
                Equal(useFast, executor.UsedFastPath, "boundary core selection");
            }
            catch (PageFaultException fault)
            {
                faulted = true;
                Equal(0x1000u, fault.Linear, "fetch fault address");
                Equal(0u, fault.ErrorCode, "fetch fault code");
                Equal(start, cpu.eip, "fetch fault restores EIP");
                Equal(0u, cpu.eax, "fetch fault preserves register");
                Equal(false, cpu.address_size_prefix || cpu.operand_size_prefix, "fetch fault clears prefixes");
            }
            Equal(crossing && !mappedNextPage, faulted, "read only required instruction bytes");
        }

        foreach (var useFast in new[] { false, true })
        {
            using var env = new EmuEnvironment(memory: new byte[0x10000]);
            byte[] code = [0x8D, 0x84, 0xB5, 0x78, 0x56, 0x34, 0x12];
            code.CopyTo(env.OneMegaMemory_, 0);
            var cpu = new CPU { code32 = true, cs_base = 0xFFF00000, ebp = 0x100, esi = 0x200 };
            Equal(true, new InstructionExecutor(useFast).Step(env, cpu).IsSuccess, "BIOS alias decoding");
            Equal(0x12345F78u, cpu.eax, "BIOS alias displacement");
            Equal((uint)code.Length, cpu.eip, "BIOS alias instruction length");
        }

        foreach (var code32 in new[] { false, true })
        foreach (var addressPrefix in new[] { false, true })
        foreach (var register in Enumerable.Range(0, 8))
        {
            using var env = new EmuEnvironment(memory: new byte[0x10000]);
            var cpu = CreateCpu(code32, addressPrefix);
            cpu.eip = 0x1000;
            env.PagingOn = true;
            Equal((false, (uint)register), GetMemOrRegAddr(3, register)(env, cpu, null).value, "register-only does not fetch");
            Equal(0x1000u, cpu.eip, "register-only EIP unchanged");
        }
    }

    static void Allocations()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = CreateCpu(true, false);
        env.OneMegaMemory_[0] = 0xB5;
        var decode = GetMemOrRegAddr(2, 4);
        var immediate = GetMemoryDataIp_(2);
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            cpu.eip = 0;
            decode(env, cpu, null);
            immediate(env, cpu, null);
        }
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 10000; iteration++)
        {
            cpu.eip = 0;
            decode(env, cpu, null);
            immediate(env, cpu, null);
        }
        Console.WriteLine($"Decode + immediate allocated bytes/iteration: {(GC.GetAllocatedBytesForCurrentThread() - start) / 10000}");
        // プレフィックス列と ModRM のパーサ、および高速コアの 1 命令(66 67 64 8D 84 B5 disp32 = LEA)も割り当てない。
        byte[] lea = [0x66, 0x67, 0x64, 0xF0, 0x8D, 0x84, 0xB5, 0x78, 0x56, 0x34, 0x12];
        lea.CopyTo(env.OneMegaMemory_, 0);
        var code = CodeSource.At(env, cpu);
        var modrm = ModRmParser(true);
        for (var iteration = 0; iteration < 1000; iteration++) { Prefixes(code, 0); modrm(code, 4); cpu.eip = 0; FastStep(env, cpu); }
        start = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 10000; iteration++) { Prefixes(code, 0); modrm(code, 4); }
        Console.WriteLine($"Prefix + ModRM parser allocated bytes/iteration: {(GC.GetAllocatedBytesForCurrentThread() - start) / 10000}");
        start = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 10000; iteration++) { cpu.eip = 0; FastStep(env, cpu); }
        Console.WriteLine($"Fast core LEA allocated bytes/iteration: {(GC.GetAllocatedBytesForCurrentThread() - start) / 10000}");
    }

    // 命令バイト列のパーサ: プレフィックスの畳み込み、ModRM/SIB/変位の構造、コンビネータの失敗時の位置、16 ビット IP の折り返し。
    static void Parsers()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        byte[] bytes = [0x66, 0x2E, 0x67, 0xF0, 0x66, 0x64, 0x8B, 0x44, 0x98, 0xF0, 0x12, 0x34];
        bytes.CopyTo(env.OneMegaMemory_, 0x100);
        var code = new CodeSource(env, 0, 0x100, true);
        var (ok, prefixes, next) = Prefixes(code, 0);
        Equal(true, ok, "prefixes succeed");
        Equal(Prefix.OperandSize | Prefix.Cs | Prefix.AddressSize | Prefix.Lock | Prefix.Fs, prefixes, "prefix flags folded");
        Equal(6, next, "prefix bytes consumed");
        Equal((true, Prefix.None, 6), Prefixes(code, 6), "no prefixes consumes nothing");
        Equal(0x8Bu, (uint)Fetch8(code, 6).value, "opcode after prefixes");
        var modrm = ModRmParser(true)(code, 7);
        Equal(true, modrm.ok, "ModRM parsed");
        Equal((1, 0, 4, true), (modrm.value.Mod, modrm.value.Reg, modrm.value.Rm, modrm.value.HasSib), "ModRM fields");
        Equal((2, 3, 0), (modrm.value.Scale, modrm.value.Index, modrm.value.Base), "SIB fields");
        Equal(0xFFFFFFF0u, modrm.value.Disp, "disp8 sign-extended");
        Equal(3, modrm.value.Length, "ModRM + SIB + disp8 length");
        Equal(10, modrm.next, "ModRM advances past the displacement");
        var cpu = CreateCpu(true, false);
        var (isMem, offset, segment) = EffectiveAddress(cpu, modrm.value, true, 0x111, 0x222);
        Equal((true, unchecked(cpu.eax + (cpu.ebx << 2) - 16), 0x111u), (isMem, offset, segment), "effective address with scaled index");
        // 同じ ModRM(44 98)を 16 ビットで読むと [si+disp8]: 変位は符号拡張され、実効アドレスは 16 ビットで折り返す。
        var modrm16 = ModRmParser(false)(code, 7).value;
        Equal((true, (uint)(ushort)(cpu.si - 0x68), 0x111u), EffectiveAddress(cpu, modrm16, false, 0x111, 0x222), "16-bit [si+disp8]");
        Equal(2, modrm16.Length, "16-bit ModRM mod=1 is ModRM + disp8");
        Equal((true, (uint)(ushort)(cpu.bp + cpu.si - 0x68), 0x222u), EffectiveAddress(cpu, ParseModRm(code, 8, 0x42, false), false, 0x111, 0x222), "16-bit [bp+si+disp8] selects SS");
        Equal((false, 3u, 0u), EffectiveAddress(cpu, ParseModRm(code, 1, 0xC3, true), true, 0x111, 0x222), "register form does not touch memory");
        Equal(1, ParseModRm(code, 1, 0xC3, true).Length, "register form length");
        Equal(5, ParseModRm(code, 1, 0x05, true).Length, "[disp32] length");
        Equal(2, ParseModRm(code, 1, 0x04, true).Length, "SIB without displacement length");
        env.OneMegaMemory_[0x200] = 0x05; BitConverter.GetBytes(0xCAFEBABEu).CopyTo(env.OneMegaMemory_, 0x201);
        var sibDisp = ParseModRm(new CodeSource(env, 0, 0x200, true), 0, 0x04, true);
        Equal((6, true, (byte)5, 0xCAFEBABEu), (sibDisp.Length, sibDisp.HasSib, sibDisp.Base, sibDisp.Disp), "SIB with base=5 and mod=0 reads disp32");
        Equal(3, ParseModRm(code, 1, 0x06, false).Length, "16-bit [disp16] length");

        // コンビネータ: Where は失敗時に位置を戻し、Or は 2 つ目を試し、クエリ構文で合成できる。
        var even = Fetch8.Where(value => (value & 1) == 0);
        Equal((false, (byte)0, 2), even(code, 2), "where fails without consuming");
        Equal((true, (byte)0x66, 1), even(code, 0), "where passes a matching byte");
        var either = even.Or(Fetch8.Select(value => (byte)(value + 1)));
        Equal((true, (byte)0x68, 3), either(code, 2), "or falls back to the second parser");
        var operand =
            from prefix in Prefixes
            from opcode in Fetch8
            where opcode == 0x8B
            from m in ModRmParser((prefix & Prefix.AddressSize) == 0)
            select (opcode, m.Length, m.Disp);
        // 0x67 が付いているので ModRM は 16 ビット形式(ModRM + disp8)として読まれる。
        Equal((true, (0x8B, 2, 0xFFFFFF98u), 9), operand(code, 0), "query composition over prefixes, opcode and ModRM");
        Equal((false, 7), (operand(code, 7).ok, operand(code, 7).next), "failed query restores the start position");
        Equal(0x7Fu, 0x7Fu.ToParser()(code, 3).value, "ToParser returns the value without consuming");
        Equal((true, 1, 5), Fetch8.Where(value => value == 0x66).Fold(0, (count, _) => count + 1)(code, 4), "fold stops at the first mismatch");
        Equal((true, 0, 1), Fetch8.Where(value => value == 0x66).Fold(0, (count, _) => count + 1)(code, 1), "fold with no match consumes nothing");

        // 16 ビットコードセグメントでは IP が折り返す: FFFE から 4 バイト読むと 0000 へ戻る。
        env.OneMegaMemory_[0xFFFE] = 0xAA; env.OneMegaMemory_[0xFFFF] = 0xBB; env.OneMegaMemory_[0] = 0xCC; env.OneMegaMemory_[1] = 0xDD;
        var wrapped = new CodeSource(env, 0, 0xFFFE, false);
        Equal((0xAA, 0xBB, 0xCC, 0xDD), (wrapped.Byte(0), wrapped.Byte(1), wrapped.Byte(2), wrapped.Byte(3)), "16-bit IP wraps per byte");
        Equal(0xAAu, new CodeSource(env, 0, 0xFFFE, true).Byte(0), "32-bit code does not wrap");
        Equal(0x10000u, new CodeSource(env, 0, 0xFFFE, true).Address(2), "32-bit address continues past 64K");
    }

    public static void Run()
    {
        Addresses();
        FetchBoundaries();
        Parsers();
        Allocations();
        Console.WriteLine($"Decode assertions passed: {checks}");
    }
}
