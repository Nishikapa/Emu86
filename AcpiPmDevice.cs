namespace Emu86;

internal sealed class AcpiPmDevice
{
    public ushort Pm1Sts, Pm1En, Pm1Cnt;
    public ushort Gpe0Sts, Gpe0En;

    public static uint Timer(ulong tsc) => (uint)(tsc * 3579545UL / 1_000_000UL) & 0xFFFFFF;

    public uint ReadWord(int port, ulong tsc)
    {
        switch ((port - 0xB000) & ~3)
        {
            case 0x00: return (uint)Pm1Sts | ((uint)Pm1En << 16);
            case 0x04: return Pm1Cnt;
            case 0x08: return Timer(tsc);
            default: return 0;
        }
    }

    public byte Read(int port, ulong tsc) => port switch
    {
        0xAFE0 or 0xAFE1 => (byte)(Gpe0Sts >> (8 * (port & 1))),
        0xAFE2 or 0xAFE3 => (byte)(Gpe0En >> (8 * (port & 1))),
        _ => (byte)(ReadWord(port, tsc) >> (8 * (port & 3)))
    };

    public void Write(int port, byte val)
    {
        switch (port)
        {
            case 0xB2: // APM コマンド(FADT の SMI_CMD)。SMM の代わりに直接 SCI_EN を操作する。
                       // ACPI_ENABLE(0xF1) で PM1_CNT.SCI_EN=1、ACPI_DISABLE(0xF0) で 0(QEMU の PIIX4 と同じ)。
                if (val == 0xF1) Pm1Cnt |= 1;
                else if (val == 0xF0) Pm1Cnt &= 0xFFFE;
                System.Console.Error.WriteLine($"[acpi] APMC=0x{val:x2} -> SCI_EN={Pm1Cnt & 1}");
                break;
            case 0xAFE0: Gpe0Sts &= (ushort)~val; break;                       // GPE0_STS: W1C
            case 0xAFE1: Gpe0Sts &= (ushort)~(val << 8); break;
            case 0xAFE2: Gpe0En = (ushort)((Gpe0En & 0xFF00) | val); break;
            case 0xAFE3: Gpe0En = (ushort)((Gpe0En & 0x00FF) | (val << 8)); break;
            default: WritePm(port, val); break;
        }
    }

    void WritePm(int port, byte val)
    {
        int off = port - 0xB000;
        switch (off)
        {
            case 0x00: Pm1Sts &= (ushort)~val; break;                          // W1C
            case 0x01: Pm1Sts &= (ushort)~(val << 8); break;
            case 0x02: Pm1En = (ushort)((Pm1En & 0xFF00) | val); break;
            case 0x03: Pm1En = (ushort)((Pm1En & 0x00FF) | (val << 8)); break;
            case 0x04: Pm1Cnt = (ushort)((Pm1Cnt & 0xFF00) | val); break;
            case 0x05:
                Pm1Cnt = (ushort)((Pm1Cnt & 0x00FF) | (val << 8));
                if ((Pm1Cnt & 0x2000) != 0) // SLP_EN
                    System.Console.Error.WriteLine($"[acpi] sleep requested: SLP_TYP={(Pm1Cnt >> 10) & 7} (PM1_CNT={Pm1Cnt:x4})");
                Pm1Cnt &= 0xDFFF; // SLP_EN は書き込み専用で読み戻しは 0
                break;
            default: break; // タイマ等は読み取り専用
        }
    }
}
