using static Emu86.CPU;
using static Emu86.States;
using static Emu86.CpuStates;
using static Emu86.Writers;

namespace Emu86;

// 最小限の VGA BIOS(INT 10h)。本物の VGA BIOS を同梱しない代わりに、2KB のオプション ROM 形式の
// スタブを 0xC0000 に置き、IVT のベクタ 10h をその中のハンドラへ向ける。ハンドラは実コードで、レジスタを
// サービスの呼び出し口(VgaBiosPort の I/O ポート)へ送り、C# 側で機能を処理した結果を読み戻して IRET する。
// ROM のコードを誰が実行しても同じ経路になる: CPU そのもの(リアルモードの SeaBIOS/NTLDR/bootmgr、Windows XP の
// vga.sys が V86 モードで行う BIOS 呼び出し)でも、OS が持つ x86 エミュレータ(Windows 7 の HAL は ROM のコードを
// 解釈して INT 10h を呼ぶ。エミュレータの外へ抜ける命令は通らない)でも、ポートの入出力はデバイスに届く。
// ES:DI へ返すデータ(VBE の情報)もハンドラがポートから読んで自分で書くので、呼び出し側のメモリの見え方に依らない。
//
// 同梱の SeaBIOS(QEMU 向けビルド)はレガシー ROM 領域を走査せず PCI/fw_cfg からしか VGA ROM を
// 取り込まないため、POST 完了後の INT 19h(ブート)時点で導入する。それ以降の INT 10h が対象になる。
//
// 対応機能: モード設定(00h: 3/12h/13h と近傍のモード)、カーソル(01h/02h/03h)、文字(08h/09h/0Ah/0Eh)、
// スクロール(06h/07h)、モード取得(0Fh)、EGA 情報(12h)、DCC(1Ah)、パレット(10h の一部)、
// VBE(4Fh の 00h-03h: コントローラ情報、モード情報、モード設定、現在のモード。Bochs VBE のレジスタと線形フレームバッファで実現する。
// Windows 7 の bootmgr は日本語の表示にグラフィックモードを使い、VBE が無いと何も表示しない)。
public static class VgaBiosStub
{
    public const uint RomBase = 0xC0000;
    public const int RomSize = 2048;
    const ushort RomSegment = (ushort)(RomBase >> 4);
    const int InitOffset = 0x20;
    const int PcirOffset = 0x40;
    public const int ServiceOffset = 0x60;      // INT 10h の入口(IVT が指す)
    const int HandlerOffset = 0x80;             // ハンドラの本体
    const int OemOffset = 0x100;                // 識別文字列(VBE の OEM 文字列も指す)
    const string OemText = "Emu86 VGA BIOS stub";
    const int VbeModeListOffset = 0x120;        // VBE のモード番号の一覧(0xFFFF で終わる)
    const int Int10Vector = 0x10 * 4;           // IVT のベクタ 10h(オフセット、セグメントの順)

    // VBE のモード。8 ビットはパレット(DAC)、16 ビットは 5:6:5、24/32 ビットは 8:8:8。
    // 番号は VESA の標準(0x101-0x118)と、Bochs VBE と同じ 32 ビットの番号(0x142-0x144)。Rom より先に初期化する(ROM に一覧を置く)。
    readonly record struct VbeMode(ushort Number, ushort Width, ushort Height, byte Bpp)
    {
        public int Pitch => Width * ((Bpp + 7) / 8);
    }

    static readonly VbeMode[] VbeModes =
    [
        new(0x101, 640, 480, 8), new(0x103, 800, 600, 8), new(0x105, 1024, 768, 8),
        new(0x111, 640, 480, 16), new(0x114, 800, 600, 16), new(0x117, 1024, 768, 16),
        new(0x112, 640, 480, 24), new(0x115, 800, 600, 24), new(0x118, 1024, 768, 24),
        new(0x142, 640, 480, 32), new(0x143, 800, 600, 32), new(0x144, 1024, 768, 32),
    ];

    public static readonly byte[] Rom = BuildRom();

    // ROM の構造(Writer で書く。At は ROM の先頭からの位置): 署名と jmp short init、PCI データ構造(SeaBIOS は ROM のベンダ/デバイス ID が
    // 取り込み元の PCI デバイスと一致することを確認してから init を呼ぶ)、init(IVT 10h をハンドラへ向けて mode 3 を設定)、
    // INT 10h の入口(jmp short でハンドラへ)、ハンドラ、識別文字列。最後の 1 バイトは ROM 全体の和を 0 にするチェックサム。
    // 入口の次の iret は、UD2 でサービスを呼んでいた以前の ROM と同じ位置に残してある(その位置で保存したスナップショットが戻れるように)。
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
            Bytes(0xC7, 0x06), U16(Int10Vector), U16(ServiceOffset),    // mov word [0x40], handler
            Bytes(0xC7, 0x06), U16(Int10Vector + 2), U16(RomSegment),   // mov word [0x42], 0xC000
            Bytes(0x1F),                                        // pop ds
            Bytes(0xB8, 0x03, 0x00),                            // mov ax, 0x0003
            Bytes(0xCD, 0x10),                                  // int 10h
            Bytes(0xCB))),                                      // retf
        At(ServiceOffset, Sequence(JmpShort(HandlerOffset), Bytes(0xCF))),  // 入口: jmp short handler、(以前の ROM の)iret
        At(HandlerOffset, Handler),
        At(OemOffset, Ascii(OemText)),
        At(VbeModeListOffset, Sequence([.. VbeModes.Select(mode => U16(mode.Number)), U16(0xFFFF)])),
        ZeroSum(RomSize))
        .Build(RomSize);

    // ハンドラ(16 ビットの実コード): AX、BX、CX、DX と ES:DI の先頭 4 バイト(VBE の呼び出し側が置く "VBE2")をサービスの呼び出し口へ送り、
    // 結果の AX、BX、CX、DX を読み戻す。ES:DI へ返すバイト列があれば、1 バイトずつ読んで書く。他のレジスタは変えない。
    // ES:DI の読み出しはバイト単位(16 ビットのアドレス計算は 64KB で折り返すので、DI が何であってもセグメントの外へ出ない)。
    static Writer Handler => Sequence(
        Bytes(0x52),                                    // push dx
        Bytes(0xBA), U16(VgaBiosPort.CallPort),         // mov dx, 0x1D0
        Bytes(0xEF),                                    // out dx, ax           呼び出しの開始: AX
        Bytes(0x42, 0x42),                              // inc dx; inc dx       dx = 0x1D2
        Bytes(0x89, 0xD8, 0xEF),                        // mov ax, bx; out dx, ax
        Bytes(0x89, 0xC8, 0xEF),                        // mov ax, cx; out dx, ax
        Bytes(0x58, 0xEF),                              // pop ax; out dx, ax   元の DX
        Bytes(0x26, 0x8A, 0x05),                        // mov al, es:[di]
        Bytes(0x26, 0x8A, 0x65, 0x01),                  // mov ah, es:[di+1]
        Bytes(0xEF),                                    // out dx, ax           ES:DI の先頭(下位の語)
        Bytes(0x26, 0x8A, 0x45, 0x02),                  // mov al, es:[di+2]
        Bytes(0x26, 0x8A, 0x65, 0x03),                  // mov ah, es:[di+3]
        Bytes(0xEF),                                    // out dx, ax           ES:DI の先頭(上位の語)
        Bytes(0xED, 0x50),                              // in ax, dx; push ax   結果の AX(ここでサービスが実行される)
        Bytes(0xED, 0x89, 0xC3),                        // in ax, dx; mov bx, ax
        Bytes(0xED, 0x50),                              // in ax, dx; push ax   結果の CX
        Bytes(0xED, 0x50),                              // in ax, dx; push ax   結果の DX
        Bytes(0xED, 0x89, 0xC1),                        // in ax, dx; mov cx, ax    ES:DI へ書くバイト数
        Bytes(0xE3, 0x09),                              // jcxz done
        Bytes(0x57),                                    // push di
        Bytes(0x42, 0x42),                              // inc dx; inc dx       dx = 0x1D4
        Bytes(0xFC),                                    // cld
        Bytes(0xEC, 0xAA),                              // next: in al, dx; stosb
        Bytes(0xE2, 0xFC),                              // loop next
        Bytes(0x5F),                                    // pop di
        Bytes(0x5A, 0x59, 0x58),                        // done: pop dx; pop cx; pop ax
        Bytes(0xCF));                                   // iret

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
        Sequence(U16(ServiceOffset), U16(RomSegment))(ram, Int10Vector);
        if (ram[Bda.VideoMode] == 0) SetMode(env, 0x03);
    }

    public static bool IsInstalled(EmuEnvironment env) =>
        env.Ram.Length >= RomBase + RomSize &&
        BitConverter.ToUInt32(env.Ram, Int10Vector) == ((uint)RomSegment << 16 | ServiceOffset) &&
        env.Ram.AsSpan((int)RomBase + ServiceOffset, HandlerOffset - ServiceOffset).SequenceEqual(Rom.AsSpan(ServiceOffset, HandlerOffset - ServiceOffset));

    // 以前のスタブ(入口が UD2 で、ランナーがそれを横取りしてサービスを実行していた)が RAM にあれば、今の ROM に置き換える。
    // v13 以前のスナップショットを読み込んだときに呼ぶ。入口の番地と、その次の iret の位置は同じなので、IVT はそのまま使える。
    public static void Upgrade(EmuEnvironment env)
    {
        var ram = env.Ram;
        if (ram.Length < RomBase + RomSize) return;
        var legacy = ram[RomBase] == 0x55 && ram[RomBase + 1] == 0xAA
            && ram[RomBase + ServiceOffset] == 0x0F && ram[RomBase + ServiceOffset + 1] == 0x0B
            && ram.AsSpan((int)RomBase + OemOffset, OemText.Length).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(OemText));
        if (!legacy) return;
        Rom.CopyTo(ram, (int)RomBase);
        env.Translations.InvalidateAll(); // RAM への直接書き込み
    }

    // INT 10h のサービス: AH → State(レジスタは Accessor、画面は環境経由)。AL/BL で分かれる機能は入れ子の表。
    // 表にない機能は何もしない。レジスタは ROM のハンドラが送ってきた AX、BX、CX、DX だけで、ES:DI のメモリは
    // 呼び出し口(env.VgaBios)を通す: 呼び出し側が置いていた先頭 4 バイトが Head、返すバイト列は Reply。
    static readonly State<byte[]> Ram = GetDataFromEnvCpu((env, _) => env.Ram);
    static readonly State<VgaDevice> Vga = GetDataFromEnvCpu((env, _) => env.Vga);
    static State<Unit> Dispatch(Accessor<CPU, byte> key, Dictionary<byte, State<Unit>> table) => (env, cpu, ope) =>
        table.TryGetValue(key.getter(cpu), out var service) ? service(env, cpu, ope) : (true, Unit.unit, cpu, string.Empty);

    // テキストモードのカーソル位置のセル番号(BDA の列・行から)。
    static readonly State<(VgaDevice vga, int unit)> CursorCell =
        from vga in Vga
        from ram in Ram
        select (vga, ram[Bda.CursorRow] * Columns(ram) + ram[Bda.CursorColumn]);

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
                 from _1 in SetCpu((_dl, ram[Bda.CursorColumn]), (_dh, ram[Bda.CursorRow]))
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
        [0x0F] = from ram in Ram from _ in SetCpu((_al, ram[Bda.VideoMode]), (_ah, ram[Bda.Columns]), (_bh, (byte)0)) select Unit.unit,
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
        // VBE: 成功は AX=004Fh、失敗は 014Fh。表にない機能は AL を変えずに返す(未対応)。
        [0x4F] = Dispatch(_al, new()
        {
            [0x00] = from vbe2 in GetDataFromEnvCpu((env, _) => env.VgaBios.Head == 0x32454256)     // 呼び出し側が ES:DI に置く "VBE2"
                     from _1 in Effect((env, _) => env.VgaBios.Reply(VbeControllerInfo(vbe2)))
                     from _2 in _ax.Set((ushort)0x004F)
                     select Unit.unit,
            [0x01] = from number in _cx.Get()
                     let mode = FindVbeMode(number & 0x1FF)
                     from _1 in When(mode != null, Effect((env, _) => env.VgaBios.Reply(VbeModeInfo(mode.Value, env.Pci.LfbBase))))
                     from _2 in _ax.Set((ushort)(mode != null ? 0x004F : 0x014F))
                     select Unit.unit,
            [0x02] = from request in _bx.Get()
                     from done in GetDataFromEnvCpu((env, _) => VbeSetMode(env, request))
                     from _ in _ax.Set((ushort)(done ? 0x004F : 0x014F))
                     select Unit.unit,
            [0x03] = from ram in Ram
                     from vga in Vga
                     from _ in SetCpu((_bx, vga.VbeEnabled ? BitConverter.ToUInt16(ram, Bda.VbeMode) : (ushort)ram[Bda.VideoMode]), (_ax, (ushort)0x004F))
                     select Unit.unit,
        }),
    };

    static VbeMode? FindVbeMode(int number) => VbeModes.Where(mode => mode.Number == number).Select(mode => (VbeMode?)mode).FirstOrDefault();

    // ROM の中を指す far ポインタ(オフセット、セグメントの順)。
    static Writer RomPointer(int offset) => Sequence(U16((ushort)offset), U16(RomSegment));

    // VBE 4F00h: コントローラ情報。呼び出し側が "VBE2" を置いていれば 512 バイト、そうでなければ 256 バイト。
    static byte[] VbeControllerInfo(bool vbe2) => Sequence(
        At(0x00, Ascii("VESA")),
        At(0x04, U16(0x0200)),                              // VBE 2.0
        At(0x06, RomPointer(OemOffset)),                    // OEM の文字列
        At(0x0A, U32(0)),                                   // 能力(DAC は 6 ビット固定)
        At(0x0E, RomPointer(VbeModeListOffset)),            // モード番号の一覧
        At(0x12, U16(VgaDevice.LfbSize >> 16)),             // ビデオメモリ(64KB 単位)
        At(0x14, U16(0x0100)),                              // OEM ソフトウェアの版
        At(0x16, RomPointer(OemOffset)),                    // ベンダ名
        At(0x1A, RomPointer(OemOffset)),                    // 製品名
        At(0x1E, RomPointer(OemOffset)))                    // 製品の版
        .Build(vbe2 ? 512 : 256);

    // VBE 4F01h: モード情報(256 バイト)。線形フレームバッファの番地(lfb)は PCI の BAR0。
    static byte[] VbeModeInfo(VbeMode mode, uint lfb)
    {
        var frame = mode.Pitch * mode.Height;
        // 色の並び: 赤・緑・青・予備の(ビット数、位置)。8 ビットはパレットなので 0。
        byte[] masks = mode.Bpp switch
        {
            16 => [5, 11, 6, 5, 5, 0, 0, 0],
            24 => [8, 16, 8, 8, 8, 0, 0, 0],
            32 => [8, 16, 8, 8, 8, 0, 8, 24],
            _ => [0, 0, 0, 0, 0, 0, 0, 0],
        };
        return Sequence(
            At(0x00, U16(0x009B)),                          // 使用可、追加情報あり、カラー、グラフィック、線形フレームバッファあり
            At(0x02, Bytes(0x07, 0x00)),                    // ウィンドウ A(再配置可・読み書き可)、B は無し
            At(0x04, U16(64)), At(0x06, U16(64)),           // ウィンドウの粒度と大きさ(KB)
            At(0x08, U16(0xA000)),                          // ウィンドウ A のセグメント
            At(0x10, U16((ushort)mode.Pitch)),              // 1 ラインのバイト数
            At(0x12, U16(mode.Width)), At(0x14, U16(mode.Height)),
            At(0x16, Bytes(8, 16, 1, mode.Bpp)),            // 文字の幅と高さ、プレーン数、色深度
            At(0x1A, U8((byte)((frame + 0xFFFF) >> 16))),   // バンク数
            At(0x1B, U8((byte)(mode.Bpp == 8 ? 4 : 6))),    // メモリモデル: パックドピクセル / ダイレクトカラー
            At(0x1D, U8((byte)Math.Min(255, VgaDevice.LfbSize / frame - 1))),   // 画像ページ数 - 1
            At(0x1E, U8(1)),
            At(0x1F, Bytes(masks)),
            At(0x27, U8((byte)(mode.Bpp == 32 ? 0x02 : 0x00))), // 予備のフィールドは使ってよい
            At(0x28, U32(lfb)),
            At(0x32, U16((ushort)mode.Pitch)),              // (VBE 3.0)線形モードの 1 ラインのバイト数
            At(0x36, Bytes(masks)))                         // (VBE 3.0)線形モードの色の並び
            .Build(256);
    }

    // Bochs VBE(DISPI)のレジスタへ書く(インデックス、値)。
    static void Dispi(VgaDevice vga, int index, int value)
    {
        vga.VbeWrite(0x1CE, (ushort)index);
        vga.VbeWrite(0x1CF, (ushort)value);
    }

    // VBE 4F02h: モード設定。要求の bit0-8 がモード番号、bit14 が線形フレームバッファ、bit15 が「画面を消さない」。
    // 0x100 未満は従来のモード(AH=00h と同じ)。知らない番号なら false。
    static bool VbeSetMode(EmuEnvironment env, int request)
    {
        var number = request & 0x1FF;
        var keep = (request & 0x8000) != 0;
        if (number < 0x100)
        {
            SetMode(env, number | (keep ? 0x80 : 0));
            return true;
        }
        if (FindVbeMode(number) is not { } mode) return false;
        var vga = env.Vga;
        Dispi(vga, 4, 0);                                   // いったん無効にしてから解像度を変える
        Dispi(vga, 1, mode.Width); Dispi(vga, 2, mode.Height); Dispi(vga, 3, mode.Bpp);
        Dispi(vga, 6, mode.Width);                          // 仮想の幅 = 表示の幅
        Dispi(vga, 5, 0); Dispi(vga, 8, 0); Dispi(vga, 9, 0);   // バンク、表示開始位置
        Dispi(vga, 4, 0x01 | 0x40 | (keep ? 0x80 : 0));     // 有効、線形フレームバッファ、(消さない)
        if (mode.Bpp == 8) LoadDefaultPalette(vga, colors256: true);
        At(Bda.VbeMode, U16((ushort)(request & 0x41FF)))(env.Ram, 0);
        vga.Dirty = true;
        return true;
    }

    // サービスを 1 回実行する。レジスタ(AX、BX、CX、DX)は cpu のものを読んで書き換える。ES:DI へ返すバイト列は呼び出し口に置く。
    internal static void Service(EmuEnvironment env, CPU cpu)
    {
        // --vgalog: INT 10h の呼び出しを記録する(ブートローダや OS がどの機能を使うかの調査用)。
        if (env.Vga.Log)
            Console.Error.WriteLine($"[int10] ax={cpu.ax:x4} bx={cpu.bx:x4} cx={cpu.cx:x4} dx={cpu.dx:x4}{(Services.ContainsKey(cpu.ah) ? "" : " (unsupported)")}");
        env.VgaBios.Reply([]);
        Dispatch(_ah, Services)(env, cpu, null);
    }

    static int Columns(byte[] ram) => ram[Bda.Columns] == 0 ? 80 : ram[Bda.Columns];
    static int Rows(byte[] ram) => ram[Bda.LastRow] + 1;

    public static void SetMode(EmuEnvironment env, int requested)
    {
        var vga = env.Vga;
        var ram = env.Ram;
        var clear = (requested & 0x80) == 0;
        var mode = requested & 0x7F;
        if (vga.VbeEnabled) Dispi(vga, 4, 0);   // VBE のモードから戻るときは、線形フレームバッファの表示を止める
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
        // BIOS データ領域のビデオ項目に、設定したモードの状態を置く(At は Bda の番地)。
        Sequence(
            At(Bda.VideoMode, U8((byte)mode)),
            At(Bda.Columns, U16((ushort)(mode == 0x13 ? 40 : 80))),
            At(Bda.PageSize, U16((ushort)(vga.GraphicsMode ? 0 : 0x1000))),                         // テキストは 4KB
            At(Bda.PageStart, U16(0)),
            At(Bda.CursorColumn, Bytes(new byte[16])),                                              // 8 ページ分の列・行
            At(Bda.CursorShape, Bytes(vga.Crtc[0x0B], vga.Crtc[0x0A])),
            At(Bda.ActivePage, U8(0)),
            At(Bda.CrtcPort, U16(0x3D4)),
            At(Bda.LastRow, U8((byte)(mode == 0x13 ? 24 : vga.GraphicsMode ? 29 : 24))),
            At(Bda.CharacterHeight, U16((ushort)(mode == 0x13 ? 8 : 16))),
            At(Bda.VideoControl, U8((byte)(0x60 | (clear ? 0 : 0x80)))),
            At(Bda.VideoSwitches, Bytes(0x09, 0x11, 0x08)),
            At(Bda.VbeMode, U16(0)))
            (ram, 0);
        vga.Dirty = true;
    }

    static void SetCursor(EmuEnvironment env, int row, int column)
    {
        var ram = env.Ram;
        ram[Bda.CursorColumn] = (byte)column; ram[Bda.CursorRow] = (byte)row;
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
        int column = ram[Bda.CursorColumn], row = ram[Bda.CursorRow];
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
