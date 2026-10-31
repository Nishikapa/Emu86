using System.Runtime.CompilerServices;

namespace Emu86;

// ページング変換状態と TLB。CPU の CR0.PG/CR0.WP/CR3/CR4.PAE のミラーで、CR 書き込みとスナップショット復元時に
// Sync で同期する。TLB は直結マップ(tag = 0x80000000 | vpn、0 は無効)。読み取り用と、Dirty ビット設定済みを
// 保証する書き込み用を分けて持つ。いずれも過渡状態なのでスナップショットには保存しない(復元時に再同期される)。
public sealed class Paging
{
    public bool Enabled;
    public bool Pae;
    public bool WriteProtect; // CR0.WP: リング0 の読み取り専用ページ書き込みを禁じる
    public uint Cr3Base;

    public const int TlbSize = 4096;
    public readonly uint[] TagR = new uint[TlbSize], PhysR = new uint[TlbSize];
    public readonly uint[] TagW = new uint[TlbSize], PhysW = new uint[TlbSize];

    // CPU の制御レジスタを反映し、TLB を捨てる。
    public void Sync(CPU cpu)
    {
        Enabled = cpu.pg;
        Pae = 0 != (cpu.cr4 & 0x20);
        WriteProtect = cpu.wp;
        // PAE では CR3 は 32 バイト境界の PDPT を指す。
        Cr3Base = cpu.cr3 & (Pae ? 0xFFFFFFE0 : 0xFFFFF000);
        Flush();
    }

    public void Flush()
    {
        Array.Clear(TagR);
        Array.Clear(TagW);
    }

    // TLB を引く。ヒットなら物理アドレスを返す。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryLookup(uint linear, bool write, out uint physical)
    {
        var vpn = linear >> 12;
        var index = vpn & (TlbSize - 1);
        var tags = write ? TagW : TagR;
        if (tags[index] == (0x80000000u | vpn))
        {
            physical = (write ? PhysW : PhysR)[index] | (linear & 0xFFF);
            return true;
        }
        physical = 0;
        return false;
    }

    // ページウォークの結果(物理ページの基底)を TLB に入れ、物理アドレスを返す。
    public uint Fill(uint linear, bool write, uint physicalPage)
    {
        var vpn = linear >> 12;
        var index = vpn & (TlbSize - 1);
        if (write) { TagW[index] = 0x80000000u | vpn; PhysW[index] = physicalPage; }
        else { TagR[index] = 0x80000000u | vpn; PhysR[index] = physicalPage; }
        return physicalPage | (linear & 0xFFF);
    }
}
