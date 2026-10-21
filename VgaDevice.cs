namespace Emu86;

// VGA(IBM VGA 互換)のメモリウィンドウ(0xA0000-0xBFFFF)とレジスタ群。
// VRAM は 4 プレーン × 64KB。CPU からのアクセスは Sequencer/Graphics Controller の
// 設定に従って「アドレス単位(全プレーンにまたがる 1 バイト幅の位置)」とプレーンへ振り分ける。
//   チェイン 4(mode 13h): プレーン = offset&3、単位 = offset>>2
//   奇偶(テキスト):      プレーン = offset&1、単位 = offset>>1
//   プレーナ(mode 12h):  単位 = offset、書き込みモード 0〜3 と ALU・ビットマスクを適用
// 初期状態は BIOS が mode 3(80×25 テキスト)を設定した直後に相当するので、VGA BIOS が無くても
// 0xB8000 への直接書き込みが文字として見える。描画(Render)は CRTC/Attribute/DAC から
// 解像度と色を決め、32bit ARGB のピクセル列を生成する。
public sealed class VgaDevice
{
    public const uint WindowBase = 0xA0000;
    public const uint WindowSize = 0x20000;
    const int PlaneSize = 0x10000;

    public readonly byte[] Vram = new byte[4 * PlaneSize];
    public readonly byte[] Sequencer = new byte[8];
    public readonly byte[] Graphics = new byte[9];
    public readonly byte[] Crtc = new byte[25];
    public readonly byte[] Attribute = new byte[21];
    public readonly byte[] Dac = new byte[256 * 3]; // 6 ビット成分
    public byte SequencerIndex, GraphicsIndex, CrtcIndex, AttributeIndex;
    public byte DacWriteIndex, DacReadIndex, DacWritePhase, DacReadPhase, DacMask = 0xFF;
    public byte MiscOutput = 0x67, FeatureControl;
    public bool AttributeData; // 3C0 フリップフロップ: false=次はインデックス、true=次はデータ
    readonly byte[] latch = new byte[4];

    // --vgalog: レジスタ書き込みを標準エラー出力へ記録する(ドライバのモード設定の追跡用)。
    public bool Log;

    // 表示側が参照する更新フラグ(VRAM・レジスタ・パレットの変更で立つ)。
    public volatile bool Dirty = true;

    // プレーン 2 にフォントが無いとき(VGA BIOS 無しの直接書き込み)に使う 8×16 フォント(256 文字 × 16 行)。
    public byte[] FallbackFont;

    // 既定 DAC の先頭 64 色(EGA 互換): 色 index の bit0-2 が B/G/R の高輝度(0x2A)、bit3-5 が低輝度(0x15)の加算。
    public static readonly byte[] EgaDac =
        [.. from index in Enumerable.Range(0, 64)
            from component in new[] { 2, 1, 0 } // R, G, B の順
            select (byte)(((index >> component) & 1) * 0x2A + ((index >> (component + 3)) & 1) * 0x15)];

    public VgaDevice() => Reset();

    public void Reset()
    {
        Array.Clear(Vram);
        new byte[] { 0x03, 0x00, 0x03, 0x00, 0x02 }.CopyTo(Sequencer, 0);
        new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x0E, 0x00, 0xFF }.CopyTo(Graphics, 0);
        new byte[] { 0x5F, 0x4F, 0x50, 0x82, 0x55, 0x81, 0xBF, 0x1F, 0x00, 0x4F, 0x0D, 0x0E, 0x00, 0x00, 0x00, 0x00,
            0x9C, 0x8E, 0x8F, 0x28, 0x1F, 0x96, 0xB9, 0xA3, 0xFF }.CopyTo(Crtc, 0);
        new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x14, 0x07, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
            0x0C, 0x00, 0x0F, 0x08, 0x00 }.CopyTo(Attribute, 0);
        Array.Clear(Dac);
        EgaDac.CopyTo(Dac, 0);
        SequencerIndex = GraphicsIndex = CrtcIndex = AttributeIndex = 0;
        DacWriteIndex = DacReadIndex = DacWritePhase = DacReadPhase = 0;
        DacMask = 0xFF; MiscOutput = 0x67; FeatureControl = 0;
        AttributeData = false;
        Array.Clear(latch);
        Dirty = true;
    }

    public static bool Contains(uint physical) => physical - WindowBase < WindowSize;

    // Graphics Controller Misc(GR6 bit2-3)のメモリマップ選択に従い、ウィンドウ内オフセットへ変換する。
    bool Map(uint physical, out uint offset)
    {
        offset = physical - WindowBase;
        switch ((Graphics[6] >> 2) & 3)
        {
            case 0: return true;                                   // A0000-BFFFF 128K
            case 1: return offset < 0x10000;                        // A0000-AFFFF 64K
            case 2: offset -= 0x10000; return offset < 0x8000;      // B0000-B7FFF 32K
            default: offset -= 0x18000; return offset < 0x8000;     // B8000-BFFFF 32K
        }
    }

    bool Chain4 => (Sequencer[4] & 0x08) != 0;
    bool OddEven => (Sequencer[4] & 0x04) == 0;

    public byte Read(uint physical)
    {
        if (!Map(physical, out var offset)) return 0xFF;
        int plane, unit;
        if (Chain4) { plane = (int)(offset & 3); unit = (int)(offset >> 2); }
        else if (OddEven) { plane = (Graphics[4] & 2) | (int)(offset & 1); unit = (int)(offset >> 1); }
        else { plane = Graphics[4] & 3; unit = (int)offset; }
        unit &= PlaneSize - 1;
        for (var index = 0; index < 4; index++) latch[index] = Vram[index * PlaneSize + unit];
        if ((Graphics[5] & 0x08) == 0) return latch[plane];
        // 読み出しモード 1: Color Don't Care で指定されたプレーンを Color Compare と比較し、一致ビットを返す。
        var result = 0xFF;
        for (var index = 0; index < 4; index++)
        {
            if ((Graphics[7] & (1 << index)) == 0) continue;
            var compare = (Graphics[2] & (1 << index)) != 0 ? 0xFF : 0x00;
            result &= ~(latch[index] ^ compare);
        }
        return (byte)result;
    }

    public void Write(uint physical, byte value)
    {
        if (!Map(physical, out var offset)) return;
        Dirty = true;
        if (Chain4)
        {
            var plane = (int)(offset & 3);
            if ((Sequencer[2] & (1 << plane)) != 0) Vram[plane * PlaneSize + (int)((offset >> 2) & (PlaneSize - 1))] = value;
            return;
        }
        if (OddEven)
        {
            var plane = (int)(offset & 1);
            if ((Sequencer[2] & (1 << plane)) != 0) Vram[plane * PlaneSize + (int)((offset >> 1) & (PlaneSize - 1))] = value;
            return;
        }
        var unit = (int)(offset & (PlaneSize - 1));
        var rotate = Graphics[3] & 7;
        var rotated = (byte)((value >> rotate) | (value << (8 - rotate)));
        var function = (Graphics[3] >> 3) & 3;
        var bitMask = Graphics[8];
        if ((Graphics[5] & 3) == 3) { bitMask &= rotated; rotated = 0; }
        for (var plane = 0; plane < 4; plane++)
        {
            if ((Sequencer[2] & (1 << plane)) == 0) continue;
            int data;
            switch (Graphics[5] & 3)
            {
                case 0:
                    data = (Graphics[1] & (1 << plane)) != 0 ? ((Graphics[0] & (1 << plane)) != 0 ? 0xFF : 0x00) : rotated;
                    break;
                case 1:
                    Vram[plane * PlaneSize + unit] = latch[plane];
                    continue;
                case 2:
                    data = (value & (1 << plane)) != 0 ? 0xFF : 0x00;
                    break;
                default:
                    data = (Graphics[0] & (1 << plane)) != 0 ? 0xFF : 0x00;
                    break;
            }
            data = function switch
            {
                1 => data & latch[plane],
                2 => data | latch[plane],
                3 => data ^ latch[plane],
                _ => data
            };
            Vram[plane * PlaneSize + unit] = (byte)((data & bitMask) | (latch[plane] & ~bitMask));
        }
    }

    public static bool IsPort(int port) =>
        port is 0x3B4 or 0x3B5 or 0x3BA or (>= 0x3C0 and <= 0x3CF) or 0x3D4 or 0x3D5 or 0x3DA;

    // tsc は垂直帰線(Input Status 1 bit3)と表示禁止(bit0)を周期的に変化させるための時刻源。
    public byte ReadPort(int port, ulong tsc)
    {
        switch (port)
        {
            case 0x3C0: return AttributeIndex;
            case 0x3C1: return (AttributeIndex & 0x1F) < Attribute.Length ? Attribute[AttributeIndex & 0x1F] : (byte)0;
            case 0x3C2: return 0x00;
            case 0x3C3: return 0x01;
            case 0x3C4: return SequencerIndex;
            case 0x3C5: return Sequencer[SequencerIndex & 7];
            case 0x3C6: return DacMask;
            case 0x3C7: return 0x00;
            case 0x3C8: return DacWriteIndex;
            case 0x3C9:
                var component = Dac[DacReadIndex * 3 + DacReadPhase];
                if (++DacReadPhase == 3) { DacReadPhase = 0; DacReadIndex++; }
                return component;
            case 0x3CA: return FeatureControl;
            case 0x3CC: return MiscOutput;
            case 0x3CE: return GraphicsIndex;
            case 0x3CF: return GraphicsIndex < Graphics.Length ? Graphics[GraphicsIndex] : (byte)0;
            case 0x3B4 or 0x3D4: return CrtcIndex;
            case 0x3B5 or 0x3D5: return CrtcIndex < Crtc.Length ? Crtc[CrtcIndex] : (byte)0;
            case 0x3BA or 0x3DA:
                AttributeData = false;
                var retrace = (int)(tsc >> 12) & 1;
                return (byte)((retrace << 3) | retrace | ((int)(tsc >> 7) & 1));
            default: return 0xFF;
        }
    }

    public void WritePort(int port, byte value)
    {
        if (Log) Console.Error.WriteLine($"[vga] out {port:x3} <- {value:x2}");
        switch (port)
        {
            case 0x3C0:
                if (AttributeData)
                {
                    var index = AttributeIndex & 0x1F;
                    if (index < Attribute.Length) { Attribute[index] = value; Dirty = true; }
                }
                else
                    AttributeIndex = value;
                AttributeData = !AttributeData;
                break;
            case 0x3C2: MiscOutput = value; break;
            case 0x3C4: SequencerIndex = value; break;
            case 0x3C5: Sequencer[SequencerIndex & 7] = value; Dirty = true; break;
            case 0x3C6: DacMask = value; Dirty = true; break;
            case 0x3C7: DacReadIndex = value; DacReadPhase = 0; break;
            case 0x3C8: DacWriteIndex = value; DacWritePhase = 0; break;
            case 0x3C9:
                Dac[DacWriteIndex * 3 + DacWritePhase] = (byte)(value & 0x3F);
                if (++DacWritePhase == 3) { DacWritePhase = 0; DacWriteIndex++; }
                Dirty = true;
                break;
            case 0x3CE: GraphicsIndex = value; break;
            case 0x3CF: if (GraphicsIndex < Graphics.Length) { Graphics[GraphicsIndex] = value; Dirty = true; } break;
            case 0x3B4 or 0x3D4: CrtcIndex = value; break;
            case 0x3B5 or 0x3D5:
                if (CrtcIndex >= Crtc.Length) break;
                if (CrtcIndex <= 7 && (Crtc[0x11] & 0x80) != 0)
                {
                    // CRTC 0-7 保護中は Overflow の Line Compare bit8 だけ書き換えられる。
                    if (CrtcIndex == 7) Crtc[7] = (byte)((Crtc[7] & ~0x10) | (value & 0x10));
                    break;
                }
                Crtc[CrtcIndex] = value;
                Dirty = true;
                break;
            case 0x3BA or 0x3DA: FeatureControl = value; break;
        }
    }

    /// 描画 ///////////////////////////////////////////
    public bool GraphicsMode => (Graphics[6] & 1) != 0;
    public bool Color256 => (Attribute[0x10] & 0x40) != 0 || Chain4;
    int Columns => Crtc[1] + 1;
    int ScanLines => (Crtc[0x12] | ((Crtc[7] & 0x02) << 7) | ((Crtc[7] & 0x40) << 3)) + 1;
    int CharHeight => (Crtc[9] & 0x1F) + 1;
    int StartAddress => (Crtc[0x0C] << 8) | Crtc[0x0D];
    int Pitch => Crtc[0x13] * 2;

    // 表示解像度(ピクセル)。テキストは 8 ドット幅のセル、256 色モードは水平 2 倍、
    // 走査線はキャラクタ高さ(グラフィックでは行の繰り返し数)と倍走査で割る。
    public (int Width, int Height) Resolution()
    {
        var rows = ScanLines / Math.Max(1, CharHeight * ((Crtc[9] & 0x80) != 0 ? 2 : 1));
        if (!GraphicsMode) return (Columns * 8, Math.Max(1, rows) * CharHeight);
        return (Color256 ? Columns * 4 : Columns * 8, Math.Max(1, rows));
    }

    public string ModeDescription()
    {
        var (width, height) = Resolution();
        return GraphicsMode
            ? $"{width}x{height} {(Color256 ? "256" : "16")} colors"
            : $"text {Columns}x{height / CharHeight}";
    }

    uint DacColor(int index)
    {
        index = (index & DacMask) * 3;
        return 0xFF000000u | ((uint)Expand(Dac[index]) << 16) | ((uint)Expand(Dac[index + 1]) << 8) | Expand(Dac[index + 2]);
    }

    static byte Expand(byte component) => (byte)((component << 2) | (component >> 4));

    // 16 色: Color Plane Enable → パレット → Color Select(P5P4/P7P6)→ DAC。
    uint PaletteColor(int index)
    {
        var entry = Attribute[index & Attribute[0x12] & 0x0F] & 0x3F;
        var dac = (Attribute[0x10] & 0x80) != 0
            ? (entry & 0x0F) | ((Attribute[0x14] & 0x0F) << 4)
            : entry | ((Attribute[0x14] & 0x0C) << 4);
        return DacColor(dac);
    }

    // buffer には Resolution() の幅×高さ以上の要素が必要。戻り値は実際に描いた解像度。
    public (int Width, int Height) Render(uint[] buffer)
    {
        var (width, height) = Resolution();
        if (buffer.Length < width * height) throw new ArgumentException("buffer is too small", nameof(buffer));
        Dirty = false;
        if (GraphicsMode)
        {
            if (Color256) RenderChain4(buffer, width, height);
            else RenderPlanar(buffer, width, height);
        }
        else
            RenderText(buffer, width, height);
        return (width, height);
    }

    void RenderChain4(uint[] buffer, int width, int height)
    {
        var colors = new uint[256];
        for (var index = 0; index < 256; index++) colors[index] = DacColor(index);
        var pitch = Pitch;
        var start = StartAddress;
        for (var y = 0; y < height; y++)
        {
            var row = start + y * pitch;
            for (var x = 0; x < width; x++)
            {
                var unit = (row + (x >> 2)) & (PlaneSize - 1);
                buffer[y * width + x] = colors[Vram[(x & 3) * PlaneSize + unit]];
            }
        }
    }

    void RenderPlanar(uint[] buffer, int width, int height)
    {
        var colors = new uint[16];
        for (var index = 0; index < 16; index++) colors[index] = PaletteColor(index);
        var pitch = Pitch;
        var start = StartAddress;
        for (var y = 0; y < height; y++)
        {
            var row = start + y * pitch;
            for (var x = 0; x < width; x += 8)
            {
                var unit = (row + (x >> 3)) & (PlaneSize - 1);
                int p0 = Vram[unit], p1 = Vram[PlaneSize + unit], p2 = Vram[2 * PlaneSize + unit], p3 = Vram[3 * PlaneSize + unit];
                for (var bit = 7; bit >= 0; bit--)
                {
                    var color = ((p0 >> bit) & 1) | (((p1 >> bit) & 1) << 1) | (((p2 >> bit) & 1) << 2) | (((p3 >> bit) & 1) << 3);
                    buffer[y * width + x + (7 - bit)] = colors[color];
                }
            }
        }
    }

    // プレーン 2 に文字パターンが読み込まれているか(VGA BIOS がフォントを転送したか)。
    bool HasLoadedFont()
    {
        var plane = Vram.AsSpan(2 * PlaneSize, 256 * 32);
        return plane.IndexOfAnyExcept((byte)0) >= 0;
    }

    void RenderText(uint[] buffer, int width, int height)
    {
        var colors = new uint[16];
        for (var index = 0; index < 16; index++) colors[index] = PaletteColor(index);
        var columns = Columns;
        var charHeight = CharHeight;
        var rows = height / charHeight;
        var pitch = Pitch;
        var start = StartAddress;
        var blink = (Attribute[0x10] & 0x08) != 0;
        var loadedFont = HasLoadedFont();
        var fallback = FallbackFont;
        var cursor = (Crtc[0x0A] & 0x20) != 0 ? -1 : ((Crtc[0x0E] << 8) | Crtc[0x0F]) - start;
        var cursorTop = Crtc[0x0A] & 0x1F;
        var cursorBottom = Crtc[0x0B] & 0x1F;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var unit = (start + row * pitch + column) & (PlaneSize - 1);
                int character = Vram[unit], attribute = Vram[PlaneSize + unit];
                var foreground = colors[attribute & 0x0F];
                var background = colors[(attribute >> 4) & (blink ? 0x07 : 0x0F)];
                var isCursor = row * pitch + column == cursor;
                for (var line = 0; line < charHeight; line++)
                {
                    int pattern;
                    if (loadedFont) pattern = Vram[2 * PlaneSize + character * 32 + line];
                    else if (fallback != null && line < 16) pattern = fallback[character * 16 + line];
                    else pattern = character != 0 && line is > 1 and < 14 ? 0x7E : 0;
                    if (isCursor && line >= cursorTop && line <= cursorBottom) pattern = 0xFF;
                    var offset = (row * charHeight + line) * width + column * 8;
                    for (var bit = 0; bit < 8; bit++)
                        buffer[offset + bit] = (pattern & (0x80 >> bit)) != 0 ? foreground : background;
                }
            }
        }
    }

    /// スナップショット(v8 から) ///////////////////////////////
    static readonly Codec<VgaDevice> Layout = new Codec<VgaDevice>()
        .Bytes(v => v.Vram)
        .Bytes(v => v.Sequencer).Bytes(v => v.Graphics).Bytes(v => v.Crtc).Bytes(v => v.Attribute).Bytes(v => v.Dac)
        .U8(v => v.SequencerIndex, (v, x) => v.SequencerIndex = x)
        .U8(v => v.GraphicsIndex, (v, x) => v.GraphicsIndex = x)
        .U8(v => v.CrtcIndex, (v, x) => v.CrtcIndex = x)
        .U8(v => v.AttributeIndex, (v, x) => v.AttributeIndex = x)
        .U8(v => v.DacWriteIndex, (v, x) => v.DacWriteIndex = x)
        .U8(v => v.DacReadIndex, (v, x) => v.DacReadIndex = x)
        .U8(v => v.DacWritePhase, (v, x) => v.DacWritePhase = x)
        .U8(v => v.DacReadPhase, (v, x) => v.DacReadPhase = x)
        .U8(v => v.DacMask, (v, x) => v.DacMask = x)
        .U8(v => v.MiscOutput, (v, x) => v.MiscOutput = x)
        .U8(v => v.FeatureControl, (v, x) => v.FeatureControl = x)
        .Bool(v => v.AttributeData, (v, x) => v.AttributeData = x)
        .Bytes(v => v.latch);

    public void SaveState(BinaryWriter writer) => Layout.Save(writer, this);

    public void LoadState(BinaryReader reader)
    {
        Layout.Load(reader, this, SnapshotStore.Version);
        Dirty = true;
    }
}
