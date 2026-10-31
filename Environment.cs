using System.Runtime.CompilerServices;
using static Emu86.CPU;
using static Emu86.States;
using static Emu86.Mmu;
using static Emu86.Ports;
using static Emu86.DataAccess;
using static Emu86.StringOps;
using static Emu86.BitOps;
using static Emu86.Interrupts;
using static Emu86.Segments;
using static Emu86.Conversions;
using static Emu86.CpuStates;
using static Emu86.Fetch;
using static Emu86.AluStates;
using static Emu86.Decoder;

namespace Emu86;

// 線形→物理アドレス変換(ページング・TLB)と、物理メモリの読み書き(RAM・VGA ウィンドウ・ROM 窓・別名)。
static public class Mmu
{
    // 線形→物理アドレス変換(TLB を引き、外れたらページテーブルを歩く)。
    // リング0 のブートコードが対象のため、U/S の保護チェックは行わない(存在と、CR0.WP 時の R/W のみ)。
    static internal uint EnvTranslate(EmuEnvironment env, uint lin, bool write) =>
        env.Paging.TryLookup(lin, write, out var physical) ? physical : EnvWalkPageTable(env, lin, write);

    static uint EnvWalkPageTable(EmuEnvironment env, uint lin, bool write) =>
        env.Paging.Fill(lin, write, EnvWalk(env, lin, write, env.Paging.Pae ? PaeLevels : LegacyLevels));

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
    static uint EnvWalk(EmuEnvironment env, uint lin, bool write, PageLevel[] levels)
    {
        var tableBase = env.Paging.Cr3Base;
        var writable = true;
        var pendingAccess = false; // Accessed を後から立てる途中段(PDE)
        uint pendingAddress = 0, pendingEntry = 0;
        for (var depth = 0; depth < levels.Length; depth++)
        {
            ref readonly var level = ref levels[depth];
            var address = level.EntryAddress(tableBase, lin);
            var entry = RawRead32(env, address);
            if ((entry & 1) == 0)
                throw new PageFaultException(lin, write ? 2u : 0u);
            if (level.HasAccessBits) writable &= (entry & 2) != 0;
            var large = level.LargePageMask != 0 && (entry & 0x80) != 0;
            if (large || depth == levels.Length - 1)
            {
                var dirty = EnvCheckWrite(env, lin, write, writable);
                if (pendingAccess) RawWrite32(env, pendingAddress, pendingEntry | 0x20u);
                var updated = entry | 0x20u | dirty;
                if (large || updated != entry) RawWrite32(env, address, updated);
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
        addr + 4 <= (uint)env.OneMegaMemory_.Length ? BitConverter.ToUInt32(env.OneMegaMemory_, (int)addr) : 0xFFFFFFFF;

    static void RawWrite32(EmuEnvironment env, uint addr, uint v)
    {
        var m = env.OneMegaMemory_;
        if (addr + 4 <= (uint)m.Length)
        {
            m[addr] = (byte)v; m[addr + 1] = (byte)(v >> 8); m[addr + 2] = (byte)(v >> 16); m[addr + 3] = (byte)(v >> 24);
        }
    }

    // ArraySegment を使い、LINQ Skip の O(addr) 走査を避ける。
    // ページング有効時はページ境界を正しく跨ぐため 1 バイトずつ変換して読む遅延列挙に切り替える。
    // ページング無効時も BIOS 高位エイリアス(0xFFFxxxxx)を折り返し、RAM 外は 1 バイトずつ読む
    // (SeaBIOS の 32bit 初期化コードは 0xFFFF1xxx で動くため、ここを直さないとモナド版の ModRM デコードが失敗する)。
    static public IEnumerable<byte> EnvGetMemoryDatas(EmuEnvironment env, uint addr)
    {
        if (env.Paging.Enabled) return EnumerateLinear(env, addr);
        var physical = EnvAlias(addr);
        return physical < (uint)env.OneMegaMemory_.Length
            ? new ArraySegment<byte>(env.OneMegaMemory_, (int)physical, env.OneMegaMemory_.Length - (int)physical)
            : EnumerateLinear(env, addr);
    }

    static IEnumerable<byte> EnumerateLinear(EmuEnvironment env, uint addr) =>
        Enumerable.InfiniteSequence(addr, 1u).Select(linear => EnvGetMemoryData8(env, linear));

    static public IEnumerable<byte> EnvGetMemoryDatas(EmuEnvironment env, ushort segment, ushort offset) =>
        EnvGetMemoryDatas(env, GetMemoryAddr(segment, offset).addr);

    static public IEnumerable<byte> EnvGetMemoryDatas(EmuEnvironment env, ushort segment, ushort offset, int length) =>
        EnvGetMemoryDatas(env, segment, offset).Take(length);

    static public MemAddr GetMemoryAddr(ushort segment, ushort offset) =>
        (true, (uint)(0x10 * segment + offset));

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
        if (addr < (uint)env.OneMegaMemory_.Length)
        {
            env.Translations.OnWrite(addr);
            env.OneMegaMemory_[addr] = val;
        }
    }

    static internal void EnvWriteWord(EmuEnvironment env, uint addr, ushort value) =>
        EnvWriteValue(env, addr, value, 2);

    static internal void EnvWriteDword(EmuEnvironment env, uint addr, uint value) =>
        EnvWriteValue(env, addr, value, 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void EnvWriteValue(EmuEnvironment env, uint addr, uint value, int size)
    {
        if ((addr & 0xFFF) > 0x1000 - size || env.Diagnostics is { WriteLog: not null })
        {
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
        var memory = env.OneMegaMemory_;
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
        else
        {
            for (var byteIndex = 0; byteIndex < size; byteIndex++)
            {
                var physical = addr + (uint)byteIndex;
                if (physical < (uint)memory.Length)
                    memory[physical] = (byte)(value >> (8 * byteIndex));
            }
        }
    }

    static public byte EnvGetMemoryData8(EmuEnvironment env, uint addr)
    {
        if (env.Paging.Enabled) addr = EnvTranslate(env, addr, write: false);
        addr = EnvAlias(addr);
        if (VgaDevice.Contains(addr)) return env.Vga.Read(addr);
        return addr < (uint)env.OneMegaMemory_.Length ? env.OneMegaMemory_[addr] : EnvReadOutsideRam(env, addr);
    }

    // RAM 外の読み出し: PCI の拡張 ROM 窓(VGA BIOS)なら ROM の内容、それ以外はオープンバス 0xFF。
    static byte EnvReadOutsideRam(EmuEnvironment env, uint addr) =>
        env.Pci.RomContains(addr) ? env.Pci.RomRead(addr) : (byte)0xFF;

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
        return addr + 2 <= (uint)env.OneMegaMemory_.Length ? BitConverter.ToUInt16(env.OneMegaMemory_, (int)addr)
            : (ushort)(EnvReadOutsideRam(env, addr) | (EnvReadOutsideRam(env, addr + 1) << 8));
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
        return addr + 4 <= (uint)env.OneMegaMemory_.Length ? BitConverter.ToUInt32(env.OneMegaMemory_, (int)addr)
            : (uint)(EnvReadOutsideRam(env, addr) | (EnvReadOutsideRam(env, addr + 1) << 8)
                | (EnvReadOutsideRam(env, addr + 2) << 16) | (EnvReadOutsideRam(env, addr + 3) << 24));
    }
}

// I/O ポートの入出力(PortMap の表を引く)。
static public class Ports
{
    // ポートの振り分けは PortMap の表(ポート範囲 → デバイスの読み書き)。該当する項がなければ IoPort 配列を素通しする。
    static public byte EnvInPort(EmuEnvironment env, int port)
    {
        port &= 0xFFFF;
        foreach (var mapping in PortMap.ByPort[port])
            if (mapping.Read != null && mapping.Applies(env, port)) return mapping.Read(env, port);
        return env.IoPort[port];
    }

    static public void EnvOutPort(EmuEnvironment env, int port, byte val)
    {
        port &= 0xFFFF;
        foreach (var mapping in PortMap.ByPort[port])
            if (mapping.Write != null && mapping.Applies(env, port)) { mapping.Write(env, port, val); return; }
        env.IoPort[port] = val;
    }

    // 幅付きポート入出力: 幅付きを自分で扱う項(ATA データ、PCI コンフィグ、ACPI の dword など)があればそれに任せ、
    // なければ 1 バイトずつ(隣接ポートへ波及する)。
    static public uint EnvInPortN(EmuEnvironment env, int port, int size)
    {
        var p = port & 0xFFFF;
        foreach (var mapping in PortMap.ByPort[p])
            if (mapping.ReadWide != null && mapping.Applies(env, p)) return mapping.ReadWide(env, p, size);
        uint v = 0;
        for (int i = 0; i < size; i++)
            v |= (uint)EnvInPort(env, port + i) << (8 * i);
        return v;
    }

    static public void EnvOutPortN(EmuEnvironment env, int port, int size, uint val)
    {
        var p = port & 0xFFFF;
        foreach (var mapping in PortMap.ByPort[p])
            if (mapping.WriteWide != null && mapping.Applies(env, p)) { mapping.WriteWide(env, p, size, val); return; }
        for (int i = 0; i < size; i++)
            EnvOutPort(env, port + i, (byte)(val >> (8 * i)));
    }
}

// State で書いた汎用レジスタ・セグメントレジスタ・制御レジスタ・スタック・r/m オペランド・moffs の読み書き。
static public class DataAccess
{
    static private readonly Accessor<CPU, byte>[] ArrayReg8 =
    [
        _al, _cl, _dl, _bl,
        _ah, _ch, _dh, _bh
    ];

    static private readonly Accessor<CPU, ushort>[] ArrayReg16 =
    [
        _ax, _cx, _dx, _bx,
        _sp, _bp, _si, _di
    ];

    static private readonly Accessor<CPU, uint>[] ArrayReg32 =
    [
        _eax, _ecx, _edx, _ebx,
        _esp, _ebp, _esi, _edi
    ];

    static private readonly Accessor<CPU, ushort>[] ArraySreg =
    [
        _es, _cs, _ss, _ds, _fs, _gs
    ];

    static internal readonly Accessor<CPU, uint>[] ArraySregBase =
    [
        _es_base, _cs_base, _ss_base, _ds_base, _fs_base, _gs_base
    ];

    static public readonly Func<byte, Func<int, Func<CPU, CPU>>> EnvSetRegData8 = EnvSetDataFromCPU(ArrayReg8);

    static public readonly Func<ushort, Func<int, Func<CPU, CPU>>> EnvSetRegData16 = EnvSetDataFromCPU(ArrayReg16);

    static public readonly Func<uint, Func<int, Func<CPU, CPU>>> EnvSetRegData32 = EnvSetDataFromCPU(ArrayReg32);

    static public readonly Func<ushort, Func<int, Func<CPU, CPU>>> EnvSetSReg3 = EnvSetDataFromCPU(ArraySreg);

    static public State<Unit> SetRegData8(int reg, byte db) =>
        SetCpu(EnvSetRegData8(db)(reg));

    static public State<Unit> SetRegData16(int reg, ushort dw) =>
        SetCpu(EnvSetRegData16(dw)(reg));

    static public State<Unit> SetRegData32(int reg, uint dd) =>
        SetCpu(EnvSetRegData32(dd)(reg));

    // CR 書き込み後はページング状態を env へ同期する(PG 切替・CR3 変更で TLB もフラッシュ)。
    static public State<Unit> SetCrReg(int reg, uint data) =>
        from _1 in GetControlRegister(reg).Set(data)
        from _2 in Effect((env, cpu) => env.Paging.Sync(cpu))
        select Unit.unit;

    static public State<Unit> SetRegData(int reg, Data data) =>
        SetCpu(cpu => SetRegisterValue(cpu, reg, data));

    static public State<ushort> GetRegData16(int reg) =>
        GetDataFromCpu(ArrayReg16[reg].getter);

    static public State<uint> GetRegData32(int reg) =>
        GetDataFromCpu(ArrayReg32[reg].getter);

    // スタックトップの物理アドレス。SS の基底 + SP/ESP。
    // 32ビットスタック(SS.B=1)なら ESP、そうでなければ SP を使う(CS.D ではなく SS.B で決まる)。
    static private MemAddr EnvGetStackAddr(CPU cpu) =>
        (true, cpu.ss_base + (cpu.stack32 ? cpu.esp : cpu.sp));

    // SP/ESP を delta だけ動かす(32 ビットスタックなら ESP、そうでなければ SP)。PUSH/POP/RET n/RETF n が使う。
    static public State<Unit> StackAdjust(int delta) =>
        SetCpu(cpu =>
        {
            if (cpu.stack32) cpu.esp = (uint)(cpu.esp + delta);
            else cpu.sp = (ushort)(cpu.sp + delta);
            return cpu;
        });

    // SP/ESP をデータ長分減らしてからスタックトップへ書き込む（PUSH）。
    static public State<Unit> Push(Data data) =>
        from _1 in StackAdjust(-Bytes(data.type))
        from cpu in GetCpu
        from _2 in SetMemOrRegData(EnvGetStackAddr(cpu), data)
        select Unit.unit;

    // スタックトップから type 長を読み出してから SP/ESP を増やす（POP）。
    static public State<Data> Pop(int type) =>
        from cpu in GetCpu
        let addr = EnvGetStackAddr(cpu)
        from data in Choice(
            type,
            GetMemOrRegData8(addr),
            GetMemOrRegData16(addr),
            GetMemOrRegData32(addr)
        )
        from _ in StackAdjust(Bytes(type))
        select data;

    static public State<Unit> Push16(ushort value) =>
        Push(value.ToTypeData());

    static public State<ushort> Pop16 =>
        Pop(1).Select(data => data.dw);

    // ENTER imm16, level: スタックフレームを構築する。
    //   push BP; frame=SP; level 回ネストリンクをコピー; BP=frame; SP-=alloc。
    static public State<Unit> Enter(ushort alloc, int level) =>
        from bp0 in GetRegData16(5)
        from _p in Push16(bp0)
        from frame in GetRegData16(4)
        from _n in EnterNesting(frame, level & 0x1F)
        from _b in _bp.Set(frame)
        from _s in _sp.Modify(sp => (ushort)(sp - alloc))
        select Unit.unit;

    // ネストレベル>0 のとき、外側フレームのリンクを順に push し、最後に frame を push する。
    static State<Unit> EnterNesting(ushort frame, int level) =>
        Enumerable.Range(1, level)
            .Select(i => i < level
                ? from _r in _bp.Modify(bp => (ushort)(bp - 2))
                  from v in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData16(env, cpu.ss_base + cpu.bp))
                  from _p in Push16(v)
                  select Unit.unit
                : Push16(frame))
            .Sequence()
            .Ignore();

    // XLAT: AL <- [DS:BX + AL]（バイト変換テーブル参照）。
    static public State<Unit> Xlat =>
        SetCpu((env, cpu) =>
        {
            var addr = cpu.ds_base + (ushort)(cpu.bx + cpu.al);
            return _al.setter(cpu)(EnvGetMemoryData8(env, addr));
        });

    // moffs の物理アドレス。既定 DS(セグメントオーバーライドで置換)+ オフセット。
    static uint EnvMoffsAddr(CPU cpu, uint offset) =>
        EnvSegBases(cpu).seg + offset;

    // MOV AL/AX/EAX, [moffs]: 直接アドレス DS:offset から読み、アキュムレータへ書く。
    static public State<Unit> MovAccFromMoffs(int type, uint offset) =>
        SetCpu((env, cpu) =>
        {
            var addr = EnvMoffsAddr(cpu, offset);
            return type switch
            {
                0 => _al.setter(cpu)(EnvGetMemoryData8(env, addr)),
                1 => _ax.setter(cpu)(EnvGetMemoryData16(env, addr)),
                _ => _eax.setter(cpu)(EnvGetMemoryData32(env, addr)),
            };
        });

    // MOV [moffs], AL/AX/EAX: アキュムレータを直接アドレス DS:offset へ書く。
    static public State<Unit> MovMoffsFromAcc(int type, uint offset) =>
        Effect((env, cpu) => EnvWriteN(env, EnvMoffsAddr(cpu, offset), Bits(type) / 8, cpu.eax));

    // セグメントレジスタ(ES/CS/SS/DS/FS/GS)を reg 番号で読み出す。
    static public State<ushort> GetSRegData(int reg) =>
        GetDataFromCpu(ArraySreg[reg].getter);

    static public State<Data> GetRegData(int reg, int type) =>
        GetDataFromCpu(GetTypeData_(
            type,
            ArrayReg8[reg].getter,
            ArrayReg16[reg].getter,
            ArrayReg32[reg].getter
            )
        );

    static public State<uint> GetCrReg(int reg) =>
        GetControlRegister(reg).Get();

    static Accessor<CPU, uint> GetControlRegister(int register) => register switch
    {
        0 => _cr0,
        2 => _cr2,
        3 => _cr3,
        4 => _cr4,
        _ => throw new KeyNotFoundException()
    };

    // データアクセスのセグメントベース(記述子キャッシュから)。
    // seg: 既定 DS(オーバーライドプレフィックスで置換)
    // ss : BP/EBP/ESP ベースの既定 SS(こちらもオーバーライドで置換される)
    static internal (uint seg, uint ss) EnvSegBases(CPU cpu)
    {
        var ovr = OverrideBase(cpu.Decode.Prefixes, cpu);
        return (ovr ?? cpu.ds_base, ovr ?? cpu.ss_base);
    }

    static public State<byte> GetMemOrRegData8(MemAddr t) =>
        GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData8(env, t.addr) : ArrayReg8[(int)t.addr].getter(cpu));

    static public State<ushort> GetMemOrRegData16(MemAddr t) =>
        GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData16(env, t.addr) : ArrayReg16[(int)t.addr].getter(cpu));

    static public State<uint> GetMemOrRegData32(MemAddr t) =>
        GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData32(env, t.addr) : ArrayReg32[(int)t.addr].getter(cpu));

    static CPU SetRegisterValue(CPU cpu, int register, Data data) => data.type switch
    {
        0 => ArrayReg8[register].setter(cpu)(data.db),
        1 => ArrayReg16[register].setter(cpu)(data.dw),
        2 => ArrayReg32[register].setter(cpu)(data.dd),
        _ => throw new IndexOutOfRangeException()
    };

    static public State<Unit> SetMemOrRegData(
        MemAddr t,
        Data data) =>
        SetCpu((env, cpu) =>
        {
            if (t.isMem)
            {
                EnvWriteN(env, t.addr, Bytes(data.type), data.Value);
                return cpu;
            }
            return SetRegisterValue(cpu, (int)t.addr, data);
        });

    // 実効オペランドサイズ(type)。32ビットコードセグメントではデフォルトが32ビットになり、
    // operand_size_prefix(0x66)は意味が反転する。
    static public State<int> OperandType(bool w) =>
        GetDataFromCpu(cpu => w ? ((cpu.code32 != cpu.operand_size_prefix) ? 2 : 1) : 0);

    static public State<Data> GetMemOrRegData(
        MemAddr t,
        bool w) =>
        from type in OperandType(w)
        from ret in Choice(
            type,
            GetMemOrRegData8(t),
            GetMemOrRegData16(t),
            GetMemOrRegData32(t)
        )
        select ret;

    static public State<(Data data, MemAddr input)> GetMemOrRegData(
        ushort segment,
        ushort offset,
        bool w) =>
        from addr in GetMemoryAddr(segment, offset).ToState()
        from data in GetMemOrRegData(addr, w)
        select (data, addr);

    static public State<(Data data, MemAddr input)> GetMemOrRegData(
        int mod,
        int rm,
        bool w) =>
        from addr in GetMemOrRegAddr(mod, rm)
        from data in GetMemOrRegData(addr, w)
        select (data, addr);

    static private Func<T, Func<int, Func<CPU, CPU>>> EnvSetDataFromCPU<T>(Accessor<CPU, T>[] array) =>
        data => reg => cpu => array[reg].setter(cpu)(data);
}

// 文字列命令(MOVS/STOS/LODS/CMPS/SCAS/INS/OUTS)の 1 要素分。
static public class StringOps
{
    // 文字列命令の実効パラメータ。
    //   要素の幅(type)      : operand size に従う(w かつ 32bit なら dword、そうでなければ word/byte)。
    //   アドレスモード     : code32 でフラット32bit(ESI/EDI, ベース0)、そうでなければ real(DS:SI/ES:DI)。
    //   源(DS:SI)はセグメントオーバーライドで差し替えられる。先(ES:DI)は固定。
    static int StrType(CPU cpu, bool w) => w ? ((cpu.code32 != cpu.operand_size_prefix) ? 2 : 1) : 0;

    static int StrSize(CPU cpu, bool w) => Bytes(StrType(cpu, w));

    // AL/AX/EAX(type の幅のアキュムレータ)。
    static uint StrAcc(CPU cpu, int type) => cpu.eax & Mask(type);

    static bool StrA32(CPU cpu) => cpu.code32 != cpu.address_size_prefix;

    static uint StrSrc(CPU cpu) => (OverrideBase(cpu.Decode.Prefixes, cpu) ?? cpu.ds_base) + (StrA32(cpu) ? cpu.esi : cpu.si);

    static uint StrDst(CPU cpu) => cpu.es_base + (StrA32(cpu) ? cpu.edi : cpu.di);

    // DF に従って SI/DI(32bit なら ESI/EDI)を size 分だけ増減する。
    static CPU StrAdvance(CPU cpu, int size, bool si, bool di)
    {
        int delta = cpu.df ? -size : size;
        bool a32 = StrA32(cpu);
        if (si) cpu = a32 ? _esi.setter(cpu)((uint)(cpu.esi + delta)) : _si.setter(cpu)((ushort)(cpu.si + delta));
        if (di) cpu = a32 ? _edi.setter(cpu)((uint)(cpu.edi + delta)) : _di.setter(cpu)((ushort)(cpu.di + delta));
        return cpu;
    }

    // MOVS: [DS:SI] -> [ES:DI] を 1 要素コピーし、DF に従って SI/DI を増減する。
    static public State<Unit> Movs(bool w) =>
        SetCpu((env, cpu) =>
        {
            int size = StrSize(cpu, w);
            EnvWriteN(env, StrDst(cpu), size, EnvReadN(env, StrSrc(cpu), size));
            return StrAdvance(cpu, size, si: true, di: true);
        });

    // STOS: AL/AX/EAX -> [ES:DI]、DF に従って DI を増減する。
    static public State<Unit> Stos(bool w) =>
        SetCpu((env, cpu) =>
        {
            int type = StrType(cpu, w), size = Bytes(type);
            EnvWriteN(env, StrDst(cpu), size, StrAcc(cpu, type));
            return StrAdvance(cpu, size, si: false, di: true);
        });

    // LODS: [DS:SI] -> AL/AX/EAX、DF に従って SI を増減する。
    static public State<Unit> Lods(bool w) =>
        SetCpu((env, cpu) =>
        {
            int size = StrSize(cpu, w);
            uint val = EnvReadN(env, StrSrc(cpu), size);
            cpu = size == 1 ? _al.setter(cpu)((byte)val) : size == 2 ? _ax.setter(cpu)((ushort)val) : _eax.setter(cpu)(val);
            return StrAdvance(cpu, size, si: true, di: false);
        });

    // CMPS: [DS:SI] - [ES:DI] でフラグを更新（結果は破棄）し、SI/DI を増減する。
    static public State<Unit> Cmps(bool w) =>
        from vals in GetDataFromEnvCpu((env, cpu) =>
        {
            int type = StrType(cpu, w), size = Bytes(type);
            return (type, s: EnvReadN(env, StrSrc(cpu), size), d: EnvReadN(env, StrDst(cpu), size));
        })
        from _f in update_eflags_sub(vals.type, vals.s, vals.d)
        from _adv in SetCpu(cpu => StrAdvance(cpu, Bytes(vals.type), si: true, di: true))
        select Unit.unit;

    // INS: ポート DX から 1 要素読み、[ES:DI] へ格納し、DF に従って DI を増減する。
    static public State<Unit> Ins(bool w) =>
        SetCpu((env, cpu) =>
        {
            int size = StrSize(cpu, w);
            EnvWriteN(env, StrDst(cpu), size, EnvInPortN(env, cpu.dx, size));
            return StrAdvance(cpu, size, si: false, di: true);
        });

    // OUTS: [DS:SI] から 1 要素読み、ポート DX へ出力し、DF に従って SI を増減する。
    static public State<Unit> Outs(bool w) =>
        SetCpu((env, cpu) =>
        {
            int size = StrSize(cpu, w);
            EnvOutPortN(env, cpu.dx, size, EnvReadN(env, StrSrc(cpu), size));
            return StrAdvance(cpu, size, si: true, di: false);
        });

    // SCAS: AL/AX - [ES:DI] でフラグを更新（結果は破棄）し、DI を増減する。
    static public State<Unit> Scas(bool w) =>
        from vals in GetDataFromEnvCpu((env, cpu) =>
        {
            int type = StrType(cpu, w);
            return (type, a: StrAcc(cpu, type), m: EnvReadN(env, StrDst(cpu), Bytes(type)));
        })
        from _f in update_eflags_sub(vals.type, vals.a, vals.m)
        from _adv in SetCpu(cpu => StrAdvance(cpu, Bytes(vals.type), si: false, di: true))
        select Unit.unit;
}

// ビット操作命令(BT 系、BSF/BSR)。
static public class BitOps
{
    // BT系: r/m の bit 番目を CF にコピーし、op に応じて値を変更して書き戻す。
    //   op: 0=BT(変更なし) 1=BTS(セット) 2=BTR(クリア) 3=BTC(反転)
    static public State<Unit> BitTest(MemAddr addr, Data data, int bit, int op) =>
        from _f in _cf.Set(((data.type == 1 ? data.dw : data.dd) & (1u << (bit & (data.type == 1 ? 15 : 31)))) != 0)
        from _w in Unless(op == 0, SetMemOrRegData(addr, BitModify(data, bit, op)))
        select Unit.unit;

    static Data BitModify(Data data, int bit, int op)
    {
        var bits = data.type == 1 ? 16 : 32;
        var mask = 1u << (bit & (bits - 1));
        var v = data.type == 1 ? (uint)data.dw : data.dd;
        v = op switch
        {
            1 => v | mask,    // BTS
            2 => v & ~mask,   // BTR
            3 => v ^ mask,    // BTC
            _ => v,
        };
        return data.type == 1 ? ((ushort)v).ToTypeData() : v.ToTypeData();
    }

    // BSF/BSR: r/m 内のセットビット位置を求めて reg へ書く(実効オペランドサイズに従う)。
    //   forward=true で最下位(BSF)、false で最上位(BSR)。
    //   ソースが 0 なら ZF=1 とし、結果は書き込まない（未定義）。
    static public State<Unit> BitScan(int reg, Data src, bool forward) =>
        src.Value == 0
            ? _zf.Set(true)
            : from _z in _zf.Set(false)
              from _w in SetRegData(reg, ((uint)BitScanIndex(src.Value, forward, Bits(src.type))).ToTypeData(src.type))
              select Unit.unit;

    static int BitScanIndex(uint src, bool forward, int bits)
    {
        if (forward)
        {
            for (int i = 0; i < bits; i++)
                if ((src & (1u << i)) != 0) return i;
        }
        else
        {
            for (int i = bits - 1; i >= 0; i--)
                if ((src & (1u << i)) != 0) return i;
        }
        return 0;
    }
}

// 割り込み・例外の配送(リアル/V86/プロテクトモード、スタック切り替え)と IRET。
static public class Interrupts
{
    // INT vector: モードに応じて配送する。
    //   リアルモード      : IVT(物理 vector*4)から CS:IP。FLAGS/CS/IP を 16 ビットで push。
    //   プロテクトモード  : IDT ゲート(idt_base + vector*8)から CS:EIP。
    //                       EFLAGS/CS/EIP を 32 ビットで push(リング遷移なし = ring0 のみ対応)。
    static public State<Unit> Interrupt(int vector) =>
        from cpu0 in GetCpu
        from _ in cpu0.pe ? InterruptProtected(vector, hasError: false, 0) : InterruptReal(vector)
        select Unit.unit;

    // V86 モードの INT n: IOPL<3 なら #GP(0)。IOPL=3 でも IDT ゲートの DPL が 3 未満なら
    // #GP(vector*8+2) になる(NT はこれを KiTrap0D で受けて IVT 経由の反射をソフトウェアで行う)。
    static public State<Unit> InterruptFromV86(int vector) =>
        from gateHi in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData32(env, cpu.idt_base + (uint)vector * 8 + 4))
        from cpu in GetCpu
        let iopl = (cpu.eflags >> 12) & 3
        let dpl = (gateHi >> 13) & 3
        from _ in iopl < 3 ? GeneralProtection(0)
                : dpl < 3 ? GeneralProtection(((uint)vector << 3) | 2)
                : Interrupt(vector)
        select Unit.unit;

    // #GP を起こす。フォルトなので命令は完了せず、InstructionExecutor が命令開始時の CPU へ戻し、
    // ランナーがそこから Raise で配送する(#PF と同じ経路)。
    static public State<Unit> GeneralProtection(uint errorCode) =>
        (env, cpu, ope) => throw new GeneralProtectionException(errorCode);

    // フォルトの配送(命令開始時の状態から)。#PF は CR2 にフォルトアドレスを入れ、
    // エラーコードを持つ例外はそれを push する。
    static public State<Unit> Raise(Fault fault) =>
        from _1 in fault.Linear is uint linear ? _cr2.Set(linear) : Pass
        from _2 in InterruptProtected(fault.Vector, hasError: fault.ErrorCode is not null, fault.ErrorCode ?? 0)
        select Unit.unit;

    // リアルモードの INT 19h(SeaBIOS の POST 完了後のブート)で VGA BIOS スタブを導入する
    // (SeaBIOS はレガシー ROM 領域を走査しないため、この時点で IVT 10h を差し替える)。
    static State<Unit> InterruptReal(int vector) =>
        from _stub in Effect((env, _) => { if (vector == 0x19) VgaBiosStub.Install(env); })
        from fl in GetDataFromCpu(cpu => (ushort)cpu.eflags)
        from _1 in Push16(fl)
        from cs in GetSRegData(1)
        from _2 in Push16(cs)
        from ip in GetDataFromCpu(cpu => cpu.ip)
        from _3 in Push16(ip)
        from _if in SetCpu(cpu => { cpu.jf = false; cpu.tf = false; return cpu; })
        from newip in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData16(env, (uint)(vector * 4)))
        from newcs in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData16(env, (uint)(vector * 4 + 2)))
        from _4 in _ip.Set(newip)
        from _5 in LoadSReg(1, newcs)
        select Unit.unit;

    // IDT ゲート経由の配送。idt_base は線形アドレスだが、この関数はページング変換の
    // 起点(#PF ハンドラ自身)になりうるため、IDT は恒等マップ域にある前提で読む。
    static State<Unit> InterruptProtected(int vector, bool hasError, uint errorCode) =>
        from _log in GetDataFromEnvCpu((env, cpu) =>
        {
            if (env.Diagnostics is { InterruptLog: not null } diagnostics && cpu.pg)
                diagnostics.LogInterrupt($"vec={vector:x2} err={(hasError ? errorCode.ToString("x") : "-")} at cs:eip={cpu.cs:x4}:{cpu.eip:x8} esp={cpu.esp:x8}",
                    fromV86: cpu.vm, env.Tsc, cpu.cr3);
            return Unit.unit;
        })
        from gateLo in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData32(env, cpu.idt_base + (uint)vector * 8))
        from gateHi in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData32(env, cpu.idt_base + (uint)vector * 8 + 4))
        let offset = (gateHi & 0xFFFF0000) | (gateLo & 0xFFFF)
        let sel = (ushort)(gateLo >> 16)
        let gateType = (int)(gateHi >> 8) & 0xF
        // 特権レベルが上がる(例: ring3 のユーザーモードから ring0 へ)ときは TSS の SS0:ESP0 へ
        // スタックを切り替え、旧 SS/ESP を先に積む(IRET で元へ戻る)。
        // V86 モードからの配送は VM を先に落として(以降のセグメントロードを記述子経由にする)、
        // 積む EFLAGS には VM=1 の元の値を使う。
        from cpu0 in GetCpu
        let fromVm = cpu0.vm
        let cpl = cpu0.cpl // VM を落とす前に評価する(V86 なら 3)
        let fl = cpu0.eflags
        from _vm in When(fromVm, SetCpu(cpu => { cpu.eflags &= ~0x20000u; return cpu; }))
        from _sw in SwitchToInnerStack(sel, cpl, fromVm)
        from _1 in Push(fl.ToTypeData())
        from cs in GetSRegData(1)
        from _2 in Push(((uint)cs).ToTypeData())
        from eip0 in GetDataFromCpu(cpu => cpu.eip)
        from _3 in Push(eip0.ToTypeData())
        // 一部の例外(#PF 等)は EIP の後ろにエラーコードを push する。
        from _e in When(hasError, Push(errorCode.ToTypeData()))
        // 割り込みゲート(型 0xE)は IF をクリアする。トラップゲート(0xF)は保つ。
        from _if in SetCpu(cpu => { if (gateType == 0xE) cpu.jf = false; cpu.tf = false; return cpu; })
        from _4 in _eip.Set(offset)
        from _5 in LoadSReg(1, sel)
        select Unit.unit;

    // 割り込み/例外配送での特権レベル変化: CPL より目標 CS の RPL が小さければ
    // TR が指す TSS から SS0/ESP0 を読んでスタックを切り替え、旧 SS と ESP を積む。
    // V86 モードから(fromVm)は、さらにその前に GS/FS/DS/ES を積み、これらを null にする
    // (ハンドラ側は KiTrap の冒頭でカーネル用セレクタをロードし直す)。
    static State<Unit> SwitchToInnerStack(ushort targetCs, int cpl, bool fromVm) =>
        When(cpl > (targetCs & 3),
            from ctx in GetDataFromEnvCpu((env, cpu) =>
                {
                    var (tssBase, _) = EnvReadDescriptor(env, cpu, cpu.tr);
                    var esp0 = EnvGetMemoryData32(env, tssBase + 4);
                    var ss0 = (ushort)EnvGetMemoryData32(env, tssBase + 8);
                    return (oldSs: cpu.ss, oldEsp: cpu.esp, ss0, esp0, cpu.es, cpu.ds, cpu.fs, cpu.gs);
                })
            from _1 in LoadSReg(2, ctx.ss0)
            from _2 in _esp.Set(ctx.esp0)
            from _v in When(fromVm,
                from a in Push(((uint)ctx.gs).ToTypeData())
                from b in Push(((uint)ctx.fs).ToTypeData())
                from c in Push(((uint)ctx.ds).ToTypeData())
                from d in Push(((uint)ctx.es).ToTypeData())
                select Unit.unit)
            from _3 in Push(((uint)ctx.oldSs).ToTypeData())
            from _4 in Push(ctx.oldEsp.ToTypeData())
            from _n in When(fromVm, SetCpu(cpu =>
                {
                    foreach (var reg in new[] { 0, 3, 4, 5 })
                        cpu = EnvSetSReg3(0)(reg)(cpu);
                    return cpu;
                }))
            select Unit.unit);

    // 外側の特権レベル(例: ring0 → ring3)へ戻るときは、さらに ESP と SS を pop してスタックを切り替える。
    // V86 モード内の far 復帰はリアルモード同様で、セレクタの下位ビットは特権を意味しない。
    static public State<Unit> ReturnToOuterStack(ushort targetCs) =>
        from cpu0 in GetCpu
        from _ in When(!cpu0.vm && (targetCs & 3) > (cpu0.cs & 3),
            from sp in Pop(2)
            from ss in Pop(2)
            from _1 in LoadSReg(2, (ushort)ss.dd)
            from _2 in _esp.Set(sp.dd)
            select Unit.unit)
        select Unit.unit;

    // IRET: モードに応じて復帰する(リアル/16bit と プロテクト/32bit)。
    static public State<Unit> Iret =>
        from cpu0 in GetCpu
        from _ in cpu0.pe && cpu0.code32 ? Iret32 : Iret16
        select Unit.unit;

    // 16 ビット IRET。V86 モード(IOPL=3 で実行可)では IOPL を変更できない(VM は上位ワードなので保たれる)。
    static State<Unit> Iret16 =>
        from ip in Pop16
        from _1 in _ip.Set(ip)
        from cs in Pop16
        from _2 in LoadSReg(1, cs)
        from fl in Pop16
        from _3 in SetCpu(cpu =>
        {
            var keep = cpu.vm ? 0x3000u : 0u;
            cpu.eflags = (cpu.eflags & (0xFFFF0000 | keep)) | (fl & ~keep);
            return cpu;
        })
        select Unit.unit;

    static State<Unit> Iret32 =>
        from fault in GetDataFromEnvCpu(IretTargetFault)
        from _ in fault is uint selector
            ? GeneralProtection(selector)
            : (from eip in Pop(2)
               from cs in Pop(2)
               from fl in Pop(2)
               from cpu0 in GetCpu
               from _v in (fl.dd & 0x20000) != 0 && cpu0.cpl == 0
                   ? IretToV86(eip.dd, (ushort)cs.dd, fl.dd)
                   : (from _0 in ReturnToOuterStack((ushort)cs.dd) // 旧 SS で ESP/SS を pop してから CS をロードする
                      from _1 in _eip.Set(eip.dd)
                      from _2 in LoadSReg(1, (ushort)cs.dd)
                      from _3 in SetCpu(cpu => { cpu.eflags = fl.dd; return cpu; })
                      select Unit.unit)
               select Unit.unit)
        select Unit.unit;

    // 32 ビット IRET の復帰先 CS をスタックを動かさずに検査し、違反なら #GP のエラーコード(セレクタ)を返す。
    // 非適合コードセグメントは DPL=RPL、適合コードは DPL<=RPL が必要で、RPL<CPL も不可。V86 への復帰は対象外。
    // NT はこれを意図的に使う: V86 の BIOS 呼び出しから戻る高速 BOP 経路は CS=000Bh(ring0 コードに RPL 3)の
    // IRET で #GP を起こし、KiTrap0D がその枠を認識して Ki386BiosCallReturnAddress へ ring0 のまま飛ぶ。
    static uint? IretTargetFault(EmuEnvironment env, CPU cpu)
    {
        if (!cpu.pe || cpu.vm) return null;
        var stack = cpu.ss_base + (cpu.stack32 ? cpu.esp : cpu.sp);
        var cs = (ushort)EnvGetMemoryData32(env, stack + 4);
        var flags = EnvGetMemoryData32(env, stack + 8);
        if ((flags & 0x20000) != 0 && cpu.cpl == 0) return null;
        if ((cs & 0xFFFC) == 0) return 0;
        var hi = EnvGetMemoryData32(env, cpu.gdt_base + (uint)(cs & 0xFFF8) + 4);
        var type = (int)(hi >> 8) & 0x1F; // bit4=S, bit3=code, bit2=conforming
        var dpl = (int)(hi >> 13) & 3;
        var rpl = cs & 3;
        if ((type & 0x18) != 0x18 || rpl < cpu.cpl) return (uint)(cs & 0xFFFC);
        var conforming = (type & 0x04) != 0;
        return (conforming ? dpl > rpl : dpl != rpl) ? (uint)(cs & 0xFFFC) : null;
    }

    // ring0 からの IRET で EFLAGS.VM=1 を積んでいた場合は V86 モードへ入る: さらに ESP, SS, ES, DS, FS, GS を
    // pop し、全セグメントを セレクタ*16 基底の 16 ビットセグメントとしてロードする(NT の HAL は
    // この経路で BIOS(INT 10h 等)を V86 で呼ぶ)。
    static State<Unit> IretToV86(uint eip, ushort cs, uint flags) =>
        from sp in Pop(2)
        from ss in Pop(2)
        from es in Pop(2)
        from ds in Pop(2)
        from fs in Pop(2)
        from gs in Pop(2)
        from _f in SetCpu(cpu => { cpu.eflags = flags; return cpu; }) // 以降のセグメントロードは V86 規則
        from _1 in _eip.Set(eip)
        from _2 in LoadSReg(1, cs)
        from _3 in LoadSReg(2, (ushort)ss.dd)
        from _4 in SetCpu(cpu => { cpu.esp = sp.dd; return cpu; })
        from _5 in LoadSReg(0, (ushort)es.dd)
        from _6 in LoadSReg(3, (ushort)ds.dd)
        from _7 in LoadSReg(4, (ushort)fs.dd)
        from _8 in LoadSReg(5, (ushort)gs.dd)
        select Unit.unit;
}

// セグメント記述子の読み取り・照合と、セグメントレジスタのロード。
static public class Segments
{
    // 記述子の照合(VERR/VERW/LAR/LSL): GDT のセレクタの記述子 8 バイトを返す。ヌルセレクタ、LDT(未対応)、GDT の範囲外は null。
    static (uint lo, uint hi)? EnvDescriptorRaw(EmuEnvironment env, CPU cpu, ushort sel)
    {
        if ((sel & 0xFFFC) == 0 || (sel & 4) != 0 || (sel & 0xFFF8) + 7 > cpu.gdt_limit) return null;
        var off = cpu.gdt_base + (uint)(sel & 0xFFF8);
        return (EnvGetMemoryData32(env, off), EnvGetMemoryData32(env, off + 4));
    }

    // VERR/VERW: セレクタのセグメントが CPL と RPL から読める(書ける)か。システム記述子・実行専用(読み)・コードと読み取り専用(書き)は否。
    // 非コンフォーミングのセグメントは DPL >= CPL かつ DPL >= RPL が要る。例外は出さず真偽だけ返す。
    static internal bool EnvSegmentAccessible(EmuEnvironment env, CPU cpu, ushort sel, bool write)
    {
        if (EnvDescriptorRaw(env, cpu, sel) is not (_, var hi)) return false;
        var access = (hi >> 8) & 0xFF;
        if ((access & 0x10) == 0) return false;
        var code = (access & 0x08) != 0;
        var readableOrWritable = (access & 0x02) != 0;
        if (write ? code || !readableOrWritable : code && !readableOrWritable) return false;
        var conforming = code && (access & 0x04) != 0;
        var dpl = (int)(access >> 5) & 3;
        return conforming || (dpl >= cpu.cpl && dpl >= (sel & 3));
    }

    // LAR/LSL: アクセス権(hi の 0x00FFFF00)またはリミット(G ビットで 4K 単位を展開)。照合に通らなければ null。
    // 許されるシステム記述子は LAR が TSS/LDT/コールゲート/タスクゲート、LSL が TSS/LDT。非コンフォーミングなら特権の検査も。
    static internal uint? EnvDescriptorQuery(EmuEnvironment env, CPU cpu, ushort sel, bool limit)
    {
        if (EnvDescriptorRaw(env, cpu, sel) is not (var lo, var hi)) return null;
        var access = (hi >> 8) & 0xFF;
        var type = access & 0x0F;
        if ((access & 0x10) == 0 && !(limit ? type is 1 or 2 or 3 or 9 or 11 : type is 1 or 2 or 3 or 4 or 5 or 9 or 11 or 12)) return null;
        var conforming = (access & 0x18) == 0x18 && (access & 0x04) != 0;
        var dpl = (int)(access >> 5) & 3;
        if (!conforming && (dpl < cpu.cpl || dpl < (sel & 3))) return null;
        if (!limit) return hi & 0x00FFFF00;
        var segmentLimit = (lo & 0xFFFF) | (hi & 0x000F0000);
        return (hi & 0x00800000) != 0 ? (segmentLimit << 12) | 0xFFF : segmentLimit;
    }

    // GDT からセグメント記述子を読み、基底アドレスと D/B ビットを返す。
    static internal (uint base_, bool d) EnvReadDescriptor(EmuEnvironment env, CPU cpu, ushort sel)
    {
        var off = cpu.gdt_base + (uint)(sel & 0xFFF8);
        var lo = EnvGetMemoryData32(env, off);
        var hi = EnvGetMemoryData32(env, off + 4);
        var base_ = (lo >> 16) | ((hi & 0xFF) << 16) | (hi & 0xFF000000);
        return (base_, (hi & 0x400000) != 0); // bit22 = D/B
    }

    // セグメントレジスタへのロード。
    // リアルモードは セレクタ*16 を基底に(セッタが自動設定)、
    // プロテクトモードは GDT 記述子から基底を読む。CS の場合は D ビットで code32 を更新する。
    static public State<Unit> LoadSReg(int reg, ushort sel) =>
        SetCpu((env, cpu) =>
        {
            cpu = EnvSetSReg3(sel)(reg)(cpu);
            if (cpu.pe && !cpu.vm) // V86 モードはリアルモードと同じく セレクタ*16(セッタが設定済み)
            {
                var (base_, d) = EnvReadDescriptor(env, cpu, sel);
                cpu = ArraySregBase[reg].setter(cpu)(base_);
                if (reg == 1) // CS: D ビットで 32ビットコードか決まる
                    cpu.code32 = d;
                else if (reg == 2) // SS: B ビットで 32ビットスタックか決まる
                    cpu.stack32 = d;
            }
            else if (reg == 1)
                cpu.code32 = false;
            else if (reg == 2)
                cpu.stack32 = false;
            return cpu;
        });

    // MOV Sreg, r/m は常に下位16ビットのみロードする
    static public State<Unit> SetSReg3(int reg, Data data) =>
        LoadSReg(reg, data.type == 2 ? (ushort)data.dd : data.dw);
}


public partial class EmuEnvironment : IDisposable
{
    // 搭載RAM量。SeaBIOS の init 再配置は 1MB 超のRAMを要求するため、
    // 1MBちょうどではなく拡張メモリを持たせる。BIOS(bios.bin)は先頭1MBの末尾に配置される。
    public const int RamSize = 256 * 1024 * 1024; // 256MB(Debian の initramfs をロードするには 32MB では不足)

    public EmuEnvironment(byte[] memory = null, byte[] bios = null, AtaDevice ata = null)
    {
        OneMegaMemory_ = memory ?? new byte[RamSize];
        if (bios is { Length: > 0 })
        {
            if (bios.Length > 0x100000 || OneMegaMemory_.Length < 0x100000)
                throw new ArgumentException("BIOS must fit in the first megabyte of RAM", nameof(bios));
            bios.CopyTo(OneMegaMemory_, 0x100000 - bios.Length);
        }
        CmosRtc = new CmosDevice(OneMegaMemory_.Length);
        Translations = new TranslationCache(OneMegaMemory_.Length);
        Ata = ata;
    }

    public void Dispose()
    {
        Ata?.Dispose();
        Com1.Host?.Dispose(); Com1.Host = null;
        Com2.Host?.Dispose(); Com2.Host = null;
    }

    // プライマリ ATA チャネルのマスタドライブ(未接続なら null)。
    public AtaDevice Ata;

    // PCI ホスト(コンフィグ機構 #1)。ata_piix を bind させるため PIIX3 IDE を露出する。
    public PciHost Pci = new();

    public byte[] OneMegaMemory_;

    public byte[] IoPort = new byte[0x10000];

    // 翻訳キャッシュ(docs/adr/0002)。RAM への書き込みで該当ページの翻訳を捨てる。スナップショットには含めない。
    public TranslationCache Translations;

    // ページング変換状態と TLB(CPU の制御レジスタのミラー。Paging.Sync で同期。スナップショットには含めない)。
    public readonly Paging Paging = new();

    // ウォッチポイント・書き込みログ・割り込みログ(デバッグ用)。何も有効でなければ null。
    public MemoryDiagnostics Diagnostics;

    // タイムスタンプカウンタの代用(Runner が毎命令、実行済み命令数を書き込む)。
    public ulong Tsc;

    // システムリセット要求(ポート 0xCF9 または 8042 経由)。ランナーが見て実行を抜ける。スナップショットには含めない。
    public bool ResetRequested;

    // MSR の汎用ストア(RDMSR/WRMSR)。未書き込みは 0 として読める。
    public Dictionary<uint, ulong> Msrs = new();

    // デバッグレジスタ DR0-DR7(保持のみ。ブレークポイント機能はなし)。
    public uint[] Dr = new uint[8];
}
