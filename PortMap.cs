namespace Emu86;

// I/O ポートの表(docs: README「I/O ポート」)。1 項 = ポート範囲と、そこを担当するデバイスの読み書き。
//   Read/Write     : 1 バイトの入出力(EnvInPort/EnvOutPort)。null ならその向きは担当しない(次の項か IoPort 配列へ)。
//   ReadWide/WriteWide: 幅付きの入出力を自分で扱う項(ATA データポート、PCI コンフィグ、ACPI の dword など)。
//                    null なら幅付きアクセスは 1 バイトずつ Read/Write に分ける。
//   When           : デバイスの有無や PCI の BAR で決まる範囲の条件(実行時に判定)。
// 表は起動時にポート番号ごとの配列(ByPort)に展開し、入出力は該当する項を表の順に試すだけ。
sealed record PortMapping(
    string Name, int Lo, int Hi,
    Func<EmuEnvironment, int, byte> Read = null,
    Action<EmuEnvironment, int, byte> Write = null,
    Func<EmuEnvironment, int, int, uint> ReadWide = null,
    Action<EmuEnvironment, int, int, uint> WriteWide = null,
    Func<EmuEnvironment, int, bool> When = null)
{
    public bool Applies(EmuEnvironment env, int port) => When == null || When(env, port);
}

static class PortMap
{
    static PortMapping Port(string name, int port, Func<EmuEnvironment, int, byte> read = null, Action<EmuEnvironment, int, byte> write = null, Func<EmuEnvironment, int, bool> when = null) =>
        new(name, port, port, read, write, When: when);

    static PortMapping Range(string name, int lo, int hi, Func<EmuEnvironment, int, byte> read = null, Action<EmuEnvironment, int, byte> write = null, Func<EmuEnvironment, int, bool> when = null) =>
        new(name, lo, hi, read, write, When: when);

    static readonly Action<EmuEnvironment, int, byte> Ignore = (_, _, _) => { };
    static readonly Func<EmuEnvironment, int, byte> Zero = (_, _) => 0;
    static bool HasAta(EmuEnvironment env, int _) => env.Ata != null;
    static uint Mask(int size) => size >= 4 ? 0xFFFFFFFF : (1u << (8 * size)) - 1;

    // 表(順に試す。重なる範囲は先の項が勝つ)。
    public static readonly PortMapping[] Table =
    [
        Port("CMOS index", 0x70, write: (env, _, v) => env.CmosRtc.Select(v)),
        Port("CMOS data", 0x71, read: (env, _) => env.CmosRtc.Read(env.Tsc), write: (env, _, v) => env.CmosRtc.Write(v, env.Tsc)),
        Port("PIT counter 0", 0x40, read: (env, p) => env.Pit.Read(p)),
        Port("PIT counter 2", 0x42, read: (env, p) => env.Pit.Read(p), write: (env, p, v) => env.Pit.Write(p, v)),
        Port("PIT control", 0x43, write: (env, p, v) => env.Pit.Write(p, v)),
        Port("PIT gate/speaker", 0x61, read: (env, p) => env.Pit.Read(p), write: (env, p, v) => env.Pit.Write(p, v)),
        Port("debug console", 0x402, write: (_, _, v) => Console.Error.Write((char)v)),
        Port("PIC master command", 0x20, read: (env, _) => env.PicMaster.Isr, write: (env, _, v) => env.PicMaster.WriteCommand(v)),
        Port("PIC master data", 0x21, read: (env, _) => env.PicMaster.Mask, write: (env, _, v) => env.PicMaster.WriteData(v, env.PicLog)),
        Port("PIC slave command", 0xA0, read: (env, _) => env.PicSlave.Isr, write: (env, _, v) => env.PicSlave.WriteCommand(v)),
        Port("PIC slave data", 0xA1, read: (env, _) => env.PicSlave.Mask, write: (env, _, v) => env.PicSlave.WriteData(v, env.PicLog)),
        Port("ACPI SMI command", 0xB2, write: (env, p, v) => { env.Acpi.Write(p, v); env.IoPort[p] = v; }),
        // PIIX4 リセット制御: bit2 でシステムリセット(bit1 でフル/ハードリセットの区別、どちらも再起動)。
        Port("PIIX4 reset control", 0xCF9, write: (env, p, v) => { if ((v & 0x04) != 0) env.ResetRequested = true; env.IoPort[p] = v; }),
        // ACPI PM I/O ブロック(0xB000 起点): +0x00 PM1a_STS / +0x02 PM1a_EN / +0x04 PM1a_CNT / +0x08 PM_TMR(24bit)。
        // dword 境界の幅付き読み出しは ACPI が自分で扱う。
        Range("ACPI PM", 0xB000, 0xB03F, read: (env, p) => env.Acpi.Read(p, env.Tsc), write: (env, p, v) => env.Acpi.Write(p, v)),
        new("ACPI PM (dword)", 0xB000, 0xB03F, ReadWide: (env, p, size) => env.Acpi.ReadWord(p, env.Tsc) & Mask(size), When: (_, p) => (p & 3) == 0),
        Range("ACPI GPE", 0xAFE0, 0xAFE3, read: (env, p) => env.Acpi.Read(p, env.Tsc), write: (env, p, v) => env.Acpi.Write(p, v)),
        Range("SMBus (no device)", 0xB100, 0xB10F, read: Zero, write: Ignore), // ホストステータス=アイドル
        // バスマスタ IDE(BMIDE)レジスタ: DMA は未実装。ステータスは常に「非活性・割り込みなし」。
        // ディスクの IDENTIFY も DMA 非対応を報告するので、ドライバは PIO を使う。範囲は PCI の BAR で決まる。
        Range("bus-master IDE", 0, 0xFFFF, read: Zero, write: Ignore,
            when: (env, p) => env.Pci.IdeBmBase != 0 && p >= env.Pci.IdeBmBase && p < env.Pci.IdeBmBase + 16),
        Port("8042 data", 0x60, read: (env, p) => env.Keyboard.Read(p), write: (env, p, v) => env.Keyboard.Write(p, v)),
        Port("8042 command", 0x64, read: (env, p) => env.Keyboard.Read(p), write: (env, p, v) => env.Keyboard.Write(p, v)),
        Range("VGA (mono CRTC)", 0x3B4, 0x3B5, read: (env, p) => env.Vga.ReadPort(p, env.Tsc), write: (env, p, v) => env.Vga.WritePort(p, v)),
        Port("VGA (mono status)", 0x3BA, read: (env, p) => env.Vga.ReadPort(p, env.Tsc), write: (env, p, v) => env.Vga.WritePort(p, v)),
        Range("VGA", 0x3C0, 0x3CF, read: (env, p) => env.Vga.ReadPort(p, env.Tsc), write: (env, p, v) => env.Vga.WritePort(p, v)),
        Range("VGA (color CRTC)", 0x3D4, 0x3D5, read: (env, p) => env.Vga.ReadPort(p, env.Tsc), write: (env, p, v) => env.Vga.WritePort(p, v)),
        Port("VGA (color status)", 0x3DA, read: (env, p) => env.Vga.ReadPort(p, env.Tsc), write: (env, p, v) => env.Vga.WritePort(p, v)),
        Range("COM1", 0x3F8, 0x3FF, read: (env, p) => env.Com1.Read(p - 0x3F8), write: (env, p, v) => env.Com1.Write(p - 0x3F8, v)),
        Range("COM2", 0x2F8, 0x2FF, read: (env, p) => env.Com2.Read(p - 0x2F8), write: (env, p, v) => env.Com2.Write(p - 0x2F8, v)),
        // ATA データポート: 16/32 ビットアクセスでデバイスバッファから連続バイトを転送する(隣接ポートへは波及しない)。
        new("ATA data", 0x1F0, 0x1F0, (env, _) => (byte)env.Ata.ReadData(1), (env, _, v) => env.Ata.WriteData(1, v),
            ReadWide: (env, _, size) => env.Ata.ReadData(size), WriteWide: (env, _, size, v) => env.Ata.WriteData(size, v), When: HasAta),
        Range("ATA registers", 0x1F1, 0x1F7, read: (env, p) => env.Ata.ReadReg(p), write: (env, p, v) => env.Ata.WriteReg(p, v), when: HasAta),
        Port("ATA control", 0x3F6, read: (env, p) => env.Ata.ReadReg(p), write: (env, p, v) => env.Ata.WriteReg(p, v), when: HasAta),
        // 第 2 IDE チャネル(ディスク無し)はフローティングバス 0xFF を返し、libata に「デバイス無し」と即断させる(幅付き読み出しのみ)。
        new("secondary IDE (no device)", 0x170, 0x177, ReadWide: (_, _, size) => Mask(size)),
        new("secondary IDE control (no device)", 0x376, 0x376, ReadWide: (_, _, size) => Mask(size)),
        // PCI コンフィグ: CONFIG_ADDRESS(0xCF8、バイト位置に合わせて更新)と CONFIG_DATA(0xCFC)。幅付きアクセスのみ。
        // 0xCF9 の 1 バイト書き込みはリセット制御(上の項)。
        new("PCI CONFIG_ADDRESS", 0xCF8, 0xCFB,
            ReadWide: (env, p, _) => env.Pci.Address >> (8 * (p - 0xCF8)),
            WriteWide: (env, p, size, v) =>
            {
                if (p == 0xCF9 && size == 1) { Ext.EnvOutPort(env, p, (byte)v); return; }
                var shift = 8 * (p - 0xCF8);
                var mask = size >= 4 ? 0xFFFFFFFF : (Mask(size) << shift);
                env.Pci.Address = (env.Pci.Address & ~mask) | ((v << shift) & mask);
            }),
        new("PCI CONFIG_DATA", 0xCFC, 0xCFF,
            ReadWide: (env, p, size) => env.Pci.DataRead(p - 0xCFC, size),
            WriteWide: (env, p, size, v) => env.Pci.DataWrite(p - 0xCFC, size, v)),
    ];

    // ポート番号ごとの該当する項(表の順)。該当なしは共有の空配列。
    public static readonly PortMapping[][] ByPort = Expand();

    static PortMapping[][] Expand()
    {
        var byPort = new PortMapping[0x10000][];
        var lists = new Dictionary<string, PortMapping[]>();
        for (var port = 0; port < byPort.Length; port++)
        {
            var mappings = Table.Where(mapping => port >= mapping.Lo && port <= mapping.Hi).ToArray();
            var key = string.Join("|", mappings.Select(mapping => mapping.Name));
            if (!lists.TryGetValue(key, out var shared)) lists[key] = shared = mappings;
            byPort[port] = shared;
        }
        return byPort;
    }
}
