namespace Emu86;

internal sealed class AcpiPmDevice(GuestClock clock)
{
    public ushort Pm1Sts, Pm1En, Pm1Cnt;
    public ushort Gpe0Sts, Gpe0En;

    // PM タイマ(3.579545 MHz、24 ビット)はゲストの時計で進む。
    public uint Timer(ulong tsc) => (uint)clock.PmTicks(tsc) & 0xFFFFFF;

    // 16 ビットレジスタの表(Lens): 偶数番地 → 読む/書く関数。バイト単位の入出力は番地の下位ビットでレーン(下位/上位)を選ぶ。
    // ClearOnWrite は状態レジスタ(W1C: 書いた 1 のビットを落とす)、そうでなければ値をそのまま置く。
    // PM1_CNT の SLP_EN(bit13)は書き込み専用で、スリープ要求を記録して読み戻しは 0。タイマ(+0x08)は読み出し専用なので表に無い。
    readonly record struct Word(Func<AcpiPmDevice, ushort> Get, Action<AcpiPmDevice, ushort> Set, bool ClearOnWrite = false);

    static readonly Dictionary<int, Word> Words = new()
    {
        [0xB000] = new(acpi => acpi.Pm1Sts, (acpi, value) => acpi.Pm1Sts = value, ClearOnWrite: true),
        [0xB002] = new(acpi => acpi.Pm1En, (acpi, value) => acpi.Pm1En = value),
        [0xB004] = new(acpi => acpi.Pm1Cnt, (acpi, value) =>
        {
            if ((value & 0x2000) != 0)
                Console.Error.WriteLine($"[acpi] sleep requested: SLP_TYP={(value >> 10) & 7} (PM1_CNT={value:x4})");
            acpi.Pm1Cnt = (ushort)(value & 0xDFFF);
        }),
        [0xAFE0] = new(acpi => acpi.Gpe0Sts, (acpi, value) => acpi.Gpe0Sts = value, ClearOnWrite: true),
        [0xAFE2] = new(acpi => acpi.Gpe0En, (acpi, value) => acpi.Gpe0En = value),
    };

    ushort ReadWord16(int port) => Words.TryGetValue(port, out var word) ? word.Get(this) : (ushort)0;

    // dword 境界の幅付き読み出し(PM1a_STS|PM1a_EN、PM1a_CNT、PM_TMR)。
    public uint ReadWord(int port, ulong tsc) =>
        ((port - 0xB000) & ~3) == 0x08 ? Timer(tsc) : (uint)(ReadWord16(port) | (ReadWord16(port + 2) << 16));

    public byte Read(int port, ulong tsc) => (byte)(ReadWord(port & ~3, tsc) >> (8 * (port & 3)));

    public void Write(int port, byte value)
    {
        // APM コマンド(FADT の SMI_CMD)。SMM の代わりに直接 SCI_EN を操作する:
        // ACPI_ENABLE(0xF1) で PM1_CNT.SCI_EN=1、ACPI_DISABLE(0xF0) で 0(QEMU の PIIX4 と同じ)。
        if (port == 0xB2)
        {
            if (value == 0xF1) Pm1Cnt |= 1;
            else if (value == 0xF0) Pm1Cnt &= 0xFFFE;
            Console.Error.WriteLine($"[acpi] APMC=0x{value:x2} -> SCI_EN={Pm1Cnt & 1}");
            return;
        }
        if (!Words.TryGetValue(port & ~1, out var word)) return;
        var shift = 8 * (port & 1);
        var current = word.Get(this);
        word.Set(this, word.ClearOnWrite
            ? (ushort)(current & ~(value << shift))
            : (ushort)((current & ~(0xFF << shift)) | (value << shift)));
    }
}
