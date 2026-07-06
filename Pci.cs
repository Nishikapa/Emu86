namespace Emu86;

using System.Buffers.Binary;

// 最小限の PCI ホスト(コンフィグ機構 #1: ポート 0xCF8 CONFIG_ADDRESS / 0xCFC CONFIG_DATA)。
// Linux の ata_piix ドライバを bind させるだけの目的で、QEMU/Bochs 相当の
// i440FX + PIIX3(ISA ブリッジ + IDE)を bus 0 に露出する。
// IDE はレガシー(互換)モードにしてあり、BAR はすべて 0(BMDMA 無し)。
// そのため libata は既存の ATA PIO ポート(0x1F0/0x3F6)を PIO で叩く。
public class PciHost
{
    // 0xCF8 に書かれる CONFIG_ADDRESS ラッチ。bit31=enable, 23:16=bus, 15:11=dev, 10:8=fn, 7:2=reg。
    public uint Address;

    // 存在する各ファンクションの 256 バイト・コンフィグ空間。キー = dev<<3 | fn。
    readonly Dictionary<int, byte[]> cfg = new();

    public PciHost()
    {
        // 00:00.0 Intel 440FX ホストブリッジ
        cfg[(0 << 3) | 0] = Make(0x8086, 0x1237, baseClass: 0x06, subClass: 0x00, progIf: 0x00, header: 0x00);
        // 00:01.0 PIIX3 ISA ブリッジ(マルチファンクション: header bit7=1 で fn1 も走査させる)
        cfg[(1 << 3) | 0] = Make(0x8086, 0x7000, baseClass: 0x06, subClass: 0x01, progIf: 0x00, header: 0x80);
        // 00:01.1 PIIX3 IDE(mass storage / IDE / prog-if=0x80: 両チャネル互換モード + バスマスタ能力)
        cfg[(1 << 3) | 1] = Make(0x8086, 0x7010, baseClass: 0x01, subClass: 0x01, progIf: 0x80, header: 0x00);
    }

    static byte[] Make(ushort vendor, ushort device, byte baseClass, byte subClass, byte progIf, byte header)
    {
        var c = new byte[256];
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(0x00), vendor);
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(0x02), device);
        // コマンド: I/O 空間 + メモリ空間 + バスマスタを有効化済みにしておく。
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(0x04), 0x0007);
        // ステータス: DEVSEL=medium(0x0200)。
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(0x06), 0x0200);
        c[0x08] = 0x02;      // リビジョン
        c[0x09] = progIf;
        c[0x0A] = subClass;
        c[0x0B] = baseClass;
        c[0x0E] = header;
        // 割り込み: IDE はレガシー IRQ14/15 を使うため PCI INTx は使わない(pin=0)。
        return c;
    }

    // reg = dword 境界のレジスタ番号(Address の 7:2)、off = ポート下位 2 ビット(0xCFC からのバイトずれ)。
    byte[] Selected(out int reg)
    {
        reg = (int)(Address & 0xFC);
        var bus = (int)(Address >> 16) & 0xFF;
        var dev = (int)(Address >> 11) & 0x1F;
        var fn = (int)(Address >> 8) & 0x7;
        if ((Address & 0x80000000) == 0 || bus != 0) return null;
        return cfg.TryGetValue((dev << 3) | fn, out var c) ? c : null;
    }

    // CONFIG_DATA 読み出し。存在しないデバイスは 0xFFFF...(ベンダ ID = 0xFFFF)。
    public uint DataRead(int off, int size)
    {
        var c = Selected(out var reg);
        uint v = 0;
        for (int i = 0; i < size; i++)
        {
            int idx = reg + off + i;
            byte b = (c != null && idx < 256) ? c[idx] : (byte)0xFF;
            v |= (uint)b << (8 * i);
        }
        return v;
    }

    // CONFIG_DATA 書き込み。コマンドレジスタ等の書き換えのみ受ける。
    // BAR(0x10-0x27)は互換モードのため常に 0 のままにし、サイズ問い合わせに 0 を返す
    //(= 未実装扱い → libata は BMDMA を諦めて PIO 動作になる)。
    public void DataWrite(int off, int size, uint val)
    {
        var c = Selected(out var reg);
        if (c == null) return;
        for (int i = 0; i < size; i++)
        {
            int idx = reg + off + i;
            if (idx >= 256) break;
            if (idx >= 0x10 && idx < 0x28) continue;         // BAR は読み取り専用 0
            if (idx == 0x0E || (idx >= 0x08 && idx <= 0x0B)) continue; // ヘッダ種別/クラスは不変
            if (idx < 0x04) continue;                         // ベンダ/デバイス ID は不変
            c[idx] = (byte)(val >> (8 * i));
        }
    }
}
