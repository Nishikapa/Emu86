using Emu86;
using System.Security.Cryptography;
using static Emu86.Ext;

// VGA デバイス(VRAM の振り分け・レジスタ・描画)、V86 モードの遷移、キーボード IRQ1 の回帰テスト。
static class VgaTests
{
    static int assertions;

    static void Equal<T>(T expected, T actual, string name)
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{name}: expected {expected}, got {actual}");
    }

    static void Out(EmuEnvironment env, int index, int data, int value)
    {
        EnvOutPort(env, index, (byte)data);
        EnvOutPort(env, index + 1, (byte)value);
    }

    static void Attribute(EmuEnvironment env, int index, int value)
    {
        EnvInPort(env, 0x3DA);
        EnvOutPort(env, 0x3C0, (byte)index);
        EnvOutPort(env, 0x3C0, (byte)value);
    }

    // CPU の通常のメモリアクセス経路(両コア)で 0xB8000 への書き込みが VRAM へ向かうことを確認する。
    static void MemoryWindow()
    {
        foreach (var fast in new[] { false, true })
        {
            using var env = new EmuEnvironment(memory: new byte[0x10000]);
            var cpu = new CPU { eip = 0x100, code32 = true, stack32 = true, esp = 0x8000, eflags = 2 };
            byte[] code = [0xC6, 0x05, 0x00, 0x80, 0x0B, 0x00, 0x41,          // mov byte [0xB8000], 'A'
                           0x66, 0xC7, 0x05, 0x02, 0x80, 0x0B, 0x00, 0x1F, 0x42, // mov word [0xB8002], 0x421F
                           0xA0, 0x00, 0x80, 0x0B, 0x00];                     // mov al, [0xB8000]
            code.CopyTo(env.OneMegaMemory_, 0x100);
            var executor = new InstructionExecutor(fast);
            for (var step = 0; step < 3; step++) Equal(true, executor.Step(env, cpu).IsSuccess, $"vga memory step {step} fast={fast}");
            Equal((byte)0x41, env.Vga.Vram[0], "text character in plane 0");
            Equal((byte)0x1F, env.Vga.Vram[1], "second cell character");
            Equal((byte)0x42, env.Vga.Vram[0x10001], "second cell attribute in plane 1");
            Equal((byte)0x41, cpu.al, "read back through the window");
            Equal((byte)0x41, EnvGetMemoryData8(env, 0xB8000), "byte read");
            Equal((byte)0xFF, EnvGetMemoryData8(env, 0xA0000), "unmapped part of the window reads 0xFF");
            Equal(true, env.OneMegaMemory_.All(value => value == 0 || Array.IndexOf(code, value) >= 0), "RAM untouched");
        }
    }

    static void PlanarAndChain4()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var vga = env.Vga;
        Out(env, 0x3C4, 4, 0x06); Out(env, 0x3CE, 6, 0x05); Out(env, 0x3CE, 5, 0x00);
        Out(env, 0x3C4, 2, 0x03);
        vga.Write(0xA0000, 0xFF);
        Equal((byte)0xFF, vga.Vram[0], "plane 0 written");
        Equal((byte)0xFF, vga.Vram[0x10000], "plane 1 written");
        Equal((byte)0x00, vga.Vram[0x20000], "plane 2 masked");
        Out(env, 0x3CE, 4, 1); Equal((byte)0xFF, vga.Read(0xA0000), "read map select 1");
        Out(env, 0x3CE, 4, 2); Equal((byte)0x00, vga.Read(0xA0000), "read map select 2");
        Out(env, 0x3CE, 5, 0x08); Out(env, 0x3CE, 2, 0x03); Out(env, 0x3CE, 7, 0x0F);
        Equal((byte)0xFF, vga.Read(0xA0000), "color compare match");
        Out(env, 0x3CE, 2, 0x01);
        Equal((byte)0x00, vga.Read(0xA0000), "color compare mismatch");
        Out(env, 0x3CE, 5, 0x02); Out(env, 0x3C4, 2, 0x0F); Out(env, 0x3CE, 8, 0x0F);
        vga.Read(0xA0001);
        vga.Write(0xA0001, 0x0A);
        Equal((byte)0x00, vga.Vram[1], "write mode 2 plane 0");
        Equal((byte)0x0F, vga.Vram[0x10001], "write mode 2 plane 1 under bit mask");
        Equal((byte)0x0F, vga.Vram[0x30001], "write mode 2 plane 3 under bit mask");
        Out(env, 0x3CE, 5, 0x00); Out(env, 0x3CE, 8, 0xFF); Out(env, 0x3CE, 3, 0x09); // ROR 1, AND with latch
        vga.Read(0xA0001);
        vga.Write(0xA0001, 0x1F);
        Equal((byte)0x0F, vga.Vram[0x10001], "write mode 0 rotate and AND");
        Out(env, 0x3CE, 3, 0x00); Out(env, 0x3CE, 5, 0x01);
        vga.Read(0xA0001);
        vga.Write(0xA0005, 0x00);
        Equal((byte)0x0F, vga.Vram[0x10005], "write mode 1 copies latches");
        Out(env, 0x3CE, 5, 0x03); Out(env, 0x3CE, 0, 0x02); Out(env, 0x3CE, 8, 0xF0);
        vga.Read(0xA0006);
        vga.Write(0xA0006, 0x3C);
        Equal((byte)0x30, vga.Vram[0x10006], "write mode 3 set/reset through data mask");
        Equal((byte)0x00, vga.Vram[0x6], "write mode 3 cleared plane");
        Out(env, 0x3CE, 5, 0x00); Out(env, 0x3CE, 8, 0xFF);
        Out(env, 0x3C4, 4, 0x0E);
        vga.Write(0xA0005, 0x55);
        Equal((byte)0x55, vga.Vram[0x10001], "chain4 plane 1 unit 1");
        Equal((byte)0x55, vga.Read(0xA0005), "chain4 read");
        Equal(true, VgaDevice.IsPort(0x3C0) && VgaDevice.IsPort(0x3DA) && !VgaDevice.IsPort(0x3BC), "port set");
        EnvOutPort(env, 0x3BC, 0xAA);
        Equal((byte)0xAA, EnvInPort(env, 0x3BC), "parallel port still passes through");
    }

    static void Registers()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var vga = env.Vga;
        EnvOutPort(env, 0x3C4, 2);
        Equal((byte)2, EnvInPort(env, 0x3C4), "sequencer index readback");
        Equal((byte)3, EnvInPort(env, 0x3C5), "default map mask");
        Attribute(env, 0x10, 0x01);
        Equal((byte)1, vga.Attribute[0x10], "attribute data after index");
        EnvInPort(env, 0x3DA);
        EnvOutPort(env, 0x3C0, 0x12);
        Equal((byte)0x0F, EnvInPort(env, 0x3C1), "attribute read after flip-flop reset");
        EnvOutPort(env, 0x3C8, 1);
        foreach (var component in new byte[] { 0x3F, 0x00, 0x15 }) EnvOutPort(env, 0x3C9, component);
        Equal((byte)2, vga.DacWriteIndex, "DAC write auto-increment");
        EnvOutPort(env, 0x3C7, 1);
        Equal("3F,0,15", string.Join(",", Enumerable.Range(0, 3).Select(_ => EnvInPort(env, 0x3C9).ToString("X"))), "DAC readback");
        Equal((byte)2, vga.DacReadIndex, "DAC read auto-increment");
        Out(env, 0x3D4, 0x11, 0x80); Out(env, 0x3D4, 0x00, 0x12);
        Equal((byte)0x5F, vga.Crtc[0], "CRTC 0-7 protected");
        Out(env, 0x3D4, 0x11, 0x00); Out(env, 0x3D4, 0x00, 0x12);
        Equal((byte)0x12, vga.Crtc[0], "CRTC unprotected");
        Out(env, 0x3B4, 0x00, 0x5F);
        Equal((byte)0x5F, EnvInPort(env, 0x3D5), "monochrome CRTC alias");
        EnvOutPort(env, 0x3C2, 0x63);
        Equal((byte)0x63, EnvInPort(env, 0x3CC), "misc output readback");
        env.Tsc = 0;
        var status = EnvInPort(env, 0x3DA);
        env.Tsc = 1 << 12;
        Equal(true, (status & 8) != (EnvInPort(env, 0x3DA) & 8), "vertical retrace toggles");
    }

    static void Rendering()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var vga = env.Vga;
        Equal((640, 400), vga.Resolution(), "text resolution");
        Equal("text 80x25", vga.ModeDescription(), "text description");
        vga.FallbackFont = new byte[256 * 16];
        vga.FallbackFont['A' * 16] = 0x80;
        vga.Write(0xB8000, (byte)'A'); vga.Write(0xB8001, 0x1F);
        Out(env, 0x3D4, 0x0A, 0x20); // カーソル非表示
        var buffer = new uint[640 * 480];
        Equal((640, 400), vga.Render(buffer), "text render size");
        Equal(0xFFFFFFFFu, buffer[0], "foreground pixel");
        Equal(0xFF0000AAu, buffer[1], "background pixel (blue)");
        Equal(0xFF000000u, buffer[8], "empty cell");
        Equal(false, vga.Dirty, "render clears dirty");
        vga.Write(0xB8002, (byte)'B');
        Equal(true, vga.Dirty, "write sets dirty");
        Array.Fill(vga.Vram, (byte)0x80, 0x20000 + 'A' * 32, 1); // プレーン 2 のフォントを優先
        vga.FallbackFont['A' * 16] = 0x00;
        vga.Render(buffer);
        Equal(0xFFFFFFFFu, buffer[0], "loaded font preferred");

        Out(env, 0x3C4, 2, 0x0F); Out(env, 0x3C4, 4, 0x06); Out(env, 0x3CE, 5, 0x00); Out(env, 0x3CE, 6, 0x05);
        Out(env, 0x3D4, 0x11, 0x0C); Out(env, 0x3D4, 1, 0x4F); Out(env, 0x3D4, 7, 0x0B); Out(env, 0x3D4, 9, 0x40);
        Out(env, 0x3D4, 0x12, 0xDF); Out(env, 0x3D4, 0x13, 0x28);
        Attribute(env, 0x10, 0x01);
        Equal((640, 480), vga.Resolution(), "mode 12h resolution");
        Equal("640x480 16 colors", vga.ModeDescription(), "mode 12h description");
        Out(env, 0x3C4, 2, 0x04);
        vga.Write(0xA0000 + 10 * 80 + 1, 0x80); // (8, 10) をパレット 4(赤)に
        Equal((640, 480), vga.Render(buffer), "mode 12h render size");
        Equal(0xFFAA0000u, buffer[10 * 640 + 8], "planar pixel color");
        Equal(0xFF000000u, buffer[10 * 640 + 9], "planar neighbour");

        Out(env, 0x3C4, 2, 0x0F); Out(env, 0x3C4, 4, 0x0E); Out(env, 0x3CE, 5, 0x40);
        Out(env, 0x3D4, 7, 0x1F); Out(env, 0x3D4, 9, 0x41); Out(env, 0x3D4, 0x12, 0x8F);
        Attribute(env, 0x10, 0x41);
        Equal((320, 200), vga.Resolution(), "mode 13h resolution");
        EnvOutPort(env, 0x3C8, 7);
        foreach (var component in new byte[] { 0x3F, 0x20, 0x10 }) EnvOutPort(env, 0x3C9, component);
        vga.Write(0xA0000 + 5 * 320 + 3, 7);
        Equal((320, 200), vga.Render(buffer), "mode 13h render size");
        Equal(0xFFFF8241u, buffer[5 * 320 + 3], "chain4 pixel uses DAC");
        Equal("320x200 256 colors", vga.ModeDescription(), "mode 13h description");
    }

    static void Write32(EmuEnvironment env, int address, uint value) => BitConverter.GetBytes(value).CopyTo(env.OneMegaMemory_, address);

    // ring0 の IRETD で V86 モードへ入り、V86 コードの INT 21h が DPL0 ゲートのため #GP(0x10A) として
    // ハンドラへ(V86 形式のフレームで)配送され、IRETD で V86 へ戻り、BOP(C4 C4)の #UD で終了することを確認する。
    static void VirtualMode()
    {
        foreach (var fast in new[] { false, true })
        {
            using var env = new EmuEnvironment(memory: new byte[0x40000]);
            var cpu = new CPU { eip = 0x100, esp = 0x6000, code32 = true, stack32 = true, eflags = 2,
                idt_base = 0x1000, idt_limit = 0x7FF, gdt_base = 0x500, gdt_limit = 0x1F, tr = 0x18 };
            CPU._cr0.setter(cpu)(1);
            CPU._cs.setter(cpu)(8); cpu.cs_base = 0;
            CPU._ss.setter(cpu)(16); cpu.ss_base = 0;
            Write32(env, 0x508, 0x0000FFFF); Write32(env, 0x50C, 0x00CF9A00);
            Write32(env, 0x510, 0x0000FFFF); Write32(env, 0x514, 0x00CF9200);
            Write32(env, 0x518, 0x06000067); Write32(env, 0x51C, 0x00008900);
            Write32(env, 0x604, 0x7000); Write32(env, 0x608, 0x10);
            Write32(env, 0x1000 + 6 * 8, 0x00080480); Write32(env, 0x1004 + 6 * 8, 0x00008E00);
            Write32(env, 0x1000 + 13 * 8, 0x00080400); Write32(env, 0x1004 + 13 * 8, 0x00008E00);
            Write32(env, 0x1000 + 0x21 * 8, 0x00080500); Write32(env, 0x1004 + 0x21 * 8, 0x00008E00);
            byte[] enter = [0x68, 0x00, 0x30, 0x00, 0x00, 0x68, 0x00, 0x30, 0x00, 0x00, 0x68, 0x00, 0x30, 0x00, 0x00, 0x68, 0x00, 0x30, 0x00, 0x00,
                0x68, 0x00, 0x30, 0x00, 0x00, 0x68, 0xFE, 0x0F, 0x00, 0x00, 0x68, 0x02, 0x32, 0x02, 0x00, 0x68, 0x00, 0x20, 0x00, 0x00, 0x6A, 0x00, 0xCF];
            byte[] v86 = [0xB8, 0x34, 0x12, 0x50, 0xCD, 0x21, 0xC4, 0xC4, 0x90, 0xEB, 0xFE];
            byte[] gp = [0x8B, 0x0C, 0x24, 0x83, 0x44, 0x24, 0x04, 0x02, 0x83, 0xC4, 0x04, 0xCF];
            byte[] ud = [0xBB, 0x0D, 0x60, 0x00, 0x00, 0xEB, 0xFE];
            enter.CopyTo(env.OneMegaMemory_, 0x100); v86.CopyTo(env.OneMegaMemory_, 0x20000);
            gp.CopyTo(env.OneMegaMemory_, 0x400); ud.CopyTo(env.OneMegaMemory_, 0x480);
            using var output = new StringWriter();
            var options = RunOptions.Parse(fast ? ["--limit", "40", "--noirq", "--notrace"] : ["--limit", "40", "--noirq", "--notrace", "--slow"]);
            EmulationRunner.Run(options, env, cpu, 0, null, output, TextWriter.Null);
            var label = fast ? " (fast)" : " (slow)";
            Equal(0x600Du, cpu.ebx, "reached #UD handler" + label + ": " + output);
            Equal(0x10Au, cpu.ecx, "#GP error code for INT 21h from V86" + label);
            Equal((ushort)0x1234, cpu.ax, "V86 code ran" + label);
            Equal((ushort)0x1234, BitConverter.ToUInt16(env.OneMegaMemory_, 0x30FFC), "V86 stack uses SS*16" + label);
            Equal(false, cpu.vm, "VM cleared in handler" + label);
            Equal((ushort)8, cpu.cs, "handler code segment" + label);
            Equal(0, cpu.ds | cpu.es | cpu.fs | cpu.gs, "data segments nulled on V86 trap" + label);
            Equal(0x6FDCu, cpu.esp, "V86 trap frame size" + label);
            uint[] frame = Enumerable.Range(0, 9).Select(index => BitConverter.ToUInt32(env.OneMegaMemory_, 0x6FDC + index * 4)).ToArray();
            Equal("6,2000,ffc,3000,3000,3000,3000,3000", string.Join(",", new[] { frame[0], frame[1], frame[3], frame[4], frame[5], frame[6], frame[7], frame[8] }.Select(value => value.ToString("x"))), "V86 frame contents" + label);
            Equal(0x20000u, frame[2] & 0x20000, "pushed EFLAGS keeps VM" + label);
        }
    }

    // 注入したスキャンコードが 8042 の出力バッファへ移り、IRQ1 がリアルモードの IVT 経由で配送されることを確認する。
    static void KeyboardInterrupt()
    {
        foreach (var enabled in new[] { true, false })
        {
            using var env = new EmuEnvironment(memory: new byte[0x10000]);
            var cpu = new CPU { eip = 0x100, esp = 0x8000, eflags = 2 };
            byte[] main = [0xFB, 0xEB, 0xFE];                   // sti; jmp $
            byte[] handler = [0xE4, 0x60, 0x88, 0xC3, 0xCF];    // in al,60h; mov bl,al; iret
            main.CopyTo(env.OneMegaMemory_, 0x100); handler.CopyTo(env.OneMegaMemory_, 0x600);
            Write32(env, 9 * 4, 0x00000600);
            env.KbdCmdByte = (byte)(enabled ? 0x45 : 0x44);
            env.Keyboard.Inject(0x1E, 0x9E);
            EmulationRunner.Run(RunOptions.Parse(["--limit", "20100", "--notrace"]), env, cpu, 0, null, TextWriter.Null, TextWriter.Null);
            Equal(enabled ? (byte)0x1E : (byte)0, cpu.bl, enabled ? "make code delivered" : "interrupt disabled by command byte");
            Equal(enabled ? 2 : 0, env.PicMasterIsr & 2, "IRQ1 in-service until EOI");
            Equal(enabled ? "0,1" : "1,1", $"{env.Keyboard.Injected.Count},{env.KbdOut.Count}", "pump moves one byte at a time");
        }
    }

    // INT 19h で VGA BIOS スタブが導入され、リアルモードの INT 10h(モード設定・文字出力・カーソル・情報取得)が
    // C000:0060 の UD2 経由で処理されることを確認する。
    static void BiosStub()
    {
        foreach (var fast in new[] { true, false })
        {
            using var env = new EmuEnvironment(memory: new byte[0x100000]);
            var cpu = new CPU { eip = 0x100, esp = 0x8000, eflags = 2 };
            byte[] main = [0xCD, 0x19];                                                   // int 19h
            byte[] boot = [0xB8, 0x03, 0x00, 0xCD, 0x10,                                   // mov ax,3; int 10h
                           0xB8, 0x41, 0x0E, 0xCD, 0x10, 0xB8, 0x42, 0x0E, 0xCD, 0x10,     // teletype 'A', 'B'
                           0xB4, 0x0F, 0xCD, 0x10, 0xA2, 0x00, 0x07,                       // get mode -> [0x700] = al
                           0xB4, 0x03, 0xCD, 0x10, 0x88, 0x16, 0x01, 0x07,                 // get cursor -> [0x701] = dl
                           0xB8, 0x00, 0x1A, 0xCD, 0x10, 0x88, 0x1E, 0x02, 0x07,           // DCC -> [0x702] = bl
                           0xB8, 0x92, 0x00, 0xCD, 0x10,                                   // mode 12h (bit7: VRAM を消さない)
                           0xEB, 0xFE];
            main.CopyTo(env.OneMegaMemory_, 0x100); boot.CopyTo(env.OneMegaMemory_, 0x7C00);
            Write32(env, 0x19 * 4, 0x00007C00);
            var options = RunOptions.Parse(fast ? ["--limit", "60", "--noirq", "--notrace"] : ["--limit", "60", "--noirq", "--notrace", "--slow"]);
            using var output = new StringWriter();
            EmulationRunner.Run(options, env, cpu, 0, null, output, TextWriter.Null);
            var label = fast ? " (fast)" : " (slow)";
            Equal(true, VgaBiosStub.IsInstalled(env), "stub installed at INT 19h" + label + ": " + output);
            Equal(0xAA55, BitConverter.ToUInt16(env.OneMegaMemory_, 0xC0000), "option ROM signature" + label);
            Equal(0, Enumerable.Range(0, VgaBiosStub.RomSize).Sum(index => env.OneMegaMemory_[0xC0000 + index]) & 0xFF, "ROM checksum" + label);
            Equal((byte)'A', env.Vga.Vram[0], "teletype first character" + label);
            Equal((byte)'B', env.Vga.Vram[1], "teletype second character" + label);
            Equal((byte)0x07, env.Vga.Vram[0x10001], "text attribute preserved" + label);
            Equal("3,2,8", string.Join(",", env.OneMegaMemory_.Skip(0x700).Take(3)), "get mode / cursor column / display combination code" + label);
            Equal((byte)0x12, env.OneMegaMemory_[0x449], "BDA mode" + label);
            Equal((640, 480), env.Vga.Resolution(), "mode 12h via BIOS" + label);
            Equal((byte)'A', env.Vga.Vram[0], "no-clear flag keeps VRAM" + label);
            Equal((ushort)0, cpu.cs, "returned from the stub to the boot code" + label);
        }
    }

    // PCI の VGA デバイス(00:02.0)と拡張 ROM BAR: サイズ問い合わせ、有効化後の ROM 窓の読み出し、PCIR 構造。
    static void PciRom()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        uint Config(int reg) { EnvOutPortN(env, 0xCF8, 4, 0x80000000u | (2u << 11) | (uint)reg); return EnvInPortN(env, 0xCFC, 4); }
        void Write(int reg, uint value) { EnvOutPortN(env, 0xCF8, 4, 0x80000000u | (2u << 11) | (uint)reg); EnvOutPortN(env, 0xCFC, 4, value); }
        uint Read(uint address, int size) => Enumerable.Range(0, size).Aggregate(0u, (value, index) => value | (uint)EnvGetMemoryData8(env, address + (uint)index) << (8 * index));
        Equal(0x11111234u, Config(0x00), "VGA vendor/device");
        Equal(0x030000u, Config(0x08) >> 8, "VGA class code");
        Equal(0u, Config(0x30), "ROM BAR disabled by default");
        Write(0x30, 0xFFFFF800);
        Equal(0xFFFF0000u, Config(0x30), "ROM BAR size query (64KB, enable bit cleared)");
        Equal((byte)0xFF, EnvGetMemoryData8(env, 0xFEBF0000), "ROM window hidden while disabled");
        Write(0x30, 0xFEBF0001);
        Equal(true, env.Pci.RomContains(0xFEBF0000) && env.Pci.RomContains(0xFEBFFFFF) && !env.Pci.RomContains(0xFEC00000), "ROM window bounds");
        Equal(0xAA55u, Read(0xFEBF0000, 2), "ROM signature through the window");
        Equal((byte)(VgaBiosStub.RomSize / 512), EnvGetMemoryData8(env, 0xFEBF0002), "ROM size in 512-byte blocks");
        var pcir = Read(0xFEBF0018, 2);
        Equal("PCIR", System.Text.Encoding.ASCII.GetString(Enumerable.Range(0, 4).Select(index => EnvGetMemoryData8(env, 0xFEBF0000u + pcir + (uint)index)).ToArray()), "PCI data structure");
        Equal(0x11111234u, Read(0xFEBF0000u + pcir + 4, 4), "PCIR vendor/device");
        Equal((byte)0x80, EnvGetMemoryData8(env, 0xFEBF0000u + pcir + 0x15), "PCIR last image");
        Equal((byte)0xFF, EnvGetMemoryData8(env, 0xFEBF0000u + VgaBiosStub.RomSize), "beyond the ROM image");
        Equal(0, Enumerable.Range(0, VgaBiosStub.RomSize).Sum(index => VgaBiosStub.Rom[index]) & 0xFF, "ROM checksum");
        Equal("B070D8AB830F68C14EF33FBAB98D315E9ED5F08B5D27D4B1717C4FA0B3D3D080", Convert.ToHexString(SHA256.HashData(VgaBiosStub.Rom)), "ROM bytes");
        Write(0x30, 0xFEBF0000);
        Equal((byte)0xFF, EnvGetMemoryData8(env, 0xFEBF0000), "ROM window hidden again");
        // 旧スナップショット(VGA デバイス追加前、PCI 関数 4 つ)の PCI 状態は読み込め、VGA は初期値のまま。
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x80000920u); writer.Write(1);
            writer.Write((1 << 3) | 1); var ide = new byte[256]; ide[0x20] = 0x01; ide[0x21] = 0xD0; writer.Write(ide);
        }
        stream.Position = 0;
        using (var reader = new BinaryReader(stream)) env.Pci.LoadState(reader);
        Equal(0xD000u, env.Pci.IdeBmBase, "older PCI state restored");
        Equal(0x11111234u, Config(0x00), "VGA function kept its defaults");
    }

    // ring0 の IRETD で復帰先 CS の RPL が記述子の DPL と合わない(000Bh = ring0 コードに RPL 3)と、スタックを
    // 動かさずに #GP(セレクタ)になる。NT は V86 の BIOS 呼び出しからの復帰にこの #GP を使う。
    static void IretPrivilegeCheck()
    {
        foreach (var fast in new[] { true, false })
        {
            using var env = new EmuEnvironment(memory: new byte[0x10000]);
            var cpu = new CPU { eip = 0x100, esp = 0x6000, code32 = true, stack32 = true, eflags = 2,
                idt_base = 0x1000, idt_limit = 0x7FF, gdt_base = 0x500, gdt_limit = 0x1F };
            CPU._cr0.setter(cpu)(1);
            CPU._cs.setter(cpu)(8); cpu.cs_base = 0;
            CPU._ss.setter(cpu)(16); cpu.ss_base = 0;
            Write32(env, 0x508, 0x0000FFFF); Write32(env, 0x50C, 0x00CF9A00);
            Write32(env, 0x510, 0x0000FFFF); Write32(env, 0x514, 0x00CF9200);
            Write32(env, 0x518, 0x0000FFFF); Write32(env, 0x51C, 0x00CFFA00); // 0x18: ring3 code
            Write32(env, 0x1000 + 13 * 8, 0x00080400); Write32(env, 0x1004 + 13 * 8, 0x00008E00);
            // push 0x23; push 0x5000; push 0x202; push 0x0B; push 0x200; iretd; (never reached) hlt
            byte[] code = [0x6A, 0x23, 0x68, 0x00, 0x50, 0x00, 0x00, 0x68, 0x02, 0x02, 0x00, 0x00, 0x6A, 0x0B, 0x68, 0x00, 0x02, 0x00, 0x00, 0xCF, 0xF4];
            byte[] handler = [0x8B, 0x0C, 0x24, 0x8B, 0x5C, 0x24, 0x04, 0x8B, 0x54, 0x24, 0x10, 0xEB, 0xFE]; // ecx=err; ebx=eip; edx=[esp+10h](元の IRET 枠の EIP); jmp $
            code.CopyTo(env.OneMegaMemory_, 0x100); handler.CopyTo(env.OneMegaMemory_, 0x400);
            var options = RunOptions.Parse(fast ? ["--limit", "12", "--noirq", "--notrace"] : ["--limit", "12", "--noirq", "--notrace", "--slow"]);
            EmulationRunner.Run(options, env, cpu, 0, null, TextWriter.Null, TextWriter.Null);
            var label = fast ? " (fast)" : " (slow)";
            Equal(0x8u, cpu.ecx, "#GP error code is the selector" + label);
            Equal(0x113u, cpu.ebx, "fault EIP points at the IRETD" + label);
            Equal(0x200u, cpu.edx, "IRET frame left intact right above the #GP frame" + label);
            Equal((ushort)8, cpu.cs, "still ring0" + label);
            Equal(0x200u, BitConverter.ToUInt32(env.OneMegaMemory_, 0x5FEC), "frame EIP intact" + label);
            // 正しい ring3 復帰(CS=1B)は通る。
            using var ok = new EmuEnvironment(memory: new byte[0x10000]);
            var cpu2 = new CPU { eip = 0x100, esp = 0x6000, code32 = true, stack32 = true, eflags = 2, gdt_base = 0x500, gdt_limit = 0x1F, idt_base = 0x1000, idt_limit = 0x7FF };
            CPU._cr0.setter(cpu2)(1); CPU._cs.setter(cpu2)(8); cpu2.cs_base = 0; CPU._ss.setter(cpu2)(16); cpu2.ss_base = 0;
            Array.Copy(env.OneMegaMemory_, 0x500, ok.OneMegaMemory_, 0x500, 0x20);
            byte[] code2 = [0x6A, 0x23, 0x68, 0x00, 0x50, 0x00, 0x00, 0x68, 0x02, 0x02, 0x00, 0x00, 0x6A, 0x1B, 0x68, 0x00, 0x02, 0x00, 0x00, 0xCF];
            code2.CopyTo(ok.OneMegaMemory_, 0x100); ok.OneMegaMemory_[0x200] = 0xEB; ok.OneMegaMemory_[0x201] = 0xFE;
            EmulationRunner.Run(options, ok, cpu2, 0, null, TextWriter.Null, TextWriter.Null);
            Equal((ushort)0x1B, cpu2.cs, "ring3 return accepted" + label);
            Equal(0x5000u, cpu2.esp, "outer stack loaded" + label);
        }
    }

    // PS/2 マウス: 8042 経由のコマンド応答、IntelliMouse 切替、パケット生成、ステータス bit5、IRQ12 の配送。
    static void Mouse()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        byte[] Drain() { var bytes = new List<byte>(); while ((EnvInPort(env, 0x64) & 1) != 0) bytes.Add(EnvInPort(env, 0x60)); return [.. bytes]; }
        string Hex(byte[] bytes) => string.Join(",", bytes.Select(b => b.ToString("X2")));
        void ToMouse(byte value) { EnvOutPort(env, 0x64, 0xD4); EnvOutPort(env, 0x60, value); }
        EnvOutPort(env, 0x64, 0x60); EnvOutPort(env, 0x60, 0x47); // キーボード/AUX 割り込み許可、変換有効(XP の設定)
        EnvOutPort(env, 0x64, 0xA8);
        Equal((byte)0x47, env.KbdCmdByte, "aux enabled in command byte");
        ToMouse(0xFF);
        Equal((byte)0x35, EnvInPort(env, 0x64), "status shows aux data");
        Equal("FA,AA,00", Hex(Drain()), "reset response");
        ToMouse(0xF2);
        Equal("FA,00", Hex(Drain()), "standard mouse id");
        foreach (var rate in new byte[] { 200, 100, 80 }) { ToMouse(0xF3); Drain(); ToMouse(rate); Drain(); }
        ToMouse(0xF2);
        Equal("FA,03", Hex(Drain()), "IntelliMouse id after the magic sequence");
        ToMouse(0xE9);
        Equal(4, Drain().Length, "status request");
        env.Mouse.Move(5, 3, -1, 1);
        Equal(false, env.Keyboard.Pump(), "no packet before reporting is enabled");
        ToMouse(0xF4); Drain();
        Equal(true, env.Keyboard.Pump(), "packet moved into the aux buffer");
        Equal(true, env.Keyboard.AuxIrqAsserted, "IRQ12 asserted");
        Equal("09,05,03,FF", Hex(Drain()), "IntelliMouse packet");
        env.Mouse.Move(-300, -2, 0, 0);
        env.Keyboard.Pump();
        Equal("78,00,FE,00", Hex(Drain()), "overflow and negative packet");
        env.Keyboard.Inject(0x1E); env.Mouse.Move(1, 1, 0, 0);
        env.Keyboard.Pump();
        Equal(true, env.Keyboard.IrqAsserted && !env.Keyboard.AuxIrqAsserted, "keyboard byte goes first");
        Equal((byte)0x15, EnvInPort(env, 0x64), "status without aux bit for keyboard data");
        Drain();
        EnvOutPort(env, 0x64, 0xA7);
        Equal(false, env.Keyboard.Pump(), "aux disabled blocks packets");
        EnvOutPort(env, 0x64, 0xA8);
        Equal(true, env.Keyboard.Pump(), "aux re-enabled");
        Drain();
        // IRQ12: リアルモードの IVT 0x74 でパケットの先頭バイトを受け取る。
        using var irq = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = new CPU { eip = 0x100, esp = 0x8000, eflags = 2, edi = 0x700 };
        byte[] main = [0xFB, 0xEB, 0xFE];
        byte[] handler = [0xE4, 0x60, 0xAA, 0xB0, 0x20, 0xE6, 0xA0, 0xE6, 0x20, 0xCF]; // in al,60h; stosb; EOI slave+master; iret
        main.CopyTo(irq.OneMegaMemory_, 0x100); handler.CopyTo(irq.OneMegaMemory_, 0x600);
        Write32(irq, 0x74 * 4, 0x00000600);
        irq.KbdCmdByte = 0x47; // キーボード/AUX 割り込み許可、変換有効
        irq.Mouse.Enabled = true;
        irq.Mouse.Move(2, 1, 0, 2);
        EmulationRunner.Run(RunOptions.Parse(["--limit", "40100", "--notrace"]), irq, cpu, 0, null, TextWriter.Null, TextWriter.Null);
        Equal("0A,02,01", Hex(irq.OneMegaMemory_.Skip(0x700).Take(3).ToArray()), "right button packet via IRQ12");
        Equal(0x703u, cpu.edi, "exactly three bytes delivered");
        Equal(0, irq.Mouse.Output.Count, "all packet bytes consumed");
        // スナップショット v9 にマウス状態が入る。
        env.Mouse.Move(1, 2, 0, 0); env.Keyboard.Pump();
        SnapshotStore.Save("mouse.snap", 7, new CPU(), env);
        using var restored = new EmuEnvironment(memory: new byte[0x10000]);
        SnapshotStore.Load("mouse.snap", restored, DiskConfiguration.Discover(Directory.GetCurrentDirectory()));
        Equal((byte)3, restored.Mouse.Id, "mouse id restored");
        Equal(true, restored.Mouse.Enabled, "mouse reporting restored");
        Equal(4, restored.Mouse.Output.Count, "aux buffer restored");
    }

    // システムリセット: 8042 の 0xFE パルス/出力ポート、0xCF9 で要求が立ち、ランナーが実行を抜ける。
    static void ResetRequest()
    {
        foreach (var (name, code) in new (string, byte[])[]
        {
            ("pulse", [0xB0, 0xFE, 0xE6, 0x64, 0xEB, 0xFE]),                                 // mov al,FEh; out 64h,al
            ("output port", [0xB0, 0xD1, 0xE6, 0x64, 0xB0, 0x02, 0xE6, 0x60, 0xEB, 0xFE]),   // D1 then 02 (bit0=0)
            ("cf9", [0xB0, 0x06, 0x66, 0xBA, 0xF9, 0x0C, 0x00, 0x00, 0xEE, 0xEB, 0xFE]),     // mov al,6; mov edx,CF9h; out dx,al
        })
        {
            using var env = new EmuEnvironment(memory: new byte[0x10000]);
            var cpu = new CPU { eip = 0x100, esp = 0x8000, eflags = 2, code32 = true, stack32 = true };
            code.CopyTo(env.OneMegaMemory_, 0x100);
            using var output = new StringWriter();
            var count = EmulationRunner.Run(RunOptions.Parse(["--limit", "30000", "--notrace", "--noirq"]), env, cpu, 0, null, output, TextWriter.Null);
            Equal(true, env.ResetRequested, "reset flagged: " + name);
            Equal(true, count < 30000, "run left early: " + name);
            Equal(true, output.ToString().Contains("[system] reset requested"), "reset reported: " + name);
        }
        using var none = new EmuEnvironment(memory: new byte[0x10000]);
        byte[] keep = [0xB0, 0xD1, 0xE6, 0x64, 0xB0, 0x03, 0xE6, 0x60, 0xEB, 0xFE]; // 出力ポート 03 (bit0=1) はリセットしない
        keep.CopyTo(none.OneMegaMemory_, 0x100);
        var count2 = EmulationRunner.Run(RunOptions.Parse(["--limit", "30000", "--notrace", "--noirq"]), none,
            new CPU { eip = 0x100, esp = 0x8000, eflags = 2, code32 = true, stack32 = true }, 0, null, TextWriter.Null, TextWriter.Null);
        Equal(30000L, count2, "A20 write does not reset");
        Equal(long.MaxValue, RunOptions.Parse(["--limit", "max"]).Limit, "limit max is unlimited");
        Equal(0L, RunOptions.Parse(["--limit", "0"]).Limit, "limit 0 stays zero");
        Equal(long.MaxValue, RunOptions.Parse(["--snapshot-interval", "0"]).SnapshotInterval, "interval 0 saves only at the end");
        Equal(12345L, RunOptions.Parse(["--snapshot-interval", "12345"]).SnapshotInterval, "snapshot interval");
    }

    // 16550 UART: レジスタ、ループバック、KDCOM のポート検出(MCR ループバックと MSR)、名前付きパイプの往復、IRQ4。
    static void Serial()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        Equal((byte)0x60, EnvInPort(env, 0x3FD), "LSR idle: THRE and TEMT");
        Equal((byte)0xB0, EnvInPort(env, 0x3FE), "MSR idle");
        Equal((byte)0x01, EnvInPort(env, 0x3FA), "IIR no interrupt");
        EnvOutPort(env, 0x3FB, 0x80); EnvOutPort(env, 0x3F8, 0x0C); EnvOutPort(env, 0x3F9, 0x00); EnvOutPort(env, 0x3FB, 0x03);
        Equal("0C,00", $"{env.Com1.Dll:X2},{env.Com1.Dlm:X2}", "divisor latch written through DLAB");
        EnvOutPort(env, 0x3FC, 0x10);
        Equal((byte)0x00, EnvInPort(env, 0x3FE), "loopback with no OUT bits: no modem status");
        EnvOutPort(env, 0x3FC, 0x14);
        Equal((byte)0x40, EnvInPort(env, 0x3FE), "loopback OUT1 -> RI (KDCOM probe)");
        EnvOutPort(env, 0x3FC, 0x1F);
        Equal((byte)0xF0, EnvInPort(env, 0x3FE), "loopback reflects all modem control bits");
        EnvOutPort(env, 0x3F8, 0x5A);
        Equal((byte)0x61, EnvInPort(env, 0x3FD), "loopback byte ready");
        Equal((byte)0x5A, EnvInPort(env, 0x3F8), "loopback byte read");
        EnvOutPort(env, 0x3FC, 0x03);
        EnvOutPort(env, 0x3FF, 0xA5);
        Equal((byte)0xA5, EnvInPort(env, 0x3FF), "scratch register");
        EnvOutPort(env, 0x2F8, 0x42);
        Equal((byte)0x60, EnvInPort(env, 0x2FD), "COM2 without a host drops the byte");
        // 名前付きパイプ: クライアントが読み書きし、UART の Pump で受信 FIFO に入る。
        var name = "emu86-test-" + Guid.NewGuid().ToString("N");
        using (var host = new NamedPipeSerialHost(name))
        {
            env.Com1.Host = host;
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut);
            client.Connect(10000);
            for (var wait = 0; wait < 100 && !host.Connected; wait++) Thread.Sleep(10);
            Equal(true, host.Connected, "pipe client connected");
            foreach (var value in "KD\n"u8.ToArray()) EnvOutPort(env, 0x3F8, value);
            env.Com1.Pump();
            var received = new byte[3];
            var reading = client.ReadAsync(received, 0, 3);
            Equal(true, reading.Wait(5000), "pipe client received data in time");
            Equal(3, reading.Result, "three bytes from the guest");
            Equal("KD\n", System.Text.Encoding.ASCII.GetString(received), "guest bytes reach the pipe client");
            client.Write("ok"u8.ToArray()); client.Flush();
            for (var wait = 0; wait < 100 && env.Com1.Rx.Count < 2; wait++) { Thread.Sleep(10); env.Com1.Pump(); }
            Equal((byte)0x61, EnvInPort(env, 0x3FD), "data ready from the pipe");
            Equal("ok", $"{(char)EnvInPort(env, 0x3F8)}{(char)EnvInPort(env, 0x3F8)}", "pipe bytes read through RBR");
            EnvOutPort(env, 0x3F9, 0x01);
            client.Write("!"u8.ToArray()); client.Flush();
            for (var wait = 0; wait < 100 && env.Com1.Rx.Count < 1; wait++) { Thread.Sleep(10); env.Com1.Pump(); }
            Equal(true, env.Com1.IrqAsserted, "receive interrupt asserted");
            Equal((byte)0x04, EnvInPort(env, 0x3FA), "IIR received data available");
            EnvInPort(env, 0x3F8);
            Equal(false, env.Com1.IrqAsserted, "interrupt dropped after reading");
            env.Com1.Host = null;
        }
        // IRQ4: リアルモードの IVT 0x0C でバイトを受け取る。
        using var irq = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = new CPU { eip = 0x100, esp = 0x8000, eflags = 2, edi = 0x700 };
        byte[] main = [0xFB, 0xEB, 0xFE];
        byte[] handler = [0xBA, 0xF8, 0x03, 0xEC, 0xAA, 0xB0, 0x20, 0xE6, 0x20, 0xCF]; // mov dx,3F8h; in al,dx; stosb; EOI; iret
        main.CopyTo(irq.OneMegaMemory_, 0x100); handler.CopyTo(irq.OneMegaMemory_, 0x600);
        Write32(irq, 0x0C * 4, 0x00000600);
        irq.Com1.Ier = 0x01; irq.Com1.Rx.Enqueue((byte)'x'); irq.Com1.Rx.Enqueue((byte)'y');
        EmulationRunner.Run(RunOptions.Parse(["--limit", "20100", "--notrace"]), irq, cpu, 0, null, TextWriter.Null, TextWriter.Null);
        Equal("xy", System.Text.Encoding.ASCII.GetString(irq.OneMegaMemory_, 0x700, 2), "bytes delivered through IRQ4");
        // スナップショット v10 に UART の状態が入る。
        env.Com2.Ier = 0x03; env.Com2.Rx.Enqueue(0x11);
        SnapshotStore.Save("serial.snap", 3, new CPU(), env);
        using var restored = new EmuEnvironment(memory: new byte[0x10000]);
        SnapshotStore.Load("serial.snap", restored, DiskConfiguration.Discover(Directory.GetCurrentDirectory()));
        Equal((byte)0x03, restored.Com2.Ier, "UART IER restored");
        Equal(1, restored.Com2.Rx.Count, "UART receive FIFO restored");
    }

    // NTLDR メニューの自動操作: 画面テキストの検出、ハイライト行の追跡、キー注入の順序。
    static void BootMenuAutomation()
    {
        using var env = new EmuEnvironment(memory: new byte[0x100000]);
        void Line(int row, string text, bool highlighted = false)
        {
            for (var column = 0; column < 80; column++)
            {
                env.Vga.Vram[row * 80 + column] = (byte)(column < text.Length ? text[column] : ' ');
                env.Vga.Vram[0x10000 + row * 80 + column] = (byte)(highlighted ? 0x70 : 0x07);
            }
        }
        var automation = new BootAutomation();
        using var output = new StringWriter();
        automation.Step(env, 1000, output);
        Equal(0, env.Keyboard.Injected.Count, "nothing before the boot sector");
        VgaBiosStub.Install(env);
        automation.Step(env, 2000, output);
        Equal(2, env.Keyboard.Injected.Count, "F8 pressed after boot");
        while (env.Keyboard.Injected.TryDequeue(out _)) { }
        Line(0, "Windows Advanced Options Menu");
        Line(2, "    Safe Mode", highlighted: true);
        Line(3, "    Debugging Mode");
        Line(4, "    Return to OS Choices Menu");
        env.OneMegaMemory_[0x41A] = 0x24; env.OneMegaMemory_[0x41C] = 0x28;
        automation.Step(env, 1_000_000, output);
        Equal("0x1E,0x1E", $"0x{env.OneMegaMemory_[0x41A]:X2},0x{env.OneMegaMemory_[0x41C]:X2}", "BIOS key buffer emptied when the menu appears");
        automation.Step(env, 1_300_000, output);
        Equal("E0,50,E0,D0", string.Join(",", env.Keyboard.Injected.Select(b => b.ToString("X2"))), "down arrow sent while another line is highlighted");
        while (env.Keyboard.Injected.TryDequeue(out _)) { }
        Line(2, "    Safe Mode"); Line(3, "    Debugging Mode", highlighted: true);
        automation.Step(env, 1_600_000, output);
        Equal("1C,9C", string.Join(",", env.Keyboard.Injected.Select(b => b.ToString("X2"))), "Enter sent on Debugging Mode");
        while (env.Keyboard.Injected.TryDequeue(out _)) { }
        Line(0, "Please select the operating system to start:"); Line(2, "    Microsoft Windows XP Professional", highlighted: true);
        Line(3, ""); Line(4, "");
        automation.Step(env, 2_000_000, output);
        Equal("1C,9C", string.Join(",", env.Keyboard.Injected.Select(b => b.ToString("X2"))), "Enter sent on the OS menu");
        Equal(true, automation.Completed, "automation finished");
        Equal(true, output.ToString().Contains("Debugging Mode selected"), "progress logged");
    }

    // 自動操作の待ち時間: メニュー最終行の再判定間隔(KeyInterval/4)、↓ の上限(40 回)、OS メニューのタイムアウト(41×KeyInterval)。
    static void BootMenuTimeouts()
    {
        using var env = new EmuEnvironment(memory: new byte[0x100000]);
        void Line(int row, string text, bool highlighted = false)
        {
            for (var column = 0; column < 80; column++)
            {
                env.Vga.Vram[row * 80 + column] = (byte)(column < text.Length ? text[column] : ' ');
                env.Vga.Vram[0x10000 + row * 80 + column] = (byte)(highlighted ? 0x70 : 0x07);
            }
        }
        void Drain() { while (env.Keyboard.Injected.TryDequeue(out _)) { } }
        VgaBiosStub.Install(env);
        using var output = new StringWriter();
        var automation = new BootAutomation();
        automation.Step(env, 0, output);
        Equal(2, env.Keyboard.Injected.Count, "F8 pressed immediately after boot");
        Drain();
        automation.Step(env, 100_000, output);
        Equal(0, env.Keyboard.Injected.Count, "F8 repeats only after the key interval");
        Line(0, "Windows Advanced Options Menu");
        Line(2, "    Safe Mode", highlighted: true);
        automation.Step(env, 200_000, output);
        Equal("SelectDebugging", automation.Status, "menu detected on the next key interval");
        automation.Step(env, 400_000, output);
        Line(4, "    Return to OS Choices Menu");
        automation.Step(env, 440_000, output);
        Equal(0, env.Keyboard.Injected.Count, "last menu line re-checked only after a quarter interval");
        automation.Step(env, 450_000, output);
        Equal(4, env.Keyboard.Injected.Count, "down arrow sent once the last line is visible");
        var at = 450_000L;
        for (var presses = 2; presses <= 40; presses++)
        {
            Drain();
            at += 200_000;
            automation.Step(env, at, output);
            Equal(4, env.Keyboard.Injected.Count, $"down arrow {presses}");
        }
        Drain();
        automation.Step(env, at + 200_000, output);
        Equal(0, env.Keyboard.Injected.Count, "no key after giving up");
        Equal(true, automation.Completed, "gives up after 40 presses");
        Equal(true, output.ToString().Contains("giving up: highlighted line is 'Safe Mode'"), "giving up is logged");

        using var quiet = new StringWriter();
        var timeout = new BootAutomation();
        Line(2, "    Safe Mode");
        Line(3, "    Debugging Mode", highlighted: true);
        timeout.Step(env, 0, quiet);
        Equal(0, env.Keyboard.Injected.Count, "menu already shown: no F8");
        timeout.Step(env, 200_000, quiet);
        Equal("1C,9C", string.Join(",", env.Keyboard.Injected.Select(b => b.ToString("X2"))), "Enter sent on Debugging Mode");
        Drain();
        timeout.Step(env, 8_400_000, quiet);
        Equal(false, timeout.Completed, "still waiting for the OS menu at the deadline");
        timeout.Step(env, 8_410_000, quiet);
        Equal(true, timeout.Completed, "assumes automatic selection after the deadline");
        Equal(0, env.Keyboard.Injected.Count, "no key on timeout");
        Equal(true, quiet.ToString().Contains("no operating system menu, assuming automatic selection (instr 8410000)"), "timeout is logged");
    }

    // 既定 DAC: EGA 互換の 64 色(リセット時と mode 3)と、mode 13h の 256 色(16 色 + グレー 16 + 色相環 216 + 黒 8)。
    // 期待値は従来のループ実装の式から手で計算したもの。
    static void Palette()
    {
        using var env = new EmuEnvironment(memory: new byte[0x100000]);
        string Rgb(int index) => $"{env.Vga.Dac[index * 3]:X2},{env.Vga.Dac[index * 3 + 1]:X2},{env.Vga.Dac[index * 3 + 2]:X2}";
        Equal("00,00,00", Rgb(0), "reset: black");
        Equal("00,00,2A", Rgb(1), "reset: blue is bit0");
        Equal("2A,00,00", Rgb(4), "reset: red is bit2");
        Equal("15,15,15", Rgb(0x38), "reset: intensity bits add 0x15");
        Equal("3F,3F,3F", Rgb(0x3F), "reset: white");
        Equal("00,00,00", Rgb(64), "reset: colors above 64 are black");
        VgaBiosStub.SetMode(env, 0x13);
        Equal("00,00,2A", Rgb(1), "mode 13h: entry 1 is EGA blue");
        Equal("15,15,15", Rgb(8), "mode 13h: entry 8 is EGA dark gray (palette index 0x38)");
        Equal("2A,2A,2A", Rgb(7), "mode 13h: entry 7 is EGA light gray (palette index 0x07)");
        Equal("3F,3F,3F", Rgb(15), "mode 13h: entry 15 is white");
        Equal("00,00,00", Rgb(16), "mode 13h: first gray");
        Equal("3F,3F,3F", Rgb(31), "mode 13h: last gray");
        Equal("10,10,3F", Rgb(32), "mode 13h: hue ring starts at blue");
        Equal("1B,10,3F", Rgb(33), "mode 13h: blue toward magenta (integer interpolation)");
        Equal("3F,10,3F", Rgb(36), "mode 13h: magenta");
        Equal("10,1C,3F", Rgb(55), "mode 13h: cyan toward blue");
        Equal("3F,1F,1F", Rgb(56 + 8), "mode 13h: second saturation level, red");
        Equal("07,07,1C", Rgb(32 + 72), "mode 13h: second brightness level starts at dark blue");
        Equal("00,00,00", Rgb(248), "mode 13h: trailing entries are black");
        VgaBiosStub.SetMode(env, 0x03);
        Equal("2A,00,00", Rgb(4), "mode 3: back to the EGA table");
        Equal("00,00,00", Rgb(100), "mode 3: no 256-color entries");
    }

    // INT 10h のサービス表を直接呼ぶ: 文字と属性の書き込み(09h)、セルの読み出し(08h)、窓のスクロール(06h)、
    // カーソル形状(01h)、属性レジスタと DAC の設定(10h)、EGA 情報(12h)、VESA(4Fh)、未定義の機能は何もしない。
    static void BiosServices()
    {
        using var env = new EmuEnvironment(memory: new byte[0x100000]);
        VgaBiosStub.Install(env);
        var vga = env.Vga;
        var cpu = new CPU { eax = 0x0941, ebx = 0x1F, ecx = 3 }; // AH=09 AL='A' BL=属性 CX=3
        env.OneMegaMemory_[0x450] = 2; env.OneMegaMemory_[0x451] = 1;    // カーソル (行 1, 列 2)
        VgaBiosStub.Service(env, cpu);
        var cell = 1 * 80 + 2;
        Equal("41,41,41", string.Join(",", Enumerable.Range(0, 3).Select(offset => vga.Vram[cell + offset].ToString("x2"))), "AH=09 writes the character");
        Equal("1f,1f,1f", string.Join(",", Enumerable.Range(0, 3).Select(offset => vga.Vram[0x10000 + cell + offset].ToString("x2"))), "AH=09 writes the attribute");
        cpu.eax = 0x0800;
        VgaBiosStub.Service(env, cpu);
        Equal((ushort)0x1F41, cpu.ax, "AH=08 reads the character and attribute under the cursor");
        cpu.eax = 0x0601; cpu.ebx = 0x0700; cpu.ecx = 0x0100; cpu.edx = 0x024F; // 1 行上へ、属性 07、窓 (1,0)-(2,79)
        VgaBiosStub.Service(env, cpu);
        Equal("20,20,20", string.Join(",", Enumerable.Range(0, 3).Select(offset => vga.Vram[cell + offset].ToString("x2"))), "AH=06 scrolls the window up (cleared cells are spaces)");
        Equal("20,07", $"{vga.Vram[2 * 80 + 2]:x2},{vga.Vram[0x10000 + 2 * 80 + 2]:x2}", "AH=06 clears the bottom row with the attribute");
        cpu.eax = 0x0100; cpu.ecx = 0x0D0E;
        VgaBiosStub.Service(env, cpu);
        Equal((0x0D, 0x0E), ((int)vga.Crtc[0x0A], (int)vga.Crtc[0x0B]), "AH=01 sets the cursor shape");
        cpu.eax = 0x1000; cpu.ebx = 0x3F05; // AL=00 BL=5 BH=0x3F
        VgaBiosStub.Service(env, cpu);
        Equal((byte)0x3F, vga.Attribute[5], "AH=10 AL=00 sets a palette register");
        cpu.eax = 0x1010; cpu.ebx = 0x0007; cpu.ecx = 0x2A15; cpu.edx = 0x3F00; // 色 7 = R 3F, G 2A, B 15
        VgaBiosStub.Service(env, cpu);
        Equal("3f,2a,15", string.Join(",", vga.Dac.Skip(7 * 3).Take(3).Select(component => component.ToString("x2"))), "AH=10 AL=10 sets a DAC entry");
        cpu.eax = 0x1200; cpu.ebx = 0x0010;
        VgaBiosStub.Service(env, cpu);
        Equal((ushort)0x0003, cpu.bx, "AH=12 BL=10 reports color and 256KB");
        Equal((ushort)0x0009, cpu.cx, "AH=12 BL=10 reports the switches");
        cpu.eax = 0x4F00;
        VgaBiosStub.Service(env, cpu);
        Equal((ushort)0x014F, cpu.ax, "AH=4F reports VESA unsupported");
        cpu.eax = 0x1234; cpu.ebx = 0x5678;
        VgaBiosStub.Service(env, cpu);
        Equal((0x1234u, 0x5678u), (cpu.eax, cpu.ebx), "an undefined function leaves the registers alone");
    }

    // 画面の PNG 保存: テキストモード(80×25、8×16 のセル)の表示が 640×400 の画像になり、文字の前景色がその位置に出る。
    static void Capture()
    {
        using var env = new EmuEnvironment(memory: new byte[0x100000]);
        VgaBiosStub.Install(env);
        env.Vga.Vram[0] = (byte)'X'; env.Vga.Vram[0x10000] = 0x0F; env.Vga.Vram[1] = 0; // 左上に白の 'X'、隣は空(文字 0)
        ScreenCapture.Save(env.Vga, "capture.png");
        using var image = new System.Drawing.Bitmap("capture.png");
        Equal((640, 400), (image.Width, image.Height), "text mode capture size");
        var lit = Enumerable.Range(0, 16).SelectMany(y => Enumerable.Range(0, 8).Select(x => image.GetPixel(x, y).ToArgb() & 0xFFFFFF)).Count(color => color != 0);
        Equal(true, lit is > 10 and < 100, $"the glyph lights some pixels of the first cell ({lit})");
        Equal(0, image.GetPixel(12, 8).ToArgb() & 0xFFFFFF, "a cell holding character 0 is black");
    }

    public static void Run()
    {
        MemoryWindow(); PlanarAndChain4(); Registers(); Rendering(); VirtualMode(); KeyboardInterrupt(); BiosStub(); BiosServices(); Capture(); PciRom(); IretPrivilegeCheck(); Mouse(); ResetRequest(); Serial(); BootMenuAutomation(); BootMenuTimeouts(); Palette();
        Console.WriteLine($"VGA/V86/keyboard assertions passed: {assertions}");
    }
}
