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
                uint[] sums = [(uint)(cpu.bx + cpu.si), (uint)(cpu.bx + cpu.di), (uint)(cpu.bp + cpu.si),
                    (uint)(cpu.bp + cpu.di), cpu.si, cpu.di, cpu.bp, cpu.bx];
                var noBase = mod == 0 && register == 6;
                offset = noBase ? 0 : sums[register];
                stack = !noBase && register is 2 or 3 or 6;
                if (mod == 1)
                {
                    env.OneMegaMemory_[length++] = (byte)displacement;
                    offset = unchecked(offset + (uint)(sbyte)displacement);
                }
                else if (mod == 2 || noBase)
                {
                    BitConverter.GetBytes(displacement).CopyTo(env.OneMegaMemory_, length);
                    length += 2;
                    offset += (ushort)displacement;
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
    }

    public static void Run()
    {
        Addresses();
        FetchBoundaries();
        Allocations();
        Console.WriteLine($"Decode assertions passed: {checks}");
    }
}
