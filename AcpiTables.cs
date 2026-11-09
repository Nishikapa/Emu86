using System.Text;

namespace Emu86;

// SeaBIOS が RAM に置いた ACPI テーブルの手直し。
// 同梱の SeaBIOS(QEMU 向けビルド)は、ローカル APIC と I/O APIC を記述した MADT("APIC" テーブル)を必ず作る。
// このエミュレータには APIC が無く(CPUID も APIC を報告しない)、割り込みは 8259 PIC だけなので、MADT を残すと
// それを信じた OS が APIC を使おうとする。Windows 7 のローダは MADT があると APIC 用の HAL(halmacpi)を選び、
// その HAL は I/O APIC が見つからず HAL_INITIALIZATION_FAILED(0x5C)で止まる。
// POST の後(INT 19h)に RSDT/XSDT から MADT への項を取り除き、PIC だけの機械として見せる(PIC 用の HAL が選ばれる)。
static class AcpiTables
{
    const int HeaderSize = 36;      // 記述ヘッダ: 署名(4)、長さ(4)、版、チェックサム(オフセット 9)…

    // RSDT(と、あれば XSDT)から MADT への項を取り除く。取り除いたら true。
    public static bool HideMadt(EmuEnvironment env)
    {
        var ram = env.Ram;
        if (FindRsdp(ram) is not int rsdp) return false;
        var removed = Remove(ram, BitConverter.ToUInt32(ram, rsdp + 16), "RSDT", entrySize: 4);
        if (ram[rsdp + 15] >= 2)    // ACPI 2.0 以降の RSDP は XSDT(64 ビットの項)も指す
            removed |= Remove(ram, (long)BitConverter.ToUInt64(ram, rsdp + 24), "XSDT", entrySize: 8);
        if (removed) Console.Error.WriteLine("[acpi] MADT hidden: no APIC is emulated, interrupts go through the 8259 PIC");
        return removed;
    }

    // RSDP("RSD PTR "、先頭 20 バイトの和が 0)を、EBDA の先頭 1KB と 0xE0000-0xFFFFF の 16 バイト境界から探す。
    static int? FindRsdp(byte[] ram)
    {
        if (ram.Length < 0x100000) return null;
        var ebda = BitConverter.ToUInt16(ram, 0x40E) << 4;
        var candidates = Enumerable.Range(0, 64).Select(index => ebda + index * 16)
            .Concat(Enumerable.Range(0, 0x20000 / 16).Select(index => 0xE0000 + index * 16));
        foreach (var address in candidates)
            if (address + 36 <= ram.Length && ram.AsSpan(address, 8).SequenceEqual("RSD PTR "u8) && Sum(ram, address, 20) == 0)
                return address;
        return null;
    }

    static byte Sum(byte[] ram, long address, int length)
    {
        var sum = 0;
        for (var index = 0; index < length; index++) sum += ram[address + index];
        return (byte)sum;
    }

    static bool HasSignature(byte[] ram, long address, string signature) =>
        address >= 0 && address + HeaderSize <= ram.Length && Encoding.ASCII.GetString(ram, (int)address, 4) == signature;

    // 表(RSDT か XSDT)の項のうち "APIC" テーブルを指すものを詰めて消し、長さとチェックサムを直す。
    static bool Remove(byte[] ram, long table, string signature, int entrySize)
    {
        if (!HasSignature(ram, table, signature)) return false;
        var length = BitConverter.ToInt32(ram, (int)table + 4);
        if (length < HeaderSize || table + length > ram.Length) return false;
        var removed = false;
        for (var entry = table + HeaderSize; entry + entrySize <= table + length;)
        {
            var target = entrySize == 8 ? (long)BitConverter.ToUInt64(ram, (int)entry) : BitConverter.ToUInt32(ram, (int)entry);
            if (!HasSignature(ram, target, "APIC")) { entry += entrySize; continue; }
            Buffer.BlockCopy(ram, (int)entry + entrySize, ram, (int)entry, (int)(table + length - entry - entrySize));
            length -= entrySize;
            Array.Clear(ram, (int)(table + length), entrySize);
            removed = true;
        }
        if (!removed) return false;
        BitConverter.GetBytes(length).CopyTo(ram, (int)table + 4);
        ram[table + 9] = 0;
        ram[table + 9] = (byte)-Sum(ram, table, length);
        return true;
    }
}
