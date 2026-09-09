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

    // --pcilog: コンフィグ空間アクセスを標準エラーへ記録する(OS の PCI 列挙手順の調査用)。
    public static bool Log;
    public static uint Eip; // ログ用: アクセス元の EIP(ランナーが命令ごとに更新)
    public static Func<string> Caller = () => ""; // ログ用: EBP チェーンの戻り番地列(ランナーが設定)

    // 存在する各ファンクションの 256 バイト・コンフィグ空間。キー = dev<<3 | fn。
    readonly Dictionary<int, byte[]> cfg = new();

    // 実装済み BAR の書き込み可能ビットマスク(サイズ問い合わせ用)。キー = (fn key, レジスタオフセット)。
    // 未登録の BAR / 拡張 ROM BAR(0x30)は読み取り専用 0(未実装)。
    readonly Dictionary<(int fn, int off), uint> barMask = new();

    // PIIX3 IDE のバスマスタ I/O ベース(BAR4)。SeaBIOS/OS が割り当て直す。
    public uint IdeBmBase
    {
        get
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(cfg[(1 << 3) | 1].AsSpan(0x20));
            return (v & 1) != 0 ? v & 0xFFF0 : 0;
        }
    }

    public PciHost()
    {
        // 00:00.0 Intel 440FX ホストブリッジ
        cfg[(0 << 3) | 0] = Make(0x8086, 0x1237, baseClass: 0x06, subClass: 0x00, progIf: 0x00, header: 0x00);
        // 00:01.0 PIIX4 ISA ブリッジ(マルチファンクション: header bit7=1 で fn1/fn3 も走査させる)
        // 00:01.1 PIIX4 IDE(mass storage / IDE / prog-if=0x80: 両チャネル互換モード + バスマスタ能力)
        // デバイス ID は PIIX3(7000/7010)ではなく PIIX4(7110/7111)にする。Windows XP のディスクイメージ
        // (Virtual PC 由来)の CriticalDeviceDatabase には pci#ven_8086&dev_7111 -> intelide しか無く、
        // 7010 だとブート時にドライバが結び付かず INACCESSIBLE_BOOT_DEVICE(0x7B)になるため。
        // SeaBIOS/Linux はどちらの ID でも同じ扱い(piix_isa_setup / piix_ide_setup / ata_piix)。
        cfg[(1 << 3) | 0] = Make(0x8086, 0x7110, baseClass: 0x06, subClass: 0x01, progIf: 0x00, header: 0x80);
        cfg[(1 << 3) | 0][0x08] = 0x01; // リビジョン(PIIX4)
        cfg[(1 << 3) | 1] = Make(0x8086, 0x7111, baseClass: 0x01, subClass: 0x01, progIf: 0x80, header: 0x00);
        cfg[(1 << 3) | 1][0x08] = 0x01;
        // IDE BAR4: バスマスタ IDE レジスタ(I/O、16 バイト)。Windows の pciidex は資源リストが
        // 空だと FDO の開始に失敗するため、レジスタ自体はほぼダミーでも BAR として露出する。
        BinaryPrimitives.WriteUInt32LittleEndian(cfg[(1 << 3) | 1].AsSpan(0x20), 0xC001);
        barMask[((1 << 3) | 1, 0x20)] = 0xFFFFFFF0;
        // 00:01.3 PIIX4 電源管理(ACPI)。SeaBIOS はこのデバイスを見つけたときだけ
        // ACPI テーブル(RSDP/RSDT/FADT/DSDT…)を生成する。Windows の ACPI HAL(halacpi)は
        // ACPI テーブルが無いと MISMATCHED_HAL(0x79) でバグチェックするため必須。
        // PM I/O ブロックは 0xB000、SMBus は 0xB100(QEMU の PIIX4 と同じ配置)。
        var pm = Make(0x8086, 0x7113, baseClass: 0x06, subClass: 0x80, progIf: 0x00, header: 0x00);
        pm[0x08] = 0x03;                                             // リビジョン
        pm[0x3D] = 0x01;                                             // 割り込みピン INTA(SCI)
        BinaryPrimitives.WriteUInt32LittleEndian(pm.AsSpan(0x40), 0xB001); // PMBA(bit0=I/O 空間)
        pm[0x80] = 0x01;                                             // PMREGMISC: PM I/O 有効
        BinaryPrimitives.WriteUInt32LittleEndian(pm.AsSpan(0x90), 0xB101); // SMBBA
        pm[0xD2] = 0x09;                                             // SMBus ホスト有効
        // DEVACTB(0x58) bit25 = APMC_EN。SeaBIOS はこれが立っていると「SMM 初期化済み」と
        // みなして SMM の再配置(APM ポート 0xB2/0xB3 経由の SMI 待ち)を省略する。
        // SMM は未実装なので、待ちに入ると永久にループするため最初から立てておく。
        BinaryPrimitives.WriteUInt32LittleEndian(pm.AsSpan(0x58), 0x02000000);
        cfg[(1 << 3) | 3] = pm;
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
        if (Log && c != null)
            Console.Error.WriteLine($"[pci] rd {Bdf()} reg {reg + off:x2} size {size} -> {v:x} @{Eip:x8} {Caller()}");
        return v;
    }

    int KeyOf() => (((int)(Address >> 11) & 0x1F) << 3) | ((int)(Address >> 8) & 7);

    string Bdf() => $"{(Address >> 16) & 0xFF:x2}:{(Address >> 11) & 0x1F:x2}.{(Address >> 8) & 7}";

    // CONFIG_DATA 書き込み。コマンドレジスタ等の書き換えのみ受ける。
    // BAR(0x10-0x27)は互換モードのため常に 0 のままにし、サイズ問い合わせに 0 を返す
    //(= 未実装扱い → libata は BMDMA を諦めて PIO 動作になる)。
    public void DataWrite(int off, int size, uint val)
    {
        var c = Selected(out var reg);
        if (c == null) return;
        if (Log) Console.Error.WriteLine($"[pci] wr {Bdf()} reg {reg + off:x2} size {size} <- {val:x} @{Eip:x8} {Caller()}");
        for (int i = 0; i < size; i++)
        {
            int idx = reg + off + i;
            if (idx >= 256) break;
            if (idx >= 0x10 && idx < 0x34)                    // BAR / 拡張 ROM BAR
            {
                var bar = idx & ~3;
                if (!barMask.TryGetValue((KeyOf(), bar), out var mask)) continue; // 未実装 BAR は読み取り専用
                var mb = (byte)(mask >> (8 * (idx & 3)));
                c[idx] = (byte)((c[idx] & ~mb) | ((byte)(val >> (8 * i)) & mb));
                continue;
            }
            if (idx == 0x0E || (idx >= 0x08 && idx <= 0x0B)) continue; // ヘッダ種別/クラスは不変
            if (idx < 0x04) continue;                         // ベンダ/デバイス ID は不変
            c[idx] = (byte)(val >> (8 * i));
        }
    }
}
