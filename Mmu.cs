using System.Runtime.CompilerServices;

namespace Emu86;

// 線形→物理アドレス変換(ページング・TLB)と、物理メモリの読み書き(RAM・VGA ウィンドウ・ROM 窓・別名)。
static public class Mmu
{
    // 線形→物理アドレス変換(TLB を引き、外れたらページテーブルを歩く)。
    // リング0 のブートコードが対象のため、U/S の保護チェックは行わない(存在と、CR0.WP 時の R/W のみ)。
    static internal uint EnvTranslate(EmuEnvironment env, uint lin, bool write) =>
        env.Paging.TryLookup(lin, write, out var physical) ? physical : EnvWalkPageTable(env, lin, write);

    static uint EnvWalkPageTable(EmuEnvironment env, uint lin, bool write)
    {
        env.Paging.Walks++;
        return env.Paging.Fill(lin, write, EnvWalk(env, lin, write, env.Paging.Pae ? PaeLevels : LegacyLevels));
    }

    // ページテーブルの 1 段。Shift/IndexBits が線形アドレス中のインデックス、EntrySize がエントリ幅。
    // HasAccessBits は R/W と Accessed を持つ段(PAE の PDPT は持たない)、LargePageMask は PS ビットで
    // 大ページになれる段(PDE)の物理基底マスク。RAM は 4GB 未満なので 8 バイトエントリの上位 32 ビットは無視する。
    readonly record struct PageLevel(int Shift, int IndexBits, int EntrySize, bool HasAccessBits, uint LargePageMask)
    {
        public readonly uint IndexMask = (1u << IndexBits) - 1;
        public readonly int EntryShift = EntrySize == 8 ? 3 : 2;
        // エントリのアドレス(表の基底 + インデックス × エントリ幅)。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint EntryAddress(uint tableBase, uint lin) => tableBase + (((lin >> Shift) & IndexMask) << EntryShift);
    }

    // 非 PAE: PDE(4MB ページ可)→ PTE。PAE: PDPTE → PDE(2MB ページ可)→ PTE。
    static readonly PageLevel[] LegacyLevels = [new(22, 10, 4, true, 0xFFC00000), new(12, 10, 4, true, 0)];

    static readonly PageLevel[] PaeLevels = [new(30, 2, 8, false, 0), new(21, 9, 8, true, 0xFFE00000), new(12, 9, 8, true, 0)];

    // 段を順にたどる。不在ならその段でフォルト。大ページ(PS)か最終段に着いたら書き込み可否
    // (R/W は通った段の論理積)を検査し、Accessed/Dirty を立てて物理ページの基底を返す。
    // 途中の段(PDE)の Accessed は最終段の検査が通ってから立てる(Linux のページ管理が参照する)。
    static uint EnvWalk(EmuEnvironment env, uint lin, bool write, PageLevel[] levels, bool updateAccessBits = true)
    {
        var tableBase = env.Paging.Cr3Base;
        var writable = true;
        var pendingAccess = false; // Accessed を後から立てる途中段(PDE)
        uint pendingAddress = 0, pendingEntry = 0;
        for (var depth = 0; depth < levels.Length; depth++)
        {
            ref readonly var level = ref levels[depth];
            var address = level.EntryAddress(tableBase, lin);
            // 診断では RAM 外のページテーブルを「読めない」と扱う。実行用のオープンバス読み出しとは区別する。
            if (!updateAccessBits && (ulong)address + (uint)level.EntrySize > (uint)env.Ram.Length)
                throw new PageFaultException(lin, write ? 2u : 0u);
            var entry = RawRead32(env, address);
            if ((entry & 1) == 0)
                throw new PageFaultException(lin, write ? 2u : 0u);
            if (level.HasAccessBits) writable &= (entry & 2) != 0;
            var large = level.LargePageMask != 0 && (entry & 0x80) != 0;
            if (large || depth == levels.Length - 1)
            {
                var dirty = EnvCheckWrite(env, lin, write, writable);
                if (pendingAccess && updateAccessBits) RawWrite32(env, pendingAddress, pendingEntry | 0x20u);
                var updated = entry | 0x20u | dirty;
                if (updateAccessBits && (large || updated != entry)) RawWrite32(env, address, updated);
                return large ? (entry & level.LargePageMask) | (lin & ~level.LargePageMask & 0xFFFFF000) : entry & 0xFFFFF000;
            }
            if (level.HasAccessBits && (entry & 0x20) == 0)
            {
                pendingAccess = true;
                pendingAddress = address;
                pendingEntry = entry;
            }
            tableBase = entry & 0xFFFFF000;
        }
        throw new InvalidOperationException("page walk ended without a leaf");
    }

    // 書き込みが許されるか(CR0.WP=1 のときのみ R/W ビットを尊重する)を検査し、
    // 違反なら保護フォルトを投げる。許されるなら Accessed/Dirty 更新用のビットを返す。
    static uint EnvCheckWrite(EmuEnvironment env, uint lin, bool write, bool rw)
    {
        if (write && env.Paging.WriteProtect && !rw)
            throw new PageFaultException(lin, 0b011); // present + write + supervisor
        return write ? 0x40u : 0u;
    }

    // ページテーブル自体へのアクセスは物理アドレス直指定(変換しない)。
    static uint RawRead32(EmuEnvironment env, uint addr) =>
        addr + 4 <= (uint)env.Ram.Length ? BitConverter.ToUInt32(env.Ram, (int)addr) : 0xFFFFFFFF;

    static void RawWrite32(EmuEnvironment env, uint addr, uint v)
    {
        var m = env.Ram;
        if (addr + 4 <= (uint)m.Length)
        {
            m[addr] = (byte)v; m[addr + 1] = (byte)(v >> 8); m[addr + 2] = (byte)(v >> 16); m[addr + 3] = (byte)(v >> 24);
        }
    }

    // コードフェッチ用アドレス。CS の基底(記述子キャッシュ)+ IP/EIP。
    // 16ビットコードセグメント(code32=false)では IP(下位16ビット)を使う。
    static public MemAddr GetCodeAddr(CPU cpu) =>
        (true, cpu.cs_base + (cpu.code32 ? cpu.eip : cpu.ip));

    static internal uint EnvReadN(EmuEnvironment env, uint addr, int size) =>
        size == 1 ? EnvGetMemoryData8(env, addr) : size == 2 ? EnvGetMemoryData16(env, addr) : EnvGetMemoryData32(env, addr);

    static internal void EnvWriteN(EmuEnvironment env, uint addr, int size, uint val)
    {
        switch (size)
        {
            case 1: EnvWriteByte(env, addr, (byte)val); break;
            case 2: EnvWriteWord(env, addr, (ushort)val); break;
            default: EnvWriteDword(env, addr, val); break;
        }
    }

    // 搭載RAM(RamSize)外へのアクセスは未マップ領域として扱う:
    //   読み出しは 0xFF(オープンバス)、書き込みは無視。MMIO 等をエミュレートしない代わりに
    //   クラッシュを避ける。
    // アドレス引数は線形アドレス。ページング有効時はここで物理へ変換し、
    // 複数バイトのアクセスがページ境界を跨ぐ場合は 1 バイトずつ変換する。
    // BIOS ROM は実機同様に 4GB 上端(0xFFF00000-0xFFFFFFFF)にもエイリアスされ、先頭1MBの
    // 同じ内容を映す。SeaBIOS の 32bit flat コードは自身を高位エイリアス経由で call/jmp する
    // (例: 0xffff1edd = 低位 0xf1edd)。ページング無効時の物理アクセスでここへ折り返す。
    // ページング有効時は変換後の物理が先頭 RAM 内なので実質何もしない。
    static internal uint EnvAlias(uint addr) => addr >= 0xFFF00000 ? addr - 0xFFF00000 : addr;

    static internal void EnvWriteByte(EmuEnvironment env, uint addr, byte val)
    {
        var paging = env.Paging.Enabled;
        if (paging) addr = EnvTranslate(env, addr, write: true);
        addr = EnvAlias(addr);
        env.Diagnostics?.OnWriteByte(addr, val, paging);
        if (VgaDevice.Contains(addr)) { env.Vga.Write(addr, val); return; }
        if (addr < (uint)env.Ram.Length)
        {
            env.Translations.OnWrite(addr);
            env.Ram[addr] = val;
        }
        else if (EnvLfbOffset(env, addr, 1, out var offset)) env.Vga.WriteLfb(offset, val);
    }

    static internal void EnvWriteWord(EmuEnvironment env, uint addr, ushort value) =>
        EnvWriteValue(env, addr, value, 2);

    static internal void EnvWriteDword(EmuEnvironment env, uint addr, uint value) =>
        EnvWriteValue(env, addr, value, 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void EnvWriteValue(EmuEnvironment env, uint addr, uint value, int size)
    {
        var crosses = (addr & 0xFFF) > 0x1000 - size;
        if (crosses || env.Diagnostics is { WriteLog: not null })
        {
            // ページをまたぐ書き込みは両方のページを先に検査する(2 つ目が不在なら、実機と同じく一部だけ書かれた状態を残さずにフォルト。IR の Probe と同じ)。
            if (crosses && env.Paging.Enabled)
            {
                EnvTranslate(env, addr, write: true);
                EnvTranslate(env, (addr & ~0xFFFu) + 0x1000, write: true);
            }
            for (var byteIndex = 0; byteIndex < size; byteIndex++)
                EnvWriteByte(env, addr + (uint)byteIndex, (byte)(value >> (8 * byteIndex)));
            return;
        }
        var paging = env.Paging.Enabled;
        if (paging) addr = EnvTranslate(env, addr, write: true);
        addr = EnvAlias(addr);
        env.Diagnostics?.OnWrite(addr, size, paging);
        if (VgaDevice.Contains(addr))
        {
            // VGA ウィンドウはページ境界に揃っているので、ここに来る複数バイトは全部ウィンドウ内。
            for (var byteIndex = 0; byteIndex < size; byteIndex++)
                env.Vga.Write(addr + (uint)byteIndex, (byte)(value >> (8 * byteIndex)));
            return;
        }
        var memory = env.Ram;
        if ((ulong)addr + (uint)size <= (uint)memory.Length)
        {
            env.Translations.OnWrite(addr); // ページ境界はまたがない(先頭で分岐済み)
            memory[addr] = (byte)value;
            memory[addr + 1] = (byte)(value >> 8);
            if (size == 4)
            {
                memory[addr + 2] = (byte)(value >> 16);
                memory[addr + 3] = (byte)(value >> 24);
            }
        }
        else if (EnvLfbOffset(env, addr, size, out var offset))
        {
            // VGA の線形フレームバッファ(PCI BAR0): RAM と同じ並びで書く。
            var lfb = env.Vga.Lfb;
            lfb[offset] = (byte)value;
            lfb[offset + 1] = (byte)(value >> 8);
            if (size == 4)
            {
                lfb[offset + 2] = (byte)(value >> 16);
                lfb[offset + 3] = (byte)(value >> 24);
            }
            env.Vga.Dirty = true;
        }
        else
        {
            for (var byteIndex = 0; byteIndex < size; byteIndex++)
            {
                var physical = addr + (uint)byteIndex;
                if (physical < (uint)memory.Length)
                    memory[physical] = (byte)(value >> (8 * byteIndex));
                else if (EnvLfbOffset(env, physical, 1, out var lfbOffset))
                    env.Vga.WriteLfb(lfbOffset, (byte)(value >> (8 * byteIndex)));
            }
        }
    }

    static public byte EnvGetMemoryData8(EmuEnvironment env, uint addr)
    {
        if (env.Paging.Enabled) addr = EnvTranslate(env, addr, write: false);
        addr = EnvAlias(addr);
        if (VgaDevice.Contains(addr)) return env.Vga.Read(addr);
        return addr < (uint)env.Ram.Length ? env.Ram[addr] : EnvReadOutsideRam(env, addr);
    }

    // 診断専用の読み出し。TLB の充填・統計・ページテーブルの Accessed/Dirty・VGA ラッチを変更しない。
    // TLB に無いページも読み取り専用のウォークで調べる。不在ページは通常の読み出しと同じ #PF。
    internal static byte EnvPeekMemoryData8(EmuEnvironment env, uint addr)
    {
        if (env.Paging.Enabled)
            addr = env.Paging.TryLookup(addr, false, out var physical) ? physical
                : EnvWalk(env, addr, false, env.Paging.Pae ? PaeLevels : LegacyLevels, updateAccessBits: false) | (addr & 0xFFF);
        addr = EnvAlias(addr);
        if (VgaDevice.Contains(addr)) return env.Vga.Peek(addr);
        return addr < (uint)env.Ram.Length ? env.Ram[addr] : EnvReadOutsideRam(env, addr);
    }

    internal static uint EnvPeekMemoryData32(EmuEnvironment env, uint addr) =>
        (uint)(EnvPeekMemoryData8(env, addr) | (EnvPeekMemoryData8(env, unchecked(addr + 1)) << 8)
            | (EnvPeekMemoryData8(env, unchecked(addr + 2)) << 16) | (EnvPeekMemoryData8(env, unchecked(addr + 3)) << 24));

    // RAM 外の読み出し: VGA の線形フレームバッファ(PCI BAR0)、PCI の拡張 ROM 窓(VGA BIOS)の内容、それ以外はオープンバス 0xFF。
    static byte EnvReadOutsideRam(EmuEnvironment env, uint addr) =>
        EnvLfbOffset(env, addr, 1, out var offset) ? env.Vga.Lfb[offset]
        : env.Pci.RomContains(addr) ? env.Pci.RomRead(addr) : (byte)0xFF;

    // PCI の VGA BAR0(線形フレームバッファ)の中なら、その先頭からのオフセット(size バイト全部が収まる場合)。
    static bool EnvLfbOffset(EmuEnvironment env, uint addr, int size, out uint offset)
    {
        var lfb = env.Pci.LfbBase;
        offset = addr - lfb;
        return lfb != 0 && offset <= (uint)(VgaDevice.LfbSize - size);
    }

    static internal ushort EnvGetMemoryData16(EmuEnvironment env, uint addr)
    {
        if (env.Paging.Enabled)
        {
            if ((addr & 0xFFF) >= 0xFFF)
                return (ushort)(EnvGetMemoryData8(env, addr) | (EnvGetMemoryData8(env, addr + 1) << 8));
            addr = EnvTranslate(env, addr, write: false);
        }
        addr = EnvAlias(addr);
        if (VgaDevice.Contains(addr)) return (ushort)(env.Vga.Read(addr) | (env.Vga.Read(addr + 1) << 8));
        if (addr + 2 <= (uint)env.Ram.Length) return BitConverter.ToUInt16(env.Ram, (int)addr);
        if (EnvLfbOffset(env, addr, 2, out var offset)) return BitConverter.ToUInt16(env.Vga.Lfb, (int)offset);
        return (ushort)(EnvReadOutsideRam(env, addr) | (EnvReadOutsideRam(env, addr + 1) << 8));
    }

    static internal uint EnvGetMemoryData32(EmuEnvironment env, uint addr)
    {
        if (env.Paging.Enabled)
        {
            if ((addr & 0xFFF) > 0xFFC)
                return (uint)(EnvGetMemoryData8(env, addr)
                    | (EnvGetMemoryData8(env, addr + 1) << 8)
                    | (EnvGetMemoryData8(env, addr + 2) << 16)
                    | (EnvGetMemoryData8(env, addr + 3) << 24));
            addr = EnvTranslate(env, addr, write: false);
        }
        addr = EnvAlias(addr);
        if (VgaDevice.Contains(addr))
            return (uint)(env.Vga.Read(addr) | (env.Vga.Read(addr + 1) << 8) | (env.Vga.Read(addr + 2) << 16) | (env.Vga.Read(addr + 3) << 24));
        if (addr + 4 <= (uint)env.Ram.Length) return BitConverter.ToUInt32(env.Ram, (int)addr);
        if (EnvLfbOffset(env, addr, 4, out var offset)) return BitConverter.ToUInt32(env.Vga.Lfb, (int)offset);
        return (uint)(EnvReadOutsideRam(env, addr) | (EnvReadOutsideRam(env, addr + 1) << 8)
            | (EnvReadOutsideRam(env, addr + 2) << 16) | (EnvReadOutsideRam(env, addr + 3) << 24));
    }
}
