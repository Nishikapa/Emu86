using Emu86;
using System.Security.Cryptography;
using System.Text;
using static Emu86.Conversions;
using static Emu86.Ports;
using static Emu86.Mmu;
using static Emu86.Interrupts;
using static Assert;

static class SnapshotTests
{
    static EmuEnvironment Environment() => new(memory: new byte[0x10000]);
    static CPU Cpu() => new() { eip = 0x100, code32 = true, stack32 = true, eax = 0x87654321, ebx = 0x3000, esp = 0x2002, eflags = 0x203 };

    static void Page(EmuEnvironment env, int linear, uint physical) =>
        BitConverter.GetBytes(physical | 3).CopyTo(env.Ram, 0xA000 + linear * 4);

    static void Paging(EmuEnvironment env, CPU cpu)
    {
        BitConverter.GetBytes(0xA003u).CopyTo(env.Ram, 0x9000);
        Page(env, 0, 0);
        CPU._cr0.setter(cpu)(0x80010001);
        CPU._cr3.setter(cpu)(0x9000);
        env.Paging.Sync(cpu);
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
            if (fast) Ir.SingleStep(env); // IR で 1 命令ずつ(コードを直接書き換えるたびに InvalidateAll)
            var cpu = Cpu();
            byte[] invalid = [0x66, 0x67, 0x64, 0xF0, 0x0F, 0xFF];
            invalid.CopyTo(env.Ram, 0x100);
            var initial = CpuState(cpu);
            Equal(false, executor.Step(env, cpu).IsSuccess, "unsupported opcode");
            Equal(initial, CpuState(cpu), "unsupported instruction rollback");
            Equal(false, cpu.lock_prefix, "LOCK cleanup");
            byte[] move = [0xB8, 0x78, 0x56, 0x34, 0x12];
            move.CopyTo(env.Ram, 0x100);
            env.Translations.InvalidateAll();
            Equal(true, executor.Step(env, cpu).IsSuccess, "instruction after unsupported opcode");
            Equal(0x12345678u, cpu.eax, "no leaked operand prefix");

            cpu = Cpu();
            Paging(env, cpu);
            env.Ram[0x100] = 0x66;
            env.Ram[0x101] = 0x50;
            env.Translations.InvalidateAll();
            initial = CpuState(cpu);
            Throws<PageFaultException>(() => executor.Step(env, cpu), "PUSH fault");
            Equal(initial, CpuState(cpu), "PUSH rollback");
            Equal(true, env.Paging.Enabled, "paging mirror after rollback");
            Page(env, 2, 0x5000);
            env.Paging.Flush();
            Equal(true, executor.Step(env, cpu).IsSuccess, "PUSH retry");
            Equal(0x2000u, cpu.esp, "PUSH stack after retry");
            Equal((ushort)0x4321, BitConverter.ToUInt16(env.Ram, 0x5000), "PUSH value after retry");

            env = Environment(); cpu = Cpu(); Paging(env, cpu);
            if (fast) Ir.SingleStep(env);
            Page(env, 1, 0x5000); Page(env, 3, 0x6000);
            env.Ram[0x100] = 0xF3; env.Ram[0x101] = 0xA4;
            env.Ram[0x6000] = 0x11; env.Ram[0x6001] = 0x22;
            cpu.ecx = 2; cpu.esi = 0x3000; cpu.edi = 0x1FFF;
            Throws<PageFaultException>(() => executor.Step(env, cpu), "REP fault");
            Equal(0x100u, cpu.eip, "REP restart EIP");
            Equal(1u, cpu.ecx, "REP remaining count");
            Equal(0x3001u, cpu.esi, "REP source progress");
            Equal(0x2000u, cpu.edi, "REP destination progress");
            env.Ram[0x6000] = 0x99;
            Page(env, 2, 0x7000); env.Paging.Flush();
            Equal(true, executor.Step(env, cpu).IsSuccess, "REP retry");
            Equal((byte)0x11, env.Ram[0x5fff], "completed REP iteration is not replayed");
            Equal((byte)0x22, env.Ram[0x7000], "remaining REP iteration");
            Equal(0u, cpu.ecx, "REP completion");

            initial = CpuState(cpu);
            Throws<PageFaultException>(() => executor.Execute(env, cpu, (context, state, opcodes) =>
            {
                state.eax = 999; state.fpu_st[0] = 88; state.fs_prefix = true;
                CPU._cr3.setter(state)(0xB000); context.Paging.Sync(state);
                context.Ram[0x8000] = 0xAB;
                throw new PageFaultException(0xDEAD, 2);
            }), "mid-instruction state fault");
            Equal(initial, CpuState(cpu), "CPU and FPU rollback");
            Equal(0x9000u, env.Paging.Cr3Base, "CR3 mirror rollback");
            Equal((byte)0xAB, env.Ram[0x8000], "memory side effects retained");
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
        env.Ram[0x4321] = 0x5A; env.IoPort[0x4321] = 0x12;
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
        env.Vga.Vram[0x12345] = 0x5A; env.Vga.Crtc[0x0E] = 0x12;
        EnvOutPort(env, 0x3C8, 7); EnvOutPort(env, 0x3C9, 0x3F);
        EnvOutPort(env, 0x3C4, 2); EnvOutPort(env, 0x3C5, 0x05);
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
        Equal((byte)0x5A, restored.Vga.Vram[0x12345], "VRAM restored");
        Equal((byte)0x12, restored.Vga.Crtc[0x0E], "CRTC restored");
        Equal((byte)0x3F, restored.Vga.Dac[21], "DAC restored");
        Equal("7,1,2,5", $"{restored.Vga.DacWriteIndex},{restored.Vga.DacWritePhase},{restored.Vga.SequencerIndex},{restored.Vga.Sequencer[2]}", "VGA indices restored");
        Equal(true, restored.Vga.Dirty, "restored display marked dirty");
        Equal(0x98765432u, restored.Dr[3], "debug register restored");
        for (var version = 2; version <= 6; version++)
        {
            Legacy("legacy.snap", version, cpu, env);
            restored = Environment(); loaded = SnapshotStore.Load("legacy.snap", restored, DiskConfiguration.Discover(Directory.GetCurrentDirectory()));
            Equal(9876L, loaded.count, "legacy count");
            Equal(cpu.eax, loaded.cpu.eax, "legacy CPU");
            Equal((byte)0x5A, restored.Ram[0x4321], "legacy RAM offset");
            Equal(version >= 3 ? env.Tsc : 9876UL, restored.Tsc, "legacy TSC");
            Equal(version >= 5 ? 1.25 : 0.0, loaded.cpu.fpu_st[3], "legacy FPU");
            Equal(version >= 6 ? (ushort)0x28 : (ushort)0, loaded.cpu.tr, "legacy TR");
            Equal((byte)0, restored.Vga.Vram[0x12345], "legacy snapshot resets VGA");
            Equal((byte)0x03, restored.Vga.Sequencer[2], "legacy snapshot restores text mode defaults");
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

    // すべての保存対象に既定値と異なる値を入れる(Populate に加えて CPU の残り・VGA・マウス・UART・ATA)。
    static void PopulateAll(EmuEnvironment env, CPU cpu)
    {
        Populate(env, cpu);
        CPU._cs.setter(cpu)(0x0008); CPU._ds.setter(cpu)(0x0023); CPU._es.setter(cpu)(0x002B);
        CPU._ss.setter(cpu)(0x0010); CPU._fs.setter(cpu)(0x0030); CPU._gs.setter(cpu)(0x0038);
        cpu.cs_base = 0x1000; cpu.ds_base = 0x2000; cpu.es_base = 0x3000; cpu.ss_base = 0x4000; cpu.fs_base = 0xFFDFF000; cpu.gs_base = 0x6000;
        cpu.idt_limit = 0x7FF; cpu.idt_base = 0x8003F400; cpu.gdt_limit = 0x3FF; cpu.gdt_base = 0x8003F000;
        CPU._cr0.setter(cpu)(0x8001003B); cpu.cr2 = 0xC0DEC0DE; CPU._cr3.setter(cpu)(0x39000); CPU._cr4.setter(cpu)(0x2D1);
        cpu.fpu_cw = 0x027F; cpu.fpu_sw = 0x3800; cpu.fpu_st[0] = -2.5; cpu.fpu_st[7] = 1e300;
        cpu.ebp = 0xF9E7C000; cpu.ecx = 0x11; cpu.edx = 0x22; cpu.esi = 0x33; cpu.edi = 0x44;
        env.Ram[0x9F] = 0xA5; env.IoPort[0x60] = 0x1C; env.Cmos[0x7F] = 0x7E;
        env.Msrs[0x1B] = 0xFEE00900; env.Dr[7] = 0x401;
        env.Vga.Vram[1] = 0x11; env.Vga.Vram[0x10001] = 0x22; env.Vga.Vram[0x20001] = 0x33; env.Vga.Vram[0x30001] = 0x44;
        env.Vga.Read(0xB8002); // ラッチを読み込む
        EnvOutPort(env, 0x3C0, 0x11); EnvOutPort(env, 0x3C0, 0x09); EnvOutPort(env, 0x3C0, 0x14);
        EnvOutPort(env, 0x3C2, 0xE3); EnvOutPort(env, 0x3C6, 0x3F); EnvOutPort(env, 0x3C7, 5); EnvInPort(env, 0x3C9);
        EnvOutPort(env, 0x3CE, 4); EnvOutPort(env, 0x3CF, 2); EnvOutPort(env, 0x3D4, 0x13); EnvOutPort(env, 0x3D5, 0x28);
        EnvOutPort(env, 0x3DA, 1);
        foreach (var value in new byte[] { 0xF3, 200, 0xF3, 100, 0xF3, 80 }) env.Mouse.Command(value);
        env.Mouse.Enabled = true; env.Mouse.Remote = true; env.Mouse.Resolution = 1; env.Mouse.Scaling2 = true;
        env.Mouse.PendingCommand = 0xE8; env.Mouse.LastButtons = 5; env.Mouse.Output.Enqueue(0x08); env.Mouse.Output.Enqueue(0xFE);
        env.Com1.Write(3, 0x83); env.Com1.Write(0, 0x0C); env.Com1.Write(1, 0x00); env.Com1.Write(3, 0x03);
        env.Com1.Write(1, 0x0F); env.Com1.Write(2, 0xC7); env.Com1.Write(4, 0x0B); env.Com1.Write(7, 0x5A);
        env.Com1.Rx.Enqueue(0x41); env.Com1.Rx.Enqueue(0x42);
        env.Com2.Write(3, 0x80); env.Com2.Write(0, 0x06); env.Com2.Write(3, 0x1B); env.Com2.Write(1, 0x02); env.Com2.Write(4, 0x1F);
        env.Com2.Rx.Enqueue(0x7F);
        var ata = new AtaDevice(null);
        ata.WriteReg(0x1F1, 0x11); ata.WriteReg(0x1F2, 2); ata.WriteReg(0x1F3, 0x21); ata.WriteReg(0x1F4, 0x43);
        ata.WriteReg(0x1F5, 0x65); ata.WriteReg(0x1F6, 0xE7); ata.WriteReg(0x1F7, 0x30); // WRITE SECTORS: 1 セクタ分のバッファを待つ
        ata.WriteData(4, 0x44434241); ata.IrqPending = true;
        env.Ata = ata;
    }

    static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    // v10 形式のバイト列を固定する。直列化の実装を差し替えても、同じ状態からは同じバイト列が出ること。
    static void Layout()
    {
        var env = Environment(); var cpu = Cpu(); PopulateAll(env, cpu);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            cpu.WriteTo(writer); env.SaveState(writer); env.SaveRuntimeState(writer); cpu.SaveRuntimeState(writer);
            env.SaveDeviceState(writer); env.Vga.SaveState(writer); env.Mouse.SaveState(writer); env.Com1.SaveState(writer); env.Com2.SaveState(writer);
        }
        Equal(396430L, stream.Length, "v10 state length");
        Equal("8D3A11DE9561C91B38A1C89400429347267F1691DA614404369C483BCE3BB32E", Sha256(stream.ToArray()), "v10 state layout (CMOS defaults with the RTC, 2026-10-28)");
        var restored = Environment();
        using (var reader = new BinaryReader(new MemoryStream(stream.ToArray()), Encoding.UTF8))
        {
            var loaded = CPU.ReadFrom(reader); restored.LoadState(reader); restored.LoadRuntimeState(reader, 10, 0); loaded.LoadRuntimeState(reader, 10);
            restored.LoadDeviceState(reader); restored.Vga.LoadState(reader); restored.Mouse.LoadState(reader); restored.Com1.LoadState(reader); restored.Com2.LoadState(reader);
            Equal(reader.BaseStream.Length, reader.BaseStream.Position, "load consumes the whole state");
            using var again = new MemoryStream();
            using var rewriter = new BinaryWriter(again, Encoding.UTF8, leaveOpen: true);
            loaded.WriteTo(rewriter); restored.SaveState(rewriter); restored.SaveRuntimeState(rewriter); loaded.SaveRuntimeState(rewriter);
            restored.SaveDeviceState(rewriter); restored.Vga.SaveState(rewriter); restored.Mouse.SaveState(rewriter); restored.Com1.SaveState(rewriter); restored.Com2.SaveState(rewriter);
            rewriter.Flush();
            Equal(Sha256(stream.ToArray()), Sha256(again.ToArray()), "load then save reproduces the bytes");
        }
        Close(restored.Ata); Close(env.Ata); env.Ata = null;
        SnapshotStore.Save("layout.snap", 777, cpu, env);
        Equal("24DCFB693EEB7B5DA326A8E928DA9C48A02C0836E1E39CA6B73AA69F8DA84D64", Sha256(File.ReadAllBytes("layout.snap")), "v10 file layout");
    }

    // フォルトの統一経路: #GP は例外として命令開始時の状態へ戻り、Raise が #PF/#UD と同じ形で IDT 経由で配送する。
    static void Faults()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var executor = new InstructionExecutor(false);
        // GDT: 0x08 = ring0 32 ビットコード、0x10 = データ。IDT: ベクタ 13 の割り込みゲート → 0008:00003000。
        byte[] code = [0xFF, 0xFF, 0x00, 0x00, 0x00, 0x9A, 0xCF, 0x00];
        byte[] data = [0xFF, 0xFF, 0x00, 0x00, 0x00, 0x92, 0xCF, 0x00];
        code.CopyTo(env.Ram, 0x7008); data.CopyTo(env.Ram, 0x7010);
        byte[] gate = [0x00, 0x30, 0x08, 0x00, 0x00, 0x8E, 0x00, 0x00];
        gate.CopyTo(env.Ram, 0x8000 + 13 * 8);
        var cpu = new CPU { eip = 0x100, code32 = true, stack32 = true, esp = 0x6000, eflags = 0x202, idt_base = 0x8000, idt_limit = 0xFF, gdt_base = 0x7000, gdt_limit = 0x1F };
        CPU._cr0.setter(cpu)(1); CPU._cs.setter(cpu)(0x08); CPU._ss.setter(cpu)(0x10); cpu.cs_base = 0; cpu.ss_base = 0;
        // IRETD の復帰先 CS=000Bh(ring0 の非適合コードに RPL 3)は #GP(selector)。NT の BOP 復帰経路が意図的に使う。
        env.Ram[0x100] = 0xCF;
        BitConverter.GetBytes(0x200u).CopyTo(env.Ram, 0x6000);
        BitConverter.GetBytes(0x0Bu).CopyTo(env.Ram, 0x6004);
        BitConverter.GetBytes(0x202u).CopyTo(env.Ram, 0x6008);
        var before = CpuState(cpu);
        GeneralProtectionException raised = null;
        try { executor.Step(env, cpu); }
        catch (GeneralProtectionException exception) { raised = exception; }
        Equal(true, raised != null, "IRETD to a ring0 segment with RPL 3 raises #GP");
        Equal(new Fault(13, 0x08), raised.Fault, "#GP carries the selector as error code");
        Equal("GENERAL PROTECTION err=8", raised.Message, "#GP message");
        Equal(before, CpuState(cpu), "faulting instruction leaves the CPU at its start");
        Equal(true, raised.Fault.IdtReady(cpu), "IDT holds the #GP gate");
        var delivered = executor.Execute(env, cpu, Raise(raised.Fault));
        Equal(true, delivered.IsSuccess, "#GP delivered");
        Equal((0x3000u, (ushort)0x08, 0x5FF0u), (cpu.eip, cpu.cs, cpu.esp), "handler entered with the error code pushed");
        Equal((0x08u, 0x100u, 0x08u, 0x202u), (Dword(env, 0x5FF0), Dword(env, 0x5FF4), Dword(env, 0x5FF8), Dword(env, 0x5FFC)), "frame: error code, EIP of the faulting instruction, CS, EFLAGS");
        Equal(false, cpu.jf, "interrupt gate clears IF");

        // #PF は従来の形式のメッセージを保ち、CR2 を設定して配送する。#UD はエラーコードなし。
        var pageFault = new PageFaultException(0xDEADB000, 2);
        Equal("PAGE FAULT lin=deadb000 err=2 (not-present, write)", pageFault.Message, "page fault message");
        Equal("lin=deadb000 err=2", pageFault.Fault.Describe(), "page fault summary");
        Equal(new Fault(14, 2, 0xDEADB000), pageFault.Fault, "page fault record");
        cpu.idt_limit = 14 * 8 + 6;
        Equal(false, pageFault.Fault.IdtReady(cpu), "IDT one byte short of the #PF gate");
        cpu.idt_limit = 14 * 8 + 7;
        Equal(true, pageFault.Fault.IdtReady(cpu), "IDT covers the #PF gate");
        Equal((false, true), (Fault.UndefinedOpcode.IdtReady(new CPU { idt_limit = 0xFF }), Fault.UndefinedOpcode.ErrorCode is null), "#UD needs protected mode and has no error code");
        cpu.eip = 0x100; cpu.esp = 0x6000;
        byte[] pageFaultGate = [0x00, 0x40, 0x08, 0x00, 0x00, 0x8E, 0x00, 0x00];
        pageFaultGate.CopyTo(env.Ram, 0x8000 + 14 * 8);
        Equal(true, executor.Execute(env, cpu, Raise(pageFault.Fault)).IsSuccess, "#PF delivered");
        Equal((0xDEADB000u, 0x4000u, 2u), (cpu.cr2, cpu.eip, Dword(env, 0x5FF0)), "#PF sets CR2 and pushes the error code");
    }

    static uint Dword(EmuEnvironment env, int address) => BitConverter.ToUInt32(env.Ram, address);

    // 名前付きのケース(ランナーが順に実行し、失敗したケース名を報告する)。
    public static (string Name, Action Run)[] Cases =>
    [
        ("instructions", Instructions),
        ("snapshots", Snapshots),
        ("disks", Disks),
        ("layout", Layout),
        ("faults", Faults),
    ];
}
