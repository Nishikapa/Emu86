using static Emu86.CPU;
using static Emu86.States;
using static Emu86.CpuStates;
using static Emu86.Writers;

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

    // ROM の構造(Writer で書く。At は ROM の先頭からの位置): 署名と jmp short init、PCI データ構造(SeaBIOS は ROM のベンダ/デバイス ID が
    // 取り込み元の PCI デバイスと一致することを確認してから init を呼ぶ)、init(IVT 10h をハンドラへ向けて mode 3 を設定)、
    // ハンドラ(ud2 でサービス呼び出し、iret)、識別文字列。最後の 1 バイトは ROM 全体の和を 0 にするチェックサム。
    static byte[] BuildRom() => Sequence(
        At(0x00, Sequence(
            Bytes(0x55, 0xAA, RomSize / 512),                   // 署名とサイズ(512 バイト単位)
            JmpShort(InitOffset))),
        At(0x18, U16(PcirOffset)),                              // PCI データ構造へのポインタ
        At(PcirOffset, Sequence(
            Ascii("PCIR"),
            U16(0x1234), U16(0x1111),                           // ベンダ、デバイス
            U16(0x0000), U16(0x0018), U8(0x00),                 // VPD ポインタ、構造体長、リビジョン
            Bytes(0x00, 0x00, 0x03),                            // クラス: VGA 互換(prog-if, sub, base)
            U16(RomSize / 512), U16(0x0000),                    // イメージ長(512 バイト単位)、コードリビジョン
            Bytes(0x00, 0x80), U16(0x0000))),                   // コード種別 x86、最後のイメージ
        At(InitOffset, Sequence(
            Bytes(0x1E),                                        // push ds
            Bytes(0x31, 0xC0),                                  // xor ax, ax
            Bytes(0x8E, 0xD8),                                  // mov ds, ax
            Bytes(0xC7, 0x06, 0x40, 0x00), U16(ServiceOffset),  // mov word [0x40], handler
            Bytes(0xC7, 0x06, 0x42, 0x00), U16(0xC000),         // mov word [0x42], 0xC000
            Bytes(0x1F),                                        // pop ds
            Bytes(0xB8, 0x03, 0x00),                            // mov ax, 0x0003
            Bytes(0xCD, 0x10),                                  // int 10h
            Bytes(0xCB))),                                      // retf
        At(ServiceOffset, Bytes(0x0F, 0x0B, 0xCF)),             // ハンドラ: ud2(サービス呼び出し)、iret
        At(0x100, Ascii("Emu86 VGA BIOS stub")),
        ZeroSum(RomSize))
        .Build(RomSize);

    // jmp short target(EB rel8)。変位は次の命令からの相対。
    static Writer JmpShort(int target) => (buffer, pos) =>
    {
        buffer[pos] = 0xEB;
        buffer[pos + 1] = (byte)(target - (pos + 2));
        return pos + 2;
    };

    // 最後の 1 バイトに、size バイト全体の和が 0 になる値を置く(他の項目をすべて書いた後に計算する)。
    static Writer ZeroSum(int size) => (buffer, pos) =>
    {
        var sum = 0;
        for (var index = 0; index < size - 1; index++) sum += buffer[pos + index];
        buffer[pos + size - 1] = (byte)-sum;
        return pos;
    };

    // ROM を 0xC0000 へ置き、IVT 10h をハンドラへ向ける(INT 19h のたびに呼んでも同じ結果)。
    public static void Install(EmuEnvironment env)
    {
        var ram = env.Ram;
        if (ram.Length < RomBase + RomSize) return;
        Rom.CopyTo(ram, (int)RomBase);
        env.Translations.InvalidateAll(); // RAM への直接書き込み
        ram[0x40] = ServiceOffset; ram[0x41] = 0x00; ram[0x42] = 0x00; ram[0x43] = 0xC0;
        if (ram[0x449] == 0) SetMode(env, 0x03);
    }

    public static bool IsInstalled(EmuEnvironment env) =>
        env.Ram.Length >= RomBase + RomSize &&
        BitConverter.ToUInt32(env.Ram, 0x40) == (0xC000u << 16 | ServiceOffset) &&
        env.Ram[RomBase + ServiceOffset] == 0x0F && env.Ram[RomBase + ServiceOffset + 1] == 0x0B;

    // C000:0060 の UD2 ならサービスを実行して EIP を進め true を返す。それ以外は false(通常の #UD 処理へ)。
    public static bool TryService(EmuEnvironment env, CPU cpu)
    {
        if (cpu.cs != 0xC000 || cpu.ip != ServiceOffset || !IsInstalled(env)) return false;
        Service(env, cpu);
        cpu.eip += 2;
        return true;
    }

    // INT 10h のサービス: AH → State(CPU のレジスタは Accessor、画面は環境経由)。AL/BL で分かれる機能は入れ子の表。
    // 表にない機能は何もしない。
    static readonly State<byte[]> Ram = GetDataFromEnvCpu((env, _) => env.Ram);
    static readonly State<VgaDevice> Vga = GetDataFromEnvCpu((env, _) => env.Vga);
    static State<Unit> Dispatch(Accessor<CPU, byte> key, Dictionary<byte, State<Unit>> table) => (env, cpu, ope) =>
        table.TryGetValue(key.getter(cpu), out var service) ? service(env, cpu, ope) : (true, Unit.unit, cpu, string.Empty);

    // テキストモードのカーソル位置のセル番号(BDA の列・行から)。
    static readonly State<(VgaDevice vga, int unit)> CursorCell =
        from vga in Vga
        from ram in Ram
        select (vga, ram[0x451] * Columns(ram) + ram[0x450]);

    // AH=09/0A: カーソル位置から CX 個の文字(09 は属性も)を書く。
    static State<Unit> WriteCells(bool withAttribute) =>
        from cell in CursorCell
        from character in _al.Get()
        from attribute in _bl.Get()
        from count in _cx.Get()
        from _ in When(!cell.vga.GraphicsMode, Effect((_, _) =>
        {
            for (var offset = 0; offset < count && cell.unit + offset < 0x4000; offset++)
            {
                cell.vga.Vram[cell.unit + offset] = character;
                if (withAttribute) cell.vga.Vram[0x10000 + cell.unit + offset] = attribute;
            }
            cell.vga.Dirty = true;
        }))
        select Unit.unit;

    // AH=06/07: 窓(CH,CL)-(DH,DL)を AL 行スクロール(0 は消去)、空いた行の属性は BH。
    static State<Unit> ScrollWindow(bool up) =>
        from lines in _al.Get()
        from attribute in _bh.Get()
        from top in _ch.Get()
        from left in _cl.Get()
        from bottom in _dh.Get()
        from right in _dl.Get()
        from _ in Effect((env, _) => Scroll(env, up, lines, attribute, top, left, bottom, right))
        select Unit.unit;

    static readonly Dictionary<byte, State<Unit>> Services = new()
    {
        [0x00] = from mode in _al.Get() from _ in Effect((env, _) => SetMode(env, mode)) select Unit.unit,
        [0x01] = from start in _ch.Get() from end in _cl.Get()
                 from _ in Effect((env, _) => { env.Vga.Crtc[0x0A] = (byte)(start & 0x3F); env.Vga.Crtc[0x0B] = (byte)(end & 0x1F); env.Vga.Dirty = true; })
                 select Unit.unit,
        [0x02] = from row in _dh.Get() from column in _dl.Get() from _ in Effect((env, _) => SetCursor(env, row, column)) select Unit.unit,
        [0x03] = from ram in Ram from vga in Vga
                 from _1 in SetCpu((_dl, ram[0x450]), (_dh, ram[0x451]))
                 from _2 in _cx.Set((ushort)((vga.Crtc[0x0A] << 8) | vga.Crtc[0x0B]))
                 select Unit.unit,
        [0x06] = ScrollWindow(up: true),
        [0x07] = ScrollWindow(up: false),
        [0x08] = from cell in CursorCell
                 from _ in When(!cell.vga.GraphicsMode, SetCpu((_al, cell.vga.Vram[cell.unit]), (_ah, cell.vga.Vram[0x10000 + cell.unit])))
                 select Unit.unit,
        [0x09] = WriteCells(withAttribute: true),
        [0x0A] = WriteCells(withAttribute: false),
        [0x0E] = from character in _al.Get() from _ in Effect((env, _) => Teletype(env, character)) select Unit.unit,
        [0x0F] = from ram in Ram from _ in SetCpu((_al, ram[0x449]), (_ah, ram[0x44A]), (_bh, (byte)0)) select Unit.unit,
        [0x10] = Dispatch(_al, new()
        {
            [0x00] = from index in _bl.Get() from value in _bh.Get()
                     from _ in Effect((env, _) => { if (index < env.Vga.Attribute.Length) { env.Vga.Attribute[index] = value; env.Vga.Dirty = true; } })
                     select Unit.unit,
            [0x10] = from index in _bx.Get() from red in _dh.Get() from green in _ch.Get() from blue in _cl.Get()
                     from _ in Effect((env, _) =>
                     {
                         var dac = (index & 0xFF) * 3;
                         env.Vga.Dac[dac] = (byte)(red & 0x3F); env.Vga.Dac[dac + 1] = (byte)(green & 0x3F); env.Vga.Dac[dac + 2] = (byte)(blue & 0x3F);
                         env.Vga.Dirty = true;
                     })
                     select Unit.unit,
        }),
        [0x12] = Dispatch(_bl, new()
        {
            [0x10] = SetCpu((_bh, (byte)0), (_bl, (byte)3), (_ch, (byte)0), (_cl, (byte)9)), // EGA 情報: カラー、256KB、スイッチ
            [0x30] = _al.Set((byte)0x12), [0x31] = _al.Set((byte)0x12), [0x32] = _al.Set((byte)0x12), [0x33] = _al.Set((byte)0x12), [0x34] = _al.Set((byte)0x12), [0x36] = _al.Set((byte)0x12),
        }),
        [0x1A] = Dispatch(_al, new() { [0x00] = SetCpu((_al, (byte)0x1A), (_bl, (byte)0x08), (_bh, (byte)0x00)) }), // 表示結合コード: VGA カラー
        [0x4F] = _ax.Set((ushort)0x014F), // VESA: 未対応
    };

    internal static void Service(EmuEnvironment env, CPU cpu) => Dispatch(_ah, Services)(env, cpu, null);

    static int Columns(byte[] ram) => ram[0x44A] == 0 ? 80 : ram[0x44A];
    static int Rows(byte[] ram) => ram[0x484] + 1;

    public static void SetMode(EmuEnvironment env, int requested)
    {
        var vga = env.Vga;
        var ram = env.Ram;
        var clear = (requested & 0x80) == 0;
        var mode = requested & 0x7F;
        vga.Load(mode switch
        {
            0x13 => VgaDevice.Mode13,
            0x0D or 0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x6A => VgaDevice.Mode12,
            _ => VgaDevice.Mode3,
        });
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
        // BIOS データ領域(At は 0x400 からの位置)に、設定したモードの状態を置く。
        Sequence(
            At(0x49, U8((byte)mode)),                                               // 現在のビデオモード
            At(0x4A, U16((ushort)(mode == 0x13 ? 40 : 80))),                        // 桁数
            At(0x4C, U16((ushort)(vga.GraphicsMode ? 0 : 0x1000))),                 // 1 ページの大きさ(テキストは 4KB)
            At(0x4E, U16(0)),                                                       // 表示ページの先頭
            At(0x50, Bytes(new byte[16])),                                          // カーソル位置(8 ページ分)
            At(0x60, Bytes(vga.Crtc[0x0B], vga.Crtc[0x0A])),                        // カーソルの形(終了行、開始行)
            At(0x62, U8(0)),                                                        // 表示ページ
            At(0x63, U16(0x3D4)),                                                   // CRTC のポート
            At(0x84, U8((byte)(mode == 0x13 ? 24 : vga.GraphicsMode ? 29 : 24))),   // 行数 - 1
            At(0x85, U16((ushort)(mode == 0x13 ? 8 : 16))),                         // 文字の高さ
            At(0x87, U8((byte)(0x60 | (clear ? 0 : 0x80)))),                        // ビデオ制御(bit7 = 画面を消さなかった)
            At(0x88, Bytes(0x09, 0x11, 0x08)))                                      // スイッチ、モード設定の制御、表示結合コードの表の番号
            (ram, 0x400);
        vga.Dirty = true;
    }

    static void SetCursor(EmuEnvironment env, int row, int column)
    {
        var ram = env.Ram;
        ram[0x450] = (byte)column; ram[0x451] = (byte)row;
        var position = row * Columns(ram) + column;
        env.Vga.Crtc[0x0E] = (byte)(position >> 8);
        env.Vga.Crtc[0x0F] = (byte)position;
        env.Vga.Dirty = true;
    }

    static void Teletype(EmuEnvironment env, byte character)
    {
        var vga = env.Vga;
        var ram = env.Ram;
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
        var ram = env.Ram;
        if (vga.GraphicsMode) return;
        var columns = Columns(ram);
        bottom = Math.Min(bottom, Rows(ram) - 1); right = Math.Min(right, columns - 1);
        if (top > bottom || left > right) return;
        ScrollLines(vga, lines, up, attribute, top, left, bottom, right, columns);
    }

    // 窓の各行に、lines 行離れた行(窓の中なら)を写すか、空白と属性で埋める。上スクロールは上の行から、下スクロールは下の行から処理する
    // (写す元がまだ書き換わっていない順)。文字はプレーン 0、属性はプレーン 1(0x10000 から)。
    static void ScrollLines(VgaDevice vga, int lines, bool up, byte attribute, int top, int left, int bottom, int right, int columns)
    {
        var height = bottom - top + 1;
        var width = right - left + 1;
        if (lines <= 0 || lines >= height) lines = height;
        for (var step = 0; step < height; step++)
        {
            var row = up ? top + step : bottom - step;
            var source = up ? row + lines : row - lines;
            var unit = row * columns + left;
            if (source >= top && source <= bottom)
            {
                var from = source * columns + left;
                Array.Copy(vga.Vram, from, vga.Vram, unit, width);
                Array.Copy(vga.Vram, 0x10000 + from, vga.Vram, 0x10000 + unit, width);
            }
            else
            {
                Array.Fill(vga.Vram, (byte)0x20, unit, width);
                Array.Fill(vga.Vram, attribute, 0x10000 + unit, width);
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
}
