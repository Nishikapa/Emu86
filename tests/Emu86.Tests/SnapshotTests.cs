using Emu86;
using System.Security.Cryptography;
using System.Text;
using static Emu86.Ext;

static class SnapshotTests
{
    static int assertions;
    static void Equal<T>(T expected, T actual, string name)
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{name}: expected {expected}, got {actual}");
    }

    static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new Exception(name + ": expected " + typeof(T).Name);
    }

    static EmuEnvironment Environment() => new(memory: new byte[0x10000]);
    static CPU Cpu() => new() { eip = 0x100, code32 = true, stack32 = true, eax = 0x87654321, ebx = 0x3000, esp = 0x2002, eflags = 0x203 };

    static void Page(EmuEnvironment env, int linear, uint physical) =>
        BitConverter.GetBytes(physical | 3).CopyTo(env.OneMegaMemory_, 0xA000 + linear * 4);

    static void Paging(EmuEnvironment env, CPU cpu)
    {
        BitConverter.GetBytes(0xA003u).CopyTo(env.OneMegaMemory_, 0x9000);
        Page(env, 0, 0);
        CPU._cr0.setter(cpu)(0x80010001);
        CPU._cr3.setter(cpu)(0x9000);
        EnvSyncPaging(env, cpu);
    }

    static string CpuState(CPU cpu)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        cpu.WriteTo(writer);
        cpu.SaveRuntimeState(writer);
        return Convert.ToHexString(stream.ToArray());
    }

    static void Instructions()
    {
        foreach (var fast in new[] { false, true })
        {
            var executor = new InstructionExecutor(fast);
            var env = Environment();
            var cpu = Cpu();
            byte[] invalid = [0x66, 0x67, 0x64, 0xF0, 0x0F, 0xFF];
            invalid.CopyTo(env.OneMegaMemory_, 0x100);
            var initial = CpuState(cpu);
            Equal(false, executor.Step(env, cpu).IsSuccess, "unsupported opcode");
            Equal(initial, CpuState(cpu), "unsupported instruction rollback");
            Equal(false, cpu.lock_prefix, "LOCK cleanup");
            byte[] move = [0xB8, 0x78, 0x56, 0x34, 0x12];
            move.CopyTo(env.OneMegaMemory_, 0x100);
            Equal(true, executor.Step(env, cpu).IsSuccess, "instruction after unsupported opcode");
            Equal(0x12345678u, cpu.eax, "no leaked operand prefix");

            cpu = Cpu();
            Paging(env, cpu);
            env.OneMegaMemory_[0x100] = 0x66;
            env.OneMegaMemory_[0x101] = 0x50;
            initial = CpuState(cpu);
            Throws<PageFaultException>(() => executor.Step(env, cpu), "PUSH fault");
            Equal(initial, CpuState(cpu), "PUSH rollback");
            Equal(true, env.PagingOn, "paging mirror after rollback");
            Page(env, 2, 0x5000);
            env.FlushTlb();
            Equal(true, executor.Step(env, cpu).IsSuccess, "PUSH retry");
            Equal(0x2000u, cpu.esp, "PUSH stack after retry");
            Equal((ushort)0x4321, BitConverter.ToUInt16(env.OneMegaMemory_, 0x5000), "PUSH value after retry");

            env = Environment(); cpu = Cpu(); Paging(env, cpu);
            Page(env, 1, 0x5000); Page(env, 3, 0x6000);
            env.OneMegaMemory_[0x100] = 0xF3; env.OneMegaMemory_[0x101] = 0xA4;
            env.OneMegaMemory_[0x6000] = 0x11; env.OneMegaMemory_[0x6001] = 0x22;
            cpu.ecx = 2; cpu.esi = 0x3000; cpu.edi = 0x1FFF;
            Throws<PageFaultException>(() => executor.Step(env, cpu), "REP fault");
            Equal(0x100u, cpu.eip, "REP restart EIP");
            Equal(1u, cpu.ecx, "REP remaining count");
            Equal(0x3001u, cpu.esi, "REP source progress");
            Equal(0x2000u, cpu.edi, "REP destination progress");
            env.OneMegaMemory_[0x6000] = 0x99;
            Page(env, 2, 0x7000); env.FlushTlb();
            Equal(true, executor.Step(env, cpu).IsSuccess, "REP retry");
            Equal((byte)0x11, env.OneMegaMemory_[0x5fff], "completed REP iteration is not replayed");
            Equal((byte)0x22, env.OneMegaMemory_[0x7000], "remaining REP iteration");
            Equal(0u, cpu.ecx, "REP completion");

            initial = CpuState(cpu);
            Throws<PageFaultException>(() => executor.Execute(env, cpu, (context, state, opcodes) =>
            {
                state.eax = 999; state.fpu_st[0] = 88; state.fs_prefix = true;
                CPU._cr3.setter(state)(0xB000); EnvSyncPaging(context, state);
                context.OneMegaMemory_[0x8000] = 0xAB;
                throw new PageFaultException(0xDEAD, 2);
            }), "mid-instruction state fault");
            Equal(initial, CpuState(cpu), "CPU and FPU rollback");
            Equal(0x9000u, env.Cr3Base, "CR3 mirror rollback");
            Equal((byte)0xAB, env.OneMegaMemory_[0x8000], "memory side effects retained");
        }
        var replacement = Cpu();
        State<int> failure = (env, cpu, opcodes) => (false, 0, replacement, "failure");
        Equal(replacement, failure.Select(value => value + 1)(null, Cpu(), null).cpu, "State failure returns actual CPU");
        var saved = Cpu(); saved.lock_prefix = true; saved.fs_prefix = true;
        var copied = Cpu(); saved.CopyTo(copied);
        Equal(true, copied.lock_prefix && copied.fs_prefix, "CopyTo includes decode context");
    }

    static void Populate(EmuEnvironment env, CPU cpu)
    {
        env.OneMegaMemory_[0x4321] = 0x5A; env.IoPort[0x4321] = 0x12;
        env.Cmos[14] = 0x41; env.CmosIndex = 14;
        env.Tsc = 12345678; env.Msrs[0x10] = 987654321;
        env.PitCounter = 0x1234; env.PitLatched = 0x5678; env.PitReadPhase = 1;
        env.PicMasterBase = 0x20; env.PicSlaveBase = 0x28;
        env.PicMasterMask = 0xE0; env.PicSlaveMask = 0xA0;
        env.PicMasterIsr = 0x05; env.PicSlaveIsr = 0x40;
        env.PicMasterInit = 2; env.PicSlaveInit = 3;
        env.PicMasterIcw4 = true; env.PicSlaveIcw4 = true;
        env.NextIrq = 12349999;
        env.Port61 = 3; env.Port61Refresh = 0x10;
        env.Pit2Counter = 0x8765; env.Pit2WritePhase = 1; env.Pit2ReadPhase = 1;
        env.Pit2Armed = true; env.Pit2Wait = 2; env.PitStatusPending = true;
        env.KbdOut.Enqueue(0xFA); env.KbdOut.Enqueue(0xAA);
        env.KbdLast = 0x55; env.KbdCmdByte = 0x67; env.KbdPendingCmd = 0x60;
        env.Pm1Sts = 1; env.Pm1En = 2; env.Pm1Cnt = 3; env.Gpe0Sts = 4; env.Gpe0En = 5;
        env.Dr[3] = 0x98765432;
        env.Pci.Address = 0x80000920; env.Pci.DataWrite(0, 4, 0xD001);
        cpu.fpu_top = 3; cpu.fpu_valid = 0xF8; cpu.fpu_st[3] = 1.25;
        cpu.tr = 0x28; cpu.ldtr = 0x30;
    }

    static string StateHash(EmuEnvironment env, CPU cpu)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        cpu.WriteTo(writer); cpu.SaveRuntimeState(writer);
        env.SaveState(writer); env.SaveRuntimeState(writer); env.SaveDeviceState(writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    static void Legacy(string path, int version, CPU cpu, EmuEnvironment env)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("EMU86SNP")); writer.Write(version); writer.Write(9876L);
        cpu.WriteTo(writer); env.SaveState(writer);
        if (version >= 3)
        {
            writer.Write(env.Tsc); writer.Write(env.Msrs.Count);
            foreach (var entry in env.Msrs) { writer.Write(entry.Key); writer.Write(entry.Value); }
        }
        if (version >= 4) { writer.Write(env.PicMasterBase); writer.Write(env.PicSlaveBase); writer.Write(env.PicMasterMask); writer.Write(env.PicSlaveMask); }
        if (version >= 5) { writer.Write(cpu.fpu_top); writer.Write(cpu.fpu_valid); foreach (var value in cpu.fpu_st) writer.Write(value); }
        if (version >= 6) { writer.Write(cpu.tr); writer.Write(cpu.ldtr); }
    }

    static void Snapshots()
    {
        var env = Environment(); var cpu = Cpu(); Populate(env, cpu);
        SnapshotStore.Save("roundtrip.snap", 12345, cpu, env);
        var restored = Environment();
        var loaded = SnapshotStore.Load("roundtrip.snap", restored, DiskConfiguration.Discover(Directory.GetCurrentDirectory()));
        Equal(12345L, loaded.count, "snapshot count");
        Equal(StateHash(env, cpu), StateHash(restored, loaded.cpu), "complete snapshot roundtrip");
        string[] fields = ["PicMasterIsr", "PicSlaveIsr", "PicMasterInit", "PicSlaveInit", "PicMasterIcw4", "PicSlaveIcw4", "NextIrq", "Port61", "Port61Refresh", "Pit2Counter", "Pit2WritePhase", "Pit2ReadPhase", "Pit2Armed", "Pit2Wait", "PitStatusPending", "KbdLast", "KbdCmdByte", "KbdPendingCmd", "Pm1Sts", "Pm1En", "Pm1Cnt", "Gpe0Sts", "Gpe0En"];
        foreach (var name in fields)
        {
            var property = typeof(EmuEnvironment).GetProperty(name);
            object ReadValue(EmuEnvironment source) => property != null
                ? property.GetValue(source)
                : typeof(EmuEnvironment).GetField(name).GetValue(source);
            Equal(ReadValue(env), ReadValue(restored), name);
        }
        Equal(0xD000u, restored.Pci.IdeBmBase, "PCI BAR restored");
        Equal(0x80000920u, restored.Pci.Address, "PCI address restored");
        Equal("250,170", string.Join(",", restored.KbdOut), "keyboard queue restored");
        Equal(0x98765432u, restored.Dr[3], "debug register restored");
        for (var version = 2; version <= 6; version++)
        {
            Legacy("legacy.snap", version, cpu, env);
            restored = Environment(); loaded = SnapshotStore.Load("legacy.snap", restored, DiskConfiguration.Discover(Directory.GetCurrentDirectory()));
            Equal(9876L, loaded.count, "legacy count");
            Equal(cpu.eax, loaded.cpu.eax, "legacy CPU");
            Equal((byte)0x5A, restored.OneMegaMemory_[0x4321], "legacy RAM offset");
            Equal(version >= 3 ? env.Tsc : 9876UL, restored.Tsc, "legacy TSC");
            Equal(version >= 5 ? 1.25 : 0.0, loaded.cpu.fpu_st[3], "legacy FPU");
            Equal(version >= 6 ? (ushort)0x28 : (ushort)0, loaded.cpu.tr, "legacy TR");
        }
        var original = File.ReadAllBytes("roundtrip.snap");
        env.Ata = new AtaDevice(null);
        Throws<InvalidOperationException>(() => SnapshotStore.Save("roundtrip.snap", 999, cpu, env), "failed save");
        Equal(true, original.AsSpan().SequenceEqual(File.ReadAllBytes("roundtrip.snap")), "failed save preserves previous generation");
        Equal(false, File.Exists("roundtrip.snap.tmp"), "failed save cleans temporary");
        var damaged = original.ToArray(); damaged[25] ^= 0x40; File.WriteAllBytes("damaged.snap", damaged);
        Throws<InvalidDataException>(() => SnapshotStore.Load("damaged.snap", Environment(), DiskConfiguration.Discover(Directory.GetCurrentDirectory())), "checksum rejects corruption");
        File.WriteAllBytes("truncated.snap", original[..^20]);
        Throws<InvalidDataException>(() => SnapshotStore.Load("truncated.snap", Environment(), DiskConfiguration.Discover(Directory.GetCurrentDirectory())), "checksum rejects truncation");
    }

    static void Close(AtaDevice ata) => ata.Dispose();

    static void Disks()
    {
        File.WriteAllBytes("sample.vhd", new byte[8192]);
        DiskImage.EnsureOverlay("sample.avhdx", "sample.vhd");
        var disk = new DiskImage("sample.avhdx", true);
        var env = Environment(); var cpu = Cpu(); Populate(env, cpu); env.Ata = new AtaDevice(disk);
        var sector = Enumerable.Repeat((byte)0x41, 512).ToArray(); disk.WriteSector(1, sector, 0);
        env.Ata.WriteReg(0x1F6, 0xE0); env.Ata.WriteReg(0x1F2, 1); env.Ata.WriteReg(0x1F3, 1); env.Ata.WriteReg(0x1F7, 0x20);
        Equal(true, env.Ata.IrqPending, "ATA pending IRQ before snapshot");
        SnapshotStore.Save("paired.snap", 222, cpu, env);
        var expected = StateHash(env, cpu);
        Array.Fill(sector, (byte)0x42); disk.WriteSector(1, sector, 0); disk.Flush(); disk.Close();
        var changed = SHA256.HashData(File.ReadAllBytes("sample.avhdx"));
        var damaged = File.ReadAllBytes("paired.snap"); damaged[^50] ^= 1; File.WriteAllBytes("bad-paired.snap", damaged);
        Throws<InvalidDataException>(() => SnapshotStore.Load("bad-paired.snap", Environment(), DiskConfiguration.Discover(Directory.GetCurrentDirectory())), "bad pair rejected");
        Equal(true, changed.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes("sample.avhdx"))), "bad load leaves working disk intact");
        File.Move("sample.vhd", "original.vhd");
        File.WriteAllBytes("sample.vhdx", new byte[8192]);
        Throws<InvalidDataException>(() => SnapshotStore.Load("paired.snap", Environment(), DiskConfiguration.Discover(Directory.GetCurrentDirectory())), "missing original base rejected");
        Equal(true, changed.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes("sample.avhdx"))), "base failure leaves working disk intact");
        Equal(false, File.Exists("sample.avhdx.restore.tmp"), "base failure cleans staged disk");
        File.Move("original.vhd", "sample.vhd");
        File.Delete("sample.vhdx");
        var restored = Environment(); var loaded = SnapshotStore.Load("paired.snap", restored, DiskConfiguration.Discover(Directory.GetCurrentDirectory()));
        Equal(expected, StateHash(restored, loaded.cpu), "disk and device state roundtrip");
        Equal(true, restored.Ata.IrqPending, "ATA pending IRQ restored");
        Equal(0x4141u, restored.Ata.ReadData(2), "ATA buffered transfer restored");
        restored.Ata.WriteReg(0x1F7, 0x20);
        Equal(0x4141u, restored.Ata.ReadData(2), "embedded disk restored");
        Close(restored.Ata);
        var check = new DiskImage("sample.avhdx"); check.ReadSector(1, sector, 0); check.Close();
        Equal((byte)0x41, sector[0], "disk generation matches saved RAM");
        Equal(false, File.Exists("paired.snap.avhdx"), "snapshot uses single file");
        Legacy("legacy-disk.snap", 6, cpu, env);
        File.Copy("sample.avhdx", "legacy-disk.snap.avhdx");
        var modified = new DiskImage("sample.avhdx", true);
        Array.Fill(sector, (byte)0x43); modified.WriteSector(1, sector, 0); modified.Close();
        var legacyEnv = Environment(); SnapshotStore.Load("legacy-disk.snap", legacyEnv, DiskConfiguration.Discover(Directory.GetCurrentDirectory()));
        legacyEnv.Ata.WriteReg(0x1F7, 0x20);
        Equal(0x4141u, legacyEnv.Ata.ReadData(2), "legacy paired disk restored");
        Close(legacyEnv.Ata);
        File.WriteAllBytes("invalid-disk.tmp", new byte[1]);
        Throws<EndOfStreamException>(() => new DiskImage("invalid-disk.tmp"), "invalid disk");
        using var exclusive = new FileStream("invalid-disk.tmp", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Equal(true, exclusive.CanWrite, "failed disk open releases handle");
    }

    public static void Run()
    {
        Instructions(); Snapshots(); Disks();
        Console.WriteLine($"State/snapshot checks passed: {assertions} assertions");
    }
}
