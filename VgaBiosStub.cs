using static Emu86.CPU;

namespace Emu86;

// 最小限の VGA BIOS(INT 10h)。本物の VGA BIOS を同梱しない代わりに、2KB のオプション ROM 形式の
// スタブを 0xC0000 に置き、IVT のベクタ 10h をその中のハンドラへ向ける。ハンドラは UD2(0F 0B)を
// 実行するだけで、ランナーがそれを「未対応命令」として受け取る前にここへ回し、C# 側で機能を処理して
// から次の IRET へ進める。リアルモード(SeaBIOS/NTLDR)でも、Windows の HAL/vga.sys が V86 モードで
// 行う BIOS 呼び出し(IVT 経由で C000:0060 へ反射される)でも同じ経路になる。
//
// 同梱の SeaBIOS(QEMU 向けビルド)はレガシー ROM 領域を走査せず PCI/fw_cfg からしか VGA ROM を
// 取り込まないため、POST 完了後の INT 19h(ブート)時点で導入する。それ以降の INT 10h が対象になる。
//
// 対応機能: モード設定(00h: 3/12h/13h と近傍のモード)、カーソル(01h/02h/03h)、文字(08h/09h/0Ah/0Eh)、
// スクロール(06h/07h)、モード取得(0Fh)、EGA 情報(12h)、DCC(1Ah)、パレット(10h の一部)。VESA(4Fh)は非対応を返す。
public static class VgaBiosStub
{
    public const uint RomBase = 0xC0000;
    public const int RomSize = 2048;
    const int InitOffset = 0x20;
    const int PcirOffset = 0x40;
    public const int ServiceOffset = 0x60;

    public static readonly byte[] Rom = BuildRom();

    // ROM の構造: 署名と jmp short init、PCI データ構造(SeaBIOS は ROM のベンダ/デバイス ID が取り込み元の
    // PCI デバイスと一致することを確認してから init を呼ぶ)、init(IVT 10h をハンドラへ向けて mode 3 を設定)、
    // ハンドラ(ud2 でサービス呼び出し、iret)、識別文字列、チェックサム。
    static byte[] BuildRom() => new RomBuilder(RomSize)
        .Db(0x55, 0xAA, RomSize / 512)                  // 署名とサイズ(512 バイト単位)
        .JmpShort("init")
        .At(0x18).Dw("pcir")                            // PCI データ構造へのポインタ
        .At(PcirOffset).Label("pcir")
        .Ascii("PCIR")
        .Dw(0x1234).Dw(0x1111)                          // ベンダ、デバイス
        .Dw(0x0000).Dw(0x0018).Db(0x00)                 // VPD ポインタ、構造体長、リビジョン
        .Db(0x00, 0x00, 0x03)                           // クラス: VGA 互換(prog-if, sub, base)
        .Dw(RomSize / 512).Dw(0x0000)                   // イメージ長(512 バイト単位)、コードリビジョン
        .Db(0x00, 0x80).Dw(0x0000)                      // コード種別 x86、最後のイメージ
        .At(InitOffset).Label("init")
        .Db(0x1E)                                       // push ds
        .Db(0x31, 0xC0)                                 // xor ax, ax
        .Db(0x8E, 0xD8)                                 // mov ds, ax
        .Db(0xC7, 0x06, 0x40, 0x00).Dw("handler")       // mov word [0x40], handler
        .Db(0xC7, 0x06, 0x42, 0x00).Dw(0xC000)          // mov word [0x42], 0xC000
        .Db(0x1F)                                       // pop ds
        .Db(0xB8, 0x03, 0x00)                           // mov ax, 0x0003
        .Db(0xCD, 0x10)                                 // int 10h
        .Db(0xCB)                                       // retf
        .At(ServiceOffset).Label("handler")
        .Db(0x0F, 0x0B)                                 // ud2(サービス呼び出し)
        .Db(0xCF)                                       // iret
        .At(0x100).Ascii("Emu86 VGA BIOS stub")
        .Checksum();

    // ROM を 0xC0000 へ置き、IVT 10h をハンドラへ向ける(INT 19h のたびに呼んでも同じ結果)。
    public static void Install(EmuEnvironment env)
    {
        var ram = env.OneMegaMemory_;
        if (ram.Length < RomBase + RomSize) return;
        Rom.CopyTo(ram, (int)RomBase);
        ram[0x40] = ServiceOffset; ram[0x41] = 0x00; ram[0x42] = 0x00; ram[0x43] = 0xC0;
        if (ram[0x449] == 0) SetMode(env, 0x03);
    }

    public static bool IsInstalled(EmuEnvironment env) =>
        env.OneMegaMemory_.Length >= RomBase + RomSize &&
        BitConverter.ToUInt32(env.OneMegaMemory_, 0x40) == (0xC000u << 16 | ServiceOffset) &&
        env.OneMegaMemory_[RomBase + ServiceOffset] == 0x0F && env.OneMegaMemory_[RomBase + ServiceOffset + 1] == 0x0B;

    // C000:0060 の UD2 ならサービスを実行して EIP を進め true を返す。それ以外は false(通常の #UD 処理へ)。
    public static bool TryService(EmuEnvironment env, CPU cpu)
    {
        if (cpu.cs != 0xC000 || cpu.ip != ServiceOffset || !IsInstalled(env)) return false;
        Service(env, cpu);
        cpu.eip += 2;
        return true;
    }

    static void Service(EmuEnvironment env, CPU cpu)
    {
        var vga = env.Vga;
        var ram = env.OneMegaMemory_;
        switch (cpu.ah)
        {
            case 0x00: SetMode(env, cpu.al); break;
            case 0x01:
                vga.Crtc[0x0A] = (byte)(cpu.ch & 0x3F);
                vga.Crtc[0x0B] = (byte)(cpu.cl & 0x1F);
                vga.Dirty = true;
                break;
            case 0x02: SetCursor(env, cpu.dh, cpu.dl); break;
            case 0x03:
                _dl.setter(cpu)(ram[0x450]); _dh.setter(cpu)(ram[0x451]);
                _cx.setter(cpu)((ushort)((vga.Crtc[0x0A] << 8) | vga.Crtc[0x0B]));
                break;
            case 0x06 or 0x07: Scroll(env, cpu.ah == 0x06, cpu.al, cpu.bh, cpu.ch, cpu.cl, cpu.dh, cpu.dl); break;
            case 0x08:
                if (!vga.GraphicsMode)
                {
                    var unit = ram[0x451] * Columns(ram) + ram[0x450];
                    _al.setter(cpu)(vga.Vram[unit]); _ah.setter(cpu)(vga.Vram[0x10000 + unit]);
                }
                break;
            case 0x09 or 0x0A:
                if (!vga.GraphicsMode)
                {
                    var unit = ram[0x451] * Columns(ram) + ram[0x450];
                    for (var count = 0; count < cpu.cx && unit + count < 0x4000; count++)
                    {
                        vga.Vram[unit + count] = cpu.al;
                        if (cpu.ah == 0x09) vga.Vram[0x10000 + unit + count] = cpu.bl;
                    }
                    vga.Dirty = true;
                }
                break;
            case 0x0E: Teletype(env, cpu.al); break;
            case 0x0F:
                _al.setter(cpu)(ram[0x449]); _ah.setter(cpu)(ram[0x44A]); _bh.setter(cpu)(0);
                break;
            case 0x10:
                if (cpu.al == 0x00 && cpu.bl < vga.Attribute.Length) { vga.Attribute[cpu.bl] = cpu.bh; vga.Dirty = true; }
                else if (cpu.al == 0x10)
                {
                    var index = (cpu.bx & 0xFF) * 3;
                    vga.Dac[index] = (byte)(cpu.dh & 0x3F); vga.Dac[index + 1] = (byte)(cpu.ch & 0x3F); vga.Dac[index + 2] = (byte)(cpu.cl & 0x3F);
                    vga.Dirty = true;
                }
                break;
            case 0x12:
                if (cpu.bl == 0x10) { _bh.setter(cpu)(0); _bl.setter(cpu)(3); _ch.setter(cpu)(0); _cl.setter(cpu)(9); }
                else if (cpu.bl is 0x30 or 0x31 or 0x32 or 0x33 or 0x34 or 0x36) _al.setter(cpu)(0x12);
                break;
            case 0x1A:
                if (cpu.al == 0) { _al.setter(cpu)(0x1A); _bl.setter(cpu)(0x08); _bh.setter(cpu)(0x00); }
                break;
            case 0x4F: _ax.setter(cpu)(0x014F); break;
        }
    }

    static int Columns(byte[] ram) => ram[0x44A] == 0 ? 80 : ram[0x44A];
    static int Rows(byte[] ram) => ram[0x484] + 1;

    public static void SetMode(EmuEnvironment env, int requested)
    {
        var vga = env.Vga;
        var ram = env.OneMegaMemory_;
        var clear = (requested & 0x80) == 0;
        var mode = requested & 0x7F;
        var (misc, seq, crtc, gc, attr) = mode switch
        {
            0x13 => Mode13,
            0x0D or 0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x6A => Mode12,
            _ => Mode3,
        };
        vga.MiscOutput = misc;
        seq.CopyTo(vga.Sequencer, 0); crtc.CopyTo(vga.Crtc, 0); gc.CopyTo(vga.Graphics, 0); attr.CopyTo(vga.Attribute, 0);
        vga.DacMask = 0xFF;
        vga.AttributeData = false;
        LoadDefaultPalette(vga, mode == 0x13);
        if (clear)
        {
            if (vga.GraphicsMode) Array.Clear(vga.Vram);
            else
            {
                Array.Fill(vga.Vram, (byte)0x20, 0, 0x4000);
                Array.Fill(vga.Vram, (byte)0x07, 0x10000, 0x4000);
            }
        }
        var columns = mode == 0x13 ? 40 : 80;
        ram[0x449] = (byte)mode;
        ram[0x44A] = (byte)columns; ram[0x44B] = 0;
        ram[0x44C] = 0x00; ram[0x44D] = (byte)(vga.GraphicsMode ? 0 : 0x10);
        ram[0x44E] = 0; ram[0x44F] = 0;
        Array.Clear(ram, 0x450, 16);
        ram[0x460] = vga.Crtc[0x0B]; ram[0x461] = vga.Crtc[0x0A];
        ram[0x462] = 0;
        ram[0x463] = 0xD4; ram[0x464] = 0x03;
        ram[0x484] = (byte)(mode == 0x13 ? 24 : vga.GraphicsMode ? 29 : 24);
        ram[0x485] = (byte)(mode == 0x13 ? 8 : 16); ram[0x486] = 0;
        ram[0x487] = (byte)(0x60 | (clear ? 0 : 0x80));
        ram[0x488] = 0x09; ram[0x489] = 0x11; ram[0x48A] = 0x08;
        vga.Dirty = true;
    }

    static void SetCursor(EmuEnvironment env, int row, int column)
    {
        var ram = env.OneMegaMemory_;
        ram[0x450] = (byte)column; ram[0x451] = (byte)row;
        var position = row * Columns(ram) + column;
        env.Vga.Crtc[0x0E] = (byte)(position >> 8);
        env.Vga.Crtc[0x0F] = (byte)position;
        env.Vga.Dirty = true;
    }

    static void Teletype(EmuEnvironment env, byte character)
    {
        var vga = env.Vga;
        var ram = env.OneMegaMemory_;
        if (vga.GraphicsMode) return;
        var columns = Columns(ram);
        var rows = Rows(ram);
        int column = ram[0x450], row = ram[0x451];
        switch (character)
        {
            case 0x0D: column = 0; break;
            case 0x0A: row++; break;
            case 0x08: if (column > 0) column--; break;
            case 0x07: break;
            default:
                vga.Vram[row * columns + column] = character;
                if (++column >= columns) { column = 0; row++; }
                break;
        }
        if (row >= rows)
        {
            ScrollLines(vga, 1, up: true, 0x07, 0, 0, rows - 1, columns - 1, columns);
            row = rows - 1;
        }
        SetCursor(env, row, column);
    }

    static void Scroll(EmuEnvironment env, bool up, int lines, byte attribute, int top, int left, int bottom, int right)
    {
        var vga = env.Vga;
        var ram = env.OneMegaMemory_;
        if (vga.GraphicsMode) return;
        var columns = Columns(ram);
        bottom = Math.Min(bottom, Rows(ram) - 1); right = Math.Min(right, columns - 1);
        if (top > bottom || left > right) return;
        ScrollLines(vga, lines, up, attribute, top, left, bottom, right, columns);
    }

    static void ScrollLines(VgaDevice vga, int lines, bool up, byte attribute, int top, int left, int bottom, int right, int columns)
    {
        var height = bottom - top + 1;
        if (lines <= 0 || lines >= height) lines = height;
        for (var step = 0; step < height; step++)
        {
            var row = up ? top + step : bottom - step;
            var source = up ? row + lines : row - lines;
            for (var column = left; column <= right; column++)
            {
                var unit = row * columns + column;
                if (source >= top && source <= bottom)
                {
                    var from = source * columns + column;
                    vga.Vram[unit] = vga.Vram[from];
                    vga.Vram[0x10000 + unit] = vga.Vram[0x10000 + from];
                }
                else
                {
                    vga.Vram[unit] = 0x20;
                    vga.Vram[0x10000 + unit] = attribute;
                }
            }
        }
        vga.Dirty = true;
    }

    // 既定の DAC: 先頭 64 色は EGA 互換、256 色モードでは BIOS 標準の 256 色。
    static void LoadDefaultPalette(VgaDevice vga, bool colors256)
    {
        Array.Clear(vga.Dac);
        (colors256 ? Vga256Dac : VgaDevice.EgaDac).CopyTo(vga.Dac, 0);
    }

    // BIOS 標準の 256 色: EGA の既定パレット順の 16 色 + 16 階調グレー + 色相環 216 色
    // (3 段階の明度 × 3 段階の彩度 × 24 色相。青→赤紫→赤→黄→緑→シアン→青 を 4 分割で補間)。残り 8 色は黒。
    static readonly byte[] Vga256Dac = BuildVga256Dac();

    static byte[] BuildVga256Dac()
    {
        byte[] ega = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x14, 0x07, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F];
        byte[] grays = [0x00, 0x05, 0x08, 0x0B, 0x0E, 0x11, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x28, 0x2D, 0x32, 0x38, 0x3F];
        int[][] levels =
        [
            [0x3F, 0x10, 0x1F, 0x2F],
            [0x1C, 0x07, 0x0E, 0x15],
            [0x10, 0x04, 0x08, 0x0C],
        ];
        var sixteen =
            from index in ega
            select (r: VgaDevice.EgaDac[index * 3], g: VgaDevice.EgaDac[index * 3 + 1], b: VgaDevice.EgaDac[index * 3 + 2]);
        var grayscale =
            from gray in grays
            select (r: gray, g: gray, b: gray);
        var hues =
            from level in levels
            let high = level[0]
            from low in level.Skip(1)
            from hue in Enumerable.Range(0, 24)
            let segment = hue / 4
            let fraction = hue % 4
            select (
                r: Blend([low, high, high, high, low, low, low], segment, fraction),
                g: Blend([low, low, low, high, high, high, low], segment, fraction),
                b: Blend([high, high, low, low, low, high, high], segment, fraction));
        var dac = new byte[256 * 3];
        sixteen.Concat(grayscale).Concat(hues).SelectMany(color => new[] { color.r, color.g, color.b }).ToArray().CopyTo(dac, 0);
        return dac;
    }

    // 色相環の 1 区間(4 色相)を anchors[segment] から anchors[segment+1] へ線形補間する(整数演算、切り捨て)。
    static byte Blend(int[] anchors, int segment, int fraction) =>
        (byte)(anchors[segment] + (anchors[segment + 1] - anchors[segment]) * fraction / 4);

    static readonly (byte misc, byte[] seq, byte[] crtc, byte[] gc, byte[] attr) Mode3 = (
        0x67,
        [0x03, 0x00, 0x03, 0x00, 0x02],
        [0x5F, 0x4F, 0x50, 0x82, 0x55, 0x81, 0xBF, 0x1F, 0x00, 0x4F, 0x0D, 0x0E, 0x00, 0x00, 0x00, 0x00,
         0x9C, 0x8E, 0x8F, 0x28, 0x1F, 0x96, 0xB9, 0xA3, 0xFF],
        [0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x0E, 0x00, 0xFF],
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x14, 0x07, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x0C, 0x00, 0x0F, 0x08, 0x00]);

    static readonly (byte misc, byte[] seq, byte[] crtc, byte[] gc, byte[] attr) Mode12 = (
        0xE3,
        [0x03, 0x01, 0x0F, 0x00, 0x06],
        [0x5F, 0x4F, 0x50, 0x82, 0x54, 0x80, 0x0B, 0x3E, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
         0xEA, 0x8C, 0xDF, 0x28, 0x00, 0xE7, 0x04, 0xE3, 0xFF],
        [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x0F, 0xFF],
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x14, 0x07, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x01, 0x00, 0x0F, 0x00, 0x00]);

    static readonly (byte misc, byte[] seq, byte[] crtc, byte[] gc, byte[] attr) Mode13 = (
        0x63,
        [0x03, 0x01, 0x0F, 0x00, 0x0E],
        [0x5F, 0x4F, 0x50, 0x82, 0x54, 0x80, 0xBF, 0x1F, 0x00, 0x41, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
         0x9C, 0x8E, 0x8F, 0x28, 0x40, 0x96, 0xB9, 0xA3, 0xFF],
        [0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x05, 0x0F, 0xFF],
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x41, 0x00, 0x0F, 0x00, 0x00]);
}
