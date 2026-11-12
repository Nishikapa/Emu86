using Emu86;
using System.Security.Cryptography;
using static Assert;
using static Emu86.Mmu;
using static Emu86.Ports;

static class DiagnosticsTests
{
    // 保存対象に加え、診断が触れてはいけない TLB・統計も比較する。VGA のラッチは SaveState に含まれる。
    static string State(EmuEnvironment env)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        env.SaveState(writer); env.SaveRuntimeState(writer); env.SaveDeviceState(writer); env.Vga.SaveState(writer);
        writer.Write(env.Vga.Dirty);
        writer.Write(env.Paging.Enabled); writer.Write(env.Paging.Pae); writer.Write(env.Paging.WriteProtect); writer.Write(env.Paging.Cr3Base);
        writer.Write(env.Paging.Walks); writer.Write(env.Paging.Flushes); writer.Write(env.Translations.Generation);
        foreach (var values in new[] { env.Paging.TagR, env.Paging.PhysR, env.Paging.TagW, env.Paging.PhysW })
            foreach (var value in values) writer.Write(value);
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }

    static void Unchanged(EmuEnvironment env, Action inspect, string name)
    {
        var before = State(env);
        inspect();
        Equal(before, State(env), name);
    }

    static void Write(EmuEnvironment env, int physical, uint value) => BitConverter.GetBytes(value).CopyTo(env.Ram, physical);

    // 0x400000 とその次のページを、隣接しない物理ページへ割り当てる。Accessed はすべて 0。
    static void Map(EmuEnvironment env, bool pae, bool large = false)
    {
        env.Paging.Enabled = true; env.Paging.Pae = pae; env.Paging.Cr3Base = 0x1000;
        if (pae)
        {
            Write(env, 0x1000, 0x2001);
            Write(env, 0x2010, large ? 0x83u : 0x4003u);
            if (!large) { Write(env, 0x4000, 0x3003); Write(env, 0x4008, 0x5003); }
        }
        else
        {
            Write(env, 0x1004, large ? 0x83u : 0x2003u);
            if (!large) { Write(env, 0x2000, 0x3003); Write(env, 0x2004, 0x5003); }
        }
    }

    static void PagedReads()
    {
        foreach (var pae in new[] { false, true })
        {
            using var env = new EmuEnvironment(memory: new byte[0x10000]);
            Map(env, pae);
            env.Ram[0x3FFC] = (byte)'A'; env.Ram[0x3FFD] = (byte)'B';
            env.Ram[0x3FFF] = 0x11; env.Ram[0x5000] = 0x22; env.Ram[0x5001] = 0x33; env.Ram[0x5002] = 0x44;
            Unchanged(env, () =>
            {
                Equal(0x44332211u, GuestMemory.Dword(0x400FFF)(env), $"cross-page diagnostic dword PAE={pae}");
                Equal("AB", GuestMemory.Ascii(0x400FFC, 10)(env), "diagnostic string");
                Equal((byte)0x22, GuestMemory.Byte(0x401000)(env), "diagnostic byte");
                Throws<PageFaultException>(() => EnvPeekMemoryData32(env, 0x401FFF), "unmapped second page");
                Equal((byte)0xFE, GuestMemory.Byte(0x402000).Or((byte)0xFE)(env), "unmapped diagnostic fallback");
            }, $"page tables, TLB and device state unchanged PAE={pae}");

            // 既存 TLB がある場合は、CPU と同じ変換を観測する。診断自身は充填しない。
            env.Paging.Fill(0x400000, false, 0x6000);
            env.Ram[0x6003] = 0xAB;
            Write(env, pae ? 0x4000 : 0x2000, 0);
            Unchanged(env, () => Equal((byte)0xAB, EnvPeekMemoryData8(env, 0x400003), "existing TLB mapping"), "cached peek is passive");
            env.Paging.Cr3Base = 0xFFFFF000;
            Unchanged(env, () => Throws<PageFaultException>(() => EnvPeekMemoryData8(env, 0x800000), "page table outside RAM"), "invalid table peek is passive");
        }
    }

    static void LargePages()
    {
        foreach (var pae in new[] { false, true })
        {
            using var env = new EmuEnvironment(memory: new byte[0x10000]);
            Map(env, pae, large: true);
            env.Ram[0x1234] = 0xCD;
            Unchanged(env, () => Equal((byte)0xCD, EnvPeekMemoryData8(env, 0x401234), $"large page PAE={pae}"), "large-page Accessed unchanged");
        }
    }

    static void PhysicalAndVga()
    {
        using var env = new EmuEnvironment(memory: new byte[0x100000]);
        env.Ram[0xF0000] = 0x77;
        env.Ram[0xFFFFF] = 0x66;
        void Config(int register, uint value)
        {
            EnvOutPortN(env, 0xCF8, 4, 0x80001000u | (uint)register);
            EnvOutPortN(env, 0xCFC, 4, value);
        }
        Config(0x10, 0xFD000000); Config(0x30, 0xFEBF0001);
        env.Vga.Lfb[0x10] = 0x5A;
        Unchanged(env, () =>
        {
            Equal((byte)0x77, EnvPeekMemoryData8(env, 0xFFFF0000), "BIOS alias");
            Equal(0xFFFFFF66u, EnvPeekMemoryData32(env, 0xFFFFF), "RAM end and open bus");
            Equal((byte)0x5A, EnvPeekMemoryData8(env, 0xFD000010), "PCI framebuffer");
            Equal((byte)0x55, EnvPeekMemoryData8(env, 0xFEBF0000), "PCI ROM");
        }, "physical diagnostic reads unchanged");

        var vga = env.Vga;
        const int planeSize = 0x10000;
        vga.Sequencer[4] = 0x06; vga.Sequencer[2] = 0x0F;
        vga.Graphics[6] = 0x05; vga.Graphics[4] = 2; vga.Graphics[5] = 0;
        for (var plane = 0; plane < 4; plane++)
        {
            vga.Vram[plane * planeSize] = (byte)(0x10 + plane);
            vga.Vram[plane * planeSize + 1] = (byte)(0x20 + plane);
        }
        vga.Read(0xA0000);
        Unchanged(env, () => Equal((byte)0x22, GuestMemory.Byte(0xA0001)(env), "VGA plane read"), "VGA latch unchanged");
        vga.Graphics[5] = 0x08; vga.Graphics[2] = 0; vga.Graphics[7] = 0x0F;
        Unchanged(env, () => Equal((byte)0xDC, vga.Peek(0xA0001), "VGA color compare"), "color-compare latch unchanged");
        vga.Graphics[5] = 1; vga.Write(0xA0002, 0);
        for (var plane = 0; plane < 4; plane++)
            Equal((byte)(0x10 + plane), vga.Vram[plane * planeSize + 2], "write mode 1 still uses original latch");
    }

    static void Logging()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        Map(env, false);
        env.Ram[0x3FFF] = 0x11; env.Ram[0x5000] = 0x22; env.Ram[0x5001] = 0x33; env.Ram[0x5002] = 0x44;
        var cpu = new CPU { code32 = true, eip = 0x400FFF, esp = 0x400FFC, ebp = 0x400FFC, idt_limit = 0x7FF };
        CPU._cr0.setter(cpu)(0x80000001);
        using var output = new StringWriter();
        var diagnostics = new RunDiagnostics(new DiagnosticOptions { NoTrace = true, FaultLogMin = 0 }, env, cpu, output, null, 0);
        Unchanged(env, () =>
        {
            diagnostics.Fault(Fault.GeneralProtection(0), 1);
            diagnostics.ReportStop(1);
            WindowsDiagnostics.LogEntry(env, cpu, 1, output);
            WindowsDiagnostics.Caller(env, cpu);
        }, "fault, stop and Windows logs leave memory and devices unchanged");
        Equal(true, output.ToString().Contains("opcode 11 22 33 44 00 00"), "fault logging still reads across pages");
        Equal(true, output.ToString().Contains("stack=11000000 00443322"), "fault logging still reads the stack");
    }

    public static (string Name, Action Run)[] Cases =>
    [
        ("paged-reads", PagedReads),
        ("large-pages", LargePages),
        ("physical-vga", PhysicalAndVga),
        ("logging", Logging),
    ];
}
