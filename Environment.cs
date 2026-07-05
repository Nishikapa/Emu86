using System.Reflection;
using static Emu86.CPU;

namespace Emu86;

// ページフォルト。Runner が捕捉して #PF(ベクタ 14)を IDT 経由で配送する。
// errorCode: bit0=保護違反(1)/不在(0)、bit1=書き込み、bit2=ユーザ。
public class PageFaultException(uint linear, uint errorCode) : Exception
{
    public uint Linear => linear;
    public uint ErrorCode => errorCode;
    public override string Message =>
        $"PAGE FAULT lin={linear:x8} err={errorCode:x} " +
        $"({((errorCode & 1) != 0 ? "protection" : "not-present")}, {((errorCode & 2) != 0 ? "write" : "read")})";
}

static public partial class Ext
{
    /// Paging //////////////////////////////////
    // CPU の CR0.PG/CR3 をページング変換状態へ反映する(CR 書き込み・スナップショット復元時に呼ぶ)。
    static public void EnvSyncPaging(EmuEnvironment env, CPU cpu)
    {
        env.PagingOn = cpu.pg;
        env.PaeOn = 0 != (cpu.cr4 & 0x20);
        env.WpOn = cpu.wp; // CR0.WP: リング0 の読み取り専用ページ書き込みを禁じる
        // PAE では CR3 は 32 バイト境界の PDPT を指す。
        env.Cr3Base = cpu.cr3 & (env.PaeOn ? 0xFFFFFFE0 : 0xFFFFF000);
        env.FlushTlb();
    }

    // 線形→物理アドレス変換(2レベルページテーブル + 直結 TLB)。
    // リング0 のブートコードが対象のため、U/S・R/W の保護チェックは行わない(存在チェックのみ)。
    static uint EnvTranslate(EmuEnvironment env, uint lin, bool write)
    {
        var vpn = lin >> 12;
        var idx = vpn & (EmuEnvironment.TlbSize - 1);
        if (write)
        {
            if (env.TlbTagW[idx] == (0x80000000u | vpn))
                return env.TlbPhysW[idx] | (lin & 0xFFF);
        }
        else if (env.TlbTagR[idx] == (0x80000000u | vpn))
            return env.TlbPhysR[idx] | (lin & 0xFFF);
        return EnvWalkPageTable(env, lin, write);
    }

    static uint EnvWalkPageTable(EmuEnvironment env, uint lin, bool write) =>
        EnvTlbFill(env, lin, write, env.PaeOn ? EnvWalkPae(env, lin, write) : EnvWalk2Level(env, lin, write));

    // 書き込みが許されるか(CR0.WP=1 のときのみ R/W ビットを尊重する)を検査し、
    // 違反なら保護フォルトを投げる。許されるなら Accessed/Dirty 更新用のビットを返す。
    static uint EnvCheckWrite(EmuEnvironment env, uint lin, bool write, bool rw)
    {
        if (write && env.WpOn && !rw)
            throw new PageFaultException(lin, 0b011); // present + write + supervisor
        return write ? 0x40u : 0u;
    }

    // 非 PAE: 2 レベル(PDE→PTE、PSE 4MB ページ対応)。ページの物理基底を返す。
    static uint EnvWalk2Level(EmuEnvironment env, uint lin, bool write)
    {
        var pdeAddr = env.Cr3Base + ((lin >> 22) << 2);
        var pde = RawRead32(env, pdeAddr);
        if ((pde & 1) == 0)
            throw new PageFaultException(lin, write ? 2u : 0u);

        if ((pde & 0x80) != 0)
        {
            // 4MB ページ(PSE)。Accessed/Dirty を立てる。
            var dirty = EnvCheckWrite(env, lin, write, (pde & 2) != 0);
            RawWrite32(env, pdeAddr, pde | 0x20u | dirty);
            return (pde & 0xFFC00000) | (lin & 0x003FF000);
        }

        var pteAddr = (pde & 0xFFFFF000) + (((lin >> 12) & 0x3FF) << 2);
        var pte = RawRead32(env, pteAddr);
        if ((pte & 1) == 0)
            throw new PageFaultException(lin, write ? 2u : 0u);
        // 有効 R/W は PDE と PTE の R/W の論理積。
        var dirty2 = EnvCheckWrite(env, lin, write, (pde & 2) != 0 && (pte & 2) != 0);
        // Accessed/Dirty を更新する(Linux のページ管理が参照する)。
        if ((pde & 0x20) == 0)
            RawWrite32(env, pdeAddr, pde | 0x20u);
        var newPte = pte | 0x20u | dirty2;
        if (newPte != pte)
            RawWrite32(env, pteAddr, newPte);
        return pte & 0xFFFFF000;
    }

    // PAE: 3 レベル(PDPT→PDE→PTE、エントリ 8 バイト、2MB ページ対応)。
    // RAM は 4GB 未満なので各エントリの上位 32 ビットは無視する。
    static uint EnvWalkPae(EmuEnvironment env, uint lin, bool write)
    {
        var pdpte = RawRead32(env, env.Cr3Base + ((lin >> 30) << 3));
        if ((pdpte & 1) == 0)
            throw new PageFaultException(lin, write ? 2u : 0u);

        var pdeAddr = (pdpte & 0xFFFFF000) + (((lin >> 21) & 0x1FF) << 3);
        var pde = RawRead32(env, pdeAddr);
        if ((pde & 1) == 0)
            throw new PageFaultException(lin, write ? 2u : 0u);

        if ((pde & 0x80) != 0)
        {
            // 2MB ページ。Accessed/Dirty を立てる。
            var dirty = EnvCheckWrite(env, lin, write, (pde & 2) != 0);
            RawWrite32(env, pdeAddr, pde | 0x20u | dirty);
            return (pde & 0xFFE00000) | (lin & 0x001FF000);
        }

        var pteAddr = (pde & 0xFFFFF000) + (((lin >> 12) & 0x1FF) << 3);
        var pte = RawRead32(env, pteAddr);
        if ((pte & 1) == 0)
            throw new PageFaultException(lin, write ? 2u : 0u);
        var dirty2 = EnvCheckWrite(env, lin, write, (pde & 2) != 0 && (pte & 2) != 0);
        if ((pde & 0x20) == 0)
            RawWrite32(env, pdeAddr, pde | 0x20u);
        var newPte = pte | 0x20u | dirty2;
        if (newPte != pte)
            RawWrite32(env, pteAddr, newPte);
        return pte & 0xFFFFF000;
    }

    static uint EnvTlbFill(EmuEnvironment env, uint lin, bool write, uint physPage)
    {
        var vpn = lin >> 12;
        var idx = vpn & (EmuEnvironment.TlbSize - 1);
        if (write) { env.TlbTagW[idx] = 0x80000000u | vpn; env.TlbPhysW[idx] = physPage; }
        else { env.TlbTagR[idx] = 0x80000000u | vpn; env.TlbPhysR[idx] = physPage; }
        return physPage | (lin & 0xFFF);
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

    /// Mem /////////////////////////////////////
    // ArraySegment を使い、LINQ Skip の O(addr) 走査を避ける。
    // ページング有効時はページ境界を正しく跨ぐため 1 バイトずつ変換して読む遅延列挙に切り替える。
    static public IEnumerable<byte> EnvGetMemoryDatas(EmuEnvironment env, uint addr) =>
        env.PagingOn
            ? EnumerateLinear(env, addr)
            : new ArraySegment<byte>(env.OneMegaMemory_, (int)addr, env.OneMegaMemory_.Length - (int)addr);

    static IEnumerable<byte> EnumerateLinear(EmuEnvironment env, uint addr)
    {
        for (uint i = 0; ; i++)
            yield return EnvGetMemoryData8(env, addr + i);
    }

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

    /// Reg /////////////////////////////////////
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
    static private readonly Accessor<CPU, uint>[] ArraySregBase =
    [
        _es_base, _cs_base, _ss_base, _ds_base, _fs_base, _gs_base
    ];

    static private readonly Func<int, Func<CPU, ushort>> GetSReg3 = EnvGetDataFromCPU(ArraySreg);
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
        from _1 in Choice(
            reg,
            (0, _cr0),
            (2, _cr2),
            (3, _cr3),
            (4, _cr4)
        ).Set(data)
        from _2 in SetCpu((env, cpu) => { EnvSyncPaging(env, cpu); return cpu; })
        select Unit.unit;

    static public State<Unit> SetRegData(int reg, Data data) =>
        SetCpu(EnvSetRegData(data)(reg));

    static public State<ushort> GetRegData16(int reg) =>
        GetDataFromCpu(EnvGetDataFromCPU(ArrayReg16)(reg));

    static public State<uint> GetRegData32(int reg) =>
        GetDataFromCpu(EnvGetDataFromCPU(ArrayReg32)(reg));

    /// Stack ////////////////////////////////////
    // スタックトップの物理アドレス。SS の基底 + SP/ESP(32ビットコードでは ESP)。
    static private MemAddr EnvGetStackAddr(CPU cpu) =>
        (true, cpu.ss_base + (cpu.code32 ? cpu.esp : cpu.sp));

    // SP/ESP をデータ長分減らしてからスタックトップへ書き込む（PUSH）。
    static public State<Unit> Push(Data data) =>
        from cpu0 in GetCpu
        from _1 in cpu0.code32 ?
            _esp.Set((uint)(cpu0.esp - arrLen[data.type])) :
            _sp.Set((ushort)(cpu0.sp - arrLen[data.type]))
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
        from _ in cpu.code32 ?
            _esp.Set((uint)(cpu.esp + arrLen[type])) :
            _sp.Set((ushort)(cpu.sp + arrLen[type]))
        select data;

    static public State<Unit> Push16(ushort value) =>
        Push(value.ToTypeData());

    static public State<ushort> Pop16 =>
        Pop(1).Select(data => data.dw);

    /// String ////////////////////////////////////
    // 文字列命令の実効パラメータ。
    //   要素サイズ(バイト): operand size に従う(w かつ 32bit なら4、そうでなければ 2/1)。
    //   アドレスモード     : code32 でフラット32bit(ESI/EDI, ベース0)、そうでなければ real(DS:SI/ES:DI)。
    static int StrSize(CPU cpu, bool w) => w ? ((cpu.code32 != cpu.operand_size_prefix) ? 4 : 2) : 1;
    static bool StrA32(CPU cpu) => cpu.code32 != cpu.address_size_prefix;
    static uint StrSrc(CPU cpu) => cpu.ds_base + (StrA32(cpu) ? cpu.esi : cpu.si);
    static uint StrDst(CPU cpu) => cpu.es_base + (StrA32(cpu) ? cpu.edi : cpu.di);

    static uint EnvReadN(EmuEnvironment env, uint addr, int size) =>
        size == 1 ? EnvGetMemoryData8(env, addr) : size == 2 ? EnvGetMemoryData16(env, addr) : EnvGetMemoryData32(env, addr);

    static void EnvWriteN(EmuEnvironment env, uint addr, int size, uint val)
    {
        switch (size)
        {
            case 1: EnvWriteByte(env, addr, (byte)val); break;
            case 2: EnvSetMemoryDatas(env, addr, ((ushort)val).ToByteArray()); break;
            default: EnvSetMemoryDatas(env, addr, val.ToByteArray()); break;
        }
    }

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
            int size = StrSize(cpu, w);
            uint val = size == 1 ? cpu.al : size == 2 ? cpu.ax : cpu.eax;
            EnvWriteN(env, StrDst(cpu), size, val);
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
            int size = StrSize(cpu, w);
            return (size, s: EnvReadN(env, StrSrc(cpu), size), d: EnvReadN(env, StrDst(cpu), size));
        })
        from _f in vals.size == 1 ? update_eflags_sub((byte)vals.s, (byte)vals.d)
                 : vals.size == 2 ? update_eflags_sub((ushort)vals.s, (ushort)vals.d)
                 : update_eflags_sub(vals.s, vals.d)
        from _adv in SetCpu(cpu => StrAdvance(cpu, StrSize(cpu, w), si: true, di: true))
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

    // BT系: r/m の bit 番目を CF にコピーし、op に応じて値を変更して書き戻す。
    //   op: 0=BT(変更なし) 1=BTS(セット) 2=BTR(クリア) 3=BTC(反転)
    static public State<Unit> BitTest(MemAddr addr, Data data, int bit, int op) =>
        from _f in _cf.Set(((data.type == 1 ? data.dw : data.dd) & (1u << (bit & (data.type == 1 ? 15 : 31)))) != 0)
        from _w in op == 0
            ? Unit.unit.ToState()
            : SetMemOrRegData(addr, BitModify(data, bit, op))
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
        src.Value() == 0
            ? _zf.Set(true)
            : from _z in _zf.Set(false)
              from _w in SetRegData(reg, ((uint)BitScanIndex(src.Value(), forward, Bits(src.type))).ToTypeData(src.type))
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

    // ENTER imm16, level: スタックフレームを構築する。
    //   push BP; frame=SP; level 回ネストリンクをコピー; BP=frame; SP-=alloc。
    static public State<Unit> Enter(ushort alloc, int level) =>
        from bp0 in GetRegData16(5)
        from _p in Push16(bp0)
        from frame in GetRegData16(4)
        from _n in EnterNesting(frame, level & 0x1F)
        from _b in _bp.Set(frame)
        from _s in SetCpu(cpu => { cpu.sp -= alloc; return cpu; })
        select Unit.unit;

    // ネストレベル>0 のとき、外側フレームのリンクを順に push し、最後に frame を push する。
    static State<Unit> EnterNesting(ushort frame, int level) =>
        Enumerable.Range(1, level)
            .Select(i => i < level
                ? from _r in SetCpu(cpu => { cpu.bp -= 2; return cpu; })
                  from v in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData16(env, cpu.ss_base + cpu.bp))
                  from _p in Push16(v)
                  select Unit.unit
                : Push16(frame))
            .Sequence()
            .Ignore();

    // INT vector: モードに応じて配送する。
    //   リアルモード      : IVT(物理 vector*4)から CS:IP。FLAGS/CS/IP を 16 ビットで push。
    //   プロテクトモード  : IDT ゲート(idt_base + vector*8)から CS:EIP。
    //                       EFLAGS/CS/EIP を 32 ビットで push(リング遷移なし = ring0 のみ対応)。
    static public State<Unit> Interrupt(int vector) =>
        from cpu0 in GetCpu
        from _ in cpu0.pe ? InterruptProtected(vector, hasError: false, 0) : InterruptReal(vector)
        select Unit.unit;

    // #PF(ベクタ 14): CR2 にフォルトアドレスを設定し、エラーコードを push して配送する。
    static public State<Unit> PageFault(uint linear, uint errorCode) =>
        from _1 in SetCpu(cpu => { cpu.cr2 = linear; return cpu; })
        from _2 in InterruptProtected(14, hasError: true, errorCode)
        select Unit.unit;

    static State<Unit> InterruptReal(int vector) =>
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
            if (env.IntLog != null && cpu.pg)
                env.IntLog.Add($"vec={vector:x2} err={(hasError ? errorCode.ToString("x") : "-")} at cs:eip={cpu.cs:x4}:{cpu.eip:x8} esp={cpu.esp:x8}");
            return Unit.unit;
        })
        from gateLo in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData32(env, cpu.idt_base + (uint)vector * 8))
        from gateHi in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData32(env, cpu.idt_base + (uint)vector * 8 + 4))
        let offset = (gateHi & 0xFFFF0000) | (gateLo & 0xFFFF)
        let sel = (ushort)(gateLo >> 16)
        let gateType = (int)(gateHi >> 8) & 0xF
        from fl in GetDataFromCpu(cpu => cpu.eflags)
        from _1 in Push(fl.ToTypeData())
        from cs in GetSRegData(1)
        from _2 in Push(((uint)cs).ToTypeData())
        from eip0 in GetDataFromCpu(cpu => cpu.eip)
        from _3 in Push(eip0.ToTypeData())
        // 一部の例外(#PF 等)は EIP の後ろにエラーコードを push する。
        from _e in hasError ? Push(errorCode.ToTypeData()) : Unit.unit.ToState()
        // 割り込みゲート(型 0xE)は IF をクリアする。トラップゲート(0xF)は保つ。
        from _if in SetCpu(cpu => { if (gateType == 0xE) cpu.jf = false; cpu.tf = false; return cpu; })
        from _4 in _eip.Set(offset)
        from _5 in LoadSReg(1, sel)
        select Unit.unit;

    // IRET: モードに応じて復帰する(リアル/16bit と プロテクト/32bit)。
    static public State<Unit> Iret =>
        from cpu0 in GetCpu
        from _ in cpu0.pe && cpu0.code32 ? Iret32 : Iret16
        select Unit.unit;

    static State<Unit> Iret16 =>
        from ip in Pop16
        from _1 in _ip.Set(ip)
        from cs in Pop16
        from _2 in LoadSReg(1, cs)
        from fl in Pop16
        from _3 in SetCpu(cpu => { cpu.eflags = (cpu.eflags & 0xFFFF0000) | fl; return cpu; })
        select Unit.unit;

    static State<Unit> Iret32 =>
        from eip in Pop(2)
        from cs in Pop(2)
        from fl in Pop(2)
        from _1 in _eip.Set(eip.dd)
        from _2 in LoadSReg(1, (ushort)cs.dd)
        from _3 in SetCpu(cpu => { cpu.eflags = fl.dd; return cpu; })
        select Unit.unit;

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
        SetCpu((env, cpu) =>
        {
            var addr = EnvMoffsAddr(cpu, offset);
            var bytes = type switch
            {
                0 => _al.getter(cpu).ToByteArray(),
                1 => _ax.getter(cpu).ToByteArray(),
                _ => _eax.getter(cpu).ToByteArray(),
            };
            EnvSetMemoryDatas(env, addr, bytes);
            return cpu;
        });

    // SCAS: AL/AX - [ES:DI] でフラグを更新（結果は破棄）し、DI を増減する。
    static public State<Unit> Scas(bool w) =>
        from vals in GetDataFromEnvCpu((env, cpu) =>
        {
            int size = StrSize(cpu, w);
            uint a = size == 1 ? cpu.al : size == 2 ? cpu.ax : cpu.eax;
            return (size, a, m: EnvReadN(env, StrDst(cpu), size));
        })
        from _f in vals.size == 1 ? update_eflags_sub((byte)vals.a, (byte)vals.m)
                 : vals.size == 2 ? update_eflags_sub((ushort)vals.a, (ushort)vals.m)
                 : update_eflags_sub(vals.a, vals.m)
        from _adv in SetCpu(cpu => StrAdvance(cpu, StrSize(cpu, w), si: false, di: true))
        select Unit.unit;

    // セグメントレジスタ(ES/CS/SS/DS/FS/GS)を reg 番号で読み出す。
    static public State<ushort> GetSRegData(int reg) =>
        GetDataFromCpu(GetSReg3(reg));

    static public State<Data> GetRegData(int reg, int type) =>
        GetDataFromCpu(GetTypeData_(
            type,
            EnvGetDataFromCPU(ArrayReg8)(reg),
            EnvGetDataFromCPU(ArrayReg16)(reg),
            EnvGetDataFromCPU(ArrayReg32)(reg)
            )
        );

    static public State<uint> GetCrReg(int reg) =>
        Choice(
            reg,
            (0, Get(_cr0)),
            (2, Get(_cr2)),
            (3, Get(_cr3)),
            (4, Get(_cr4))
        );

    // GDT からセグメント記述子を読み、基底アドレスと D/B ビットを返す。
    static (uint base_, bool d) EnvReadDescriptor(EmuEnvironment env, CPU cpu, ushort sel)
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
            if (cpu.pe)
            {
                var (base_, d) = EnvReadDescriptor(env, cpu, sel);
                cpu = ArraySregBase[reg].setter(cpu)(base_);
                if (reg == 1) // CS
                    cpu.code32 = d;
            }
            else if (reg == 1)
                cpu.code32 = false;
            return cpu;
        });

    // MOV Sreg, r/m は常に下位16ビットのみロードする
    static public State<Unit> SetSReg3(int reg, Data data) =>
        LoadSReg(reg, data.type == 2 ? (ushort)data.dd : data.dw);

    /// MemReg //////////////////////////////////
    // データアクセスのセグメントベース(記述子キャッシュから)。
    // seg: 既定 DS(オーバーライドプレフィックスで置換)
    // ss : BP/EBP/ESP ベースの既定 SS(こちらもオーバーライドで置換される)
    static private (uint seg, uint ss) EnvSegBases(CPU cpu)
    {
        uint? ovr =
            cpu.es_prefix ? cpu.es_base :
            cpu.cs_prefix ? cpu.cs_base :
            cpu.ss_prefix ? cpu.ss_base :
            cpu.ds_prefix ? cpu.ds_base :
            cpu.fs_prefix ? cpu.fs_base :
            cpu.gs_prefix ? cpu.gs_base : null;
        return (ovr ?? cpu.ds_base, ovr ?? cpu.ss_base);
    }

    // ModRM 16ビットアドレッシング。実効オフセット・セグメントベース・消費した disp バイト数を返す。
    static private (bool isMem, uint offset, uint segBase, int inc) EnvGetMemOrRegAddr16_(CPU cpu, int mod, int rm, IEnumerable<byte> disp)
    {
        if (mod == 3)
            return (false, (uint)rm, 0, 0);

        var (segment_base, ss_base) = EnvSegBases(cpu);

        if (mod == 0 && rm == 6) // [d16]
            return (true, disp.ToUint16(), segment_base, 2);

        // rm ごとのレジスタ組み合わせ(BP を含む形は既定セグメントが SS)
        uint[] regsum =
        [
            (uint)(cpu.bx + cpu.si), (uint)(cpu.bx + cpu.di), (uint)(cpu.bp + cpu.si), (uint)(cpu.bp + cpu.di),
            cpu.si, cpu.di, cpu.bp, cpu.bx
        ];
        var segBase = rm is 2 or 3 or 6 ? ss_base : segment_base;

        return mod switch
        {
            0 => (true, regsum[rm], segBase, 0),
            1 => (true, (uint)(regsum[rm] + (sbyte)disp.ElementAt(0)), segBase, 1),
            _ => (true, regsum[rm] + disp.ToUint16(), segBase, 2),
        };
    }
    // ModRM 32ビットアドレッシング(SIB 対応)。実効オフセット・セグメントベース・消費 disp バイト数を返す。
    static private (bool isMem, uint offset, uint segBase, int inc) EnvGetMemOrRegAddr32_(CPU cpu, int mod, int rm, IEnumerable<byte> disp)
    {
        if (mod == 3)
            return (false, (uint)rm, 0, 0);

        var (segment_base, ss_base) = EnvSegBases(cpu);

        // SIB バイト(rm=4)。base=5 かつ mod=0 は「ベースなし、disp32 が続く」特殊ケース。
        // ベースが ESP/EBP のときは既定セグメントが SS になる。
        if (rm == 4)
        {
            var sib = disp.ElementAt(0);
            var scaled = (uint)((1 << ((sib >> 6) & 3)) * EnvGetIndexRegData32(cpu, (sib >> 3) & 7));
            var basef = sib & 7;
            var sb = basef is 4 or 5 ? ss_base : segment_base;
            return (mod, basef) switch
            {
                (0, 5) => (true, scaled + disp.Skip(1).ToUint32(), segment_base, 5),
                (0, _) => (true, scaled + EnvGetReg32(cpu, basef), sb, 1),
                (1, _) => (true, (uint)(scaled + EnvGetReg32(cpu, basef) + (sbyte)disp.ElementAt(1)), sb, 2),
                _ => (true, scaled + EnvGetReg32(cpu, basef) + disp.Skip(1).ToUint32(), sb, 5),
            };
        }

        if (mod == 0 && rm == 5) // [d32]
            return (true, disp.ToUint32(), segment_base, 4);

        // ベースレジスタ(EBP ベースは SS)
        var segBase = rm == 5 ? ss_base : segment_base;
        var reg = EnvGetReg32(cpu, rm);
        return mod switch
        {
            0 => (true, reg, segBase, 0),
            1 => (true, (uint)(reg + (sbyte)disp.ElementAt(0)), segBase, 1),
            _ => (true, reg + disp.ToUint32(), segBase, 4),
        };
    }

    static private (bool isMem, uint offset, uint segBase, int inc) EnvGetMemOrRegAddr_(EmuEnvironment env, CPU cpu, int mod, int rm) =>
        // 32ビットコードではデフォルトが32ビットModRMになり、address_size_prefix(0x67)で反転する
        (cpu.code32 != cpu.address_size_prefix) ?
        EnvGetMemOrRegAddr32_(cpu, mod, rm, EnvGetMemoryDatas(env, GetCodeAddr(cpu).addr)) :
        EnvGetMemOrRegAddr16_(cpu, mod, rm, EnvGetMemoryDatas(env, GetCodeAddr(cpu).addr));

    // 物理アドレス(セグメントベース + 実効オフセット)を返す通常版。
    static public State<MemAddr> GetMemOrRegAddr(int mod, int rm) =>
        from data in GetDataFromEnvCpu((env, cpu) => EnvGetMemOrRegAddr_(env, cpu, mod, rm))
        from _ in IpInc(data.inc)
        select (data.isMem, data.offset + data.segBase);

    // 実効オフセットのみを返す版(LEA 用: セグメントベースを加算しない)。
    static public State<MemAddr> GetMemOrRegOffset(int mod, int rm) =>
        from data in GetDataFromEnvCpu((env, cpu) => EnvGetMemOrRegAddr_(env, cpu, mod, rm))
        from _ in IpInc(data.inc)
        select (data.isMem, data.offset);

    static public State<byte> GetMemOrRegData8(MemAddr t) =>
        GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData8(env, t.addr) : EnvGetDataFromCPU(ArrayReg8)((int)t.addr)(cpu));

    static public State<ushort> GetMemOrRegData16(MemAddr t) =>
        GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData16(env, t.addr) : EnvGetDataFromCPU(ArrayReg16)((int)t.addr)(cpu));

    static public State<uint> GetMemOrRegData32(MemAddr t) =>
        GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData32(env, t.addr) : EnvGetDataFromCPU(ArrayReg32)((int)t.addr)(cpu));

    static Func<int, Func<CPU, CPU>> EnvSetRegData(Data data) =>
        SetTypeData
        (
            data,
            EnvSetRegData8,
            EnvSetRegData16,
            EnvSetRegData32
        );

    static Action<EmuEnvironment, uint> EnvSetMemoryData(Data data) =>
        SetTypeData<Action<EmuEnvironment, uint>>
        (
            data,
            db => (env, addr) => { EnvWriteByte(env, addr, db); },
            dw => (env, addr) => { EnvSetMemoryDatas(env, addr, dw.ToByteArray()); },
            dd => (env, addr) => { EnvSetMemoryDatas(env, addr, dd.ToByteArray()); }
        );

    static public State<Unit> SetMemOrRegData(
        MemAddr t,
        Data data) =>
        SetCpu((env, cpu) =>
        {
            if (t.isMem)
            {
                EnvSetMemoryData(data)(env, t.addr);
                return cpu;
            }
            else
            {
                return EnvSetRegData(data)((int)t.addr)(cpu);
            }
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

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // 32ビット汎用レジスタを番号で読む。
    static private uint EnvGetReg32(CPU cpu, int reg) =>
        new[] { cpu.eax, cpu.ecx, cpu.edx, cpu.ebx, cpu.esp, cpu.ebp, cpu.esi, cpu.edi }[reg];

    // SIB の index(4=なし)。
    static private uint EnvGetIndexRegData32(CPU cpu, int reg) =>
        reg == 4 ? 0 : EnvGetReg32(cpu, reg);

    static private Func<T, Func<int, Func<CPU, CPU>>> EnvSetDataFromCPU<T>(Accessor<CPU, T>[] array) =>
        data => reg => cpu => array[reg].setter(cpu)(data);

    static private void EnvSetMemoryDatas(EmuEnvironment env, uint addr, byte[] data)
    {
        for (int i = 0; i < data.Length; ++i)
        {
            EnvWriteByte(env, addr + (uint)i, data[i]);
        }
    }

    /// I/O port ///////////////////////////////////
    // CMOS(0x70/0x71)・PIT(0x40/0x43)・ATA(0x1F0-0x1F7, 0x3F6)を
    // 特別扱いする以外は IoPort 配列を素通しする。
    static public byte EnvInPort(EmuEnvironment env, int port)
    {
        switch (port & 0xFFFF)
        {
            case 0x71: // CMOS データ
                return env.Cmos[env.CmosIndex];
            case 0x21: // PIC マスタ: マスク読み出し
                return env.PicMasterMask;
            case 0xA1: // PIC スレーブ: マスク読み出し
                return env.PicSlaveMask;
            case 0x20: // マスタ IRR/ISR 読み(in-service ビットを返す)
                return env.PicMasterIsr;
            case 0xA0: // スレーブ IRR/ISR 読み
                return env.PicSlaveIsr;
            case 0x61: // システム制御ポートB(NMI/PIT ch2 ゲート・スピーカ)
                // bit4 = リフレッシュタイマ(読むたびにトグルさせて DRAM リフレッシュ
                //         監視のディレイループを進める)。
                // bit5 = PIT ch2 の OUT。ゲート有効かつ ch2 がプログラム済みなら、
                //         数回読んだ後に High にして TSC 校正のウェイトを抜けさせる。
                env.Port61Refresh ^= 0x10;
                byte out2 = 0;
                if ((env.Port61 & 1) != 0 && env.Pit2Armed)
                {
                    if (env.Pit2Wait > 0) env.Pit2Wait--;
                    else out2 = 0x20;
                }
                return (byte)((env.Port61 & 0x0F) | env.Port61Refresh | out2);
            case 0x42: // PIT チャネル2 データ(読み出しはラッチ値を返す)
                byte c2 = (byte)(env.Pit2ReadPhase == 0 ? env.Pit2Counter : env.Pit2Counter >> 8);
                env.Pit2ReadPhase ^= 1;
                return c2;
            case 0x40: // PIT チャネル0
                // リードバックでステータスがラッチされていれば、まずそれを返す。
                //   0x36 = OUT=0, NULL COUNT=0(カウント有効), lo/hi アクセス, モード3, 二進
                // Linux の i8254 エントロピー読み(KASLR)は NULL COUNT ビットが
                // 落ちるまでポーリングするため、これがないと無限ループになる。
                if (env.PitStatusPending)
                {
                    env.PitStatusPending = false;
                    return 0x36;
                }
                byte b = (byte)(env.PitReadPhase == 0 ? env.PitLatched : env.PitLatched >> 8);
                env.PitReadPhase ^= 1;
                return b;
            case 0x1F0 when env.Ata != null:
                return (byte)env.Ata.ReadData(1);
            case (>= 0x1F1 and <= 0x1F7) or 0x3F6 when env.Ata != null:
                return env.Ata.ReadReg(port & 0xFFFF);
            default:
                return env.IoPort[port & 0xFFFF];
        }
    }

    // 幅付きポート入出力。ATA データポート(0x1F0)は 16/32 ビットアクセスで
    // デバイスバッファから連続バイトを転送する(隣接ポートへは波及しない)。
    static public uint EnvInPortN(EmuEnvironment env, int port, int size)
    {
        if ((port & 0xFFFF) == 0x1F0 && env.Ata != null)
            return env.Ata.ReadData(size);
        uint v = 0;
        for (int i = 0; i < size; i++)
            v |= (uint)EnvInPort(env, port + i) << (8 * i);
        return v;
    }

    static public void EnvOutPortN(EmuEnvironment env, int port, int size, uint val)
    {
        if ((port & 0xFFFF) == 0x1F0 && env.Ata != null)
        {
            env.Ata.WriteData(size, val);
            return;
        }
        for (int i = 0; i < size; i++)
            EnvOutPort(env, port + i, (byte)(val >> (8 * i)));
    }

    static public void EnvOutPort(EmuEnvironment env, int port, byte val)
    {
        switch (port & 0xFFFF)
        {
            case 0x70: // インデックス選択(bit7 は NMI 禁止フラグなので落とす)
                env.CmosIndex = val & 0x7F;
                break;
            case 0x71: // 選択中の CMOS レジスタへ書き込み
                env.Cmos[env.CmosIndex] = val;
                break;
            case 0x43: // PIT コントロール: ラッチ/リードバックでカウンタを捕捉し時刻を進める
                //   カウンタラッチ  : bit5-4 = 00
                //   リードバック    : bit7-6 = 11。bit5=0 でカウント、bit4=0 でステータスをラッチ
                int channel = (val >> 6) & 3;
                if (channel == 2)
                {
                    // ch2 のプログラム開始。lo/hi 書き込みの位相をリセットする。
                    env.Pit2WritePhase = 0;
                    env.Pit2Armed = false;
                    break;
                }
                bool readback = (val & 0xC0) == 0xC0;
                bool latch = (val & 0x30) == 0 || (readback && (val & 0x20) == 0);
                if (latch)
                {
                    env.PitLatched = env.PitCounter;
                    env.PitReadPhase = 0;
                    env.PitCounter -= 0x100; // 経過時間の代用として下向きに減算する
                }
                if (readback && (val & 0x10) == 0)
                    env.PitStatusPending = true;
                break;
            case 0x42: // PIT チャネル2 カウント(lo→hi)。全部書けたら「短時間で満了」として武装する。
                if (env.Pit2WritePhase == 0) { env.Pit2Counter = val; env.Pit2WritePhase = 1; }
                else
                {
                    env.Pit2Counter = (ushort)((env.Pit2Counter & 0xFF) | (val << 8));
                    env.Pit2WritePhase = 0;
                    env.Pit2Armed = true;
                    env.Pit2Wait = 2; // 数回 0x61 を読んだら OUT2 を立てる(ウェイトを抜けさせる)
                }
                break;
            case 0x61: // システム制御ポートB。低位ビット(ゲート/スピーカ)を保持する。
                env.Port61 = val;
                if ((val & 1) == 0) env.Pit2Armed = false; // ゲート断で武装解除
                break;
            case 0x402:
                System.Console.Error.Write((char)val);
                break;
            // 8259 PIC(マスタ 0x20/0x21、スレーブ 0xA0/0xA1)。
            // ICW1-4 の初期化シーケンスとマスク、ベクタベース(ICW2)のみ追跡する。
            // EOI(OCW2)は、割り込みキューを持たないため無視してよい。
            case 0x20:
                if ((val & 0x10) != 0) { env.PicMasterInit = 1; env.PicMasterIcw4 = (val & 1) != 0; }
                else if ((val & 0x08) == 0) // OCW2
                {
                    // EOI(非特定 0x20 / 特定 0x60|irq)で in-service を降ろす。
                    if ((val & 0x20) != 0)
                        env.PicMasterIsr = (val & 0x40) != 0 ? (byte)(env.PicMasterIsr & ~(1 << (val & 7))) : (byte)0;
                }
                break;
            case 0x21:
                if (env.PicMasterInit == 1) { env.PicMasterBase = val; env.PicMasterInit = 2; }
                else if (env.PicMasterInit == 2) { env.PicMasterInit = env.PicMasterIcw4 ? 3 : 0; }
                else if (env.PicMasterInit == 3) { env.PicMasterInit = 0; }
                else env.PicMasterMask = val;
                break;
            case 0xA0:
                if ((val & 0x10) != 0) { env.PicSlaveInit = 1; env.PicSlaveIcw4 = (val & 1) != 0; }
                else if ((val & 0x08) == 0) // OCW2
                {
                    if ((val & 0x20) != 0)
                        env.PicSlaveIsr = (val & 0x40) != 0 ? (byte)(env.PicSlaveIsr & ~(1 << (val & 7))) : (byte)0;
                }
                break;
            case 0xA1:
                if (env.PicSlaveInit == 1) { env.PicSlaveBase = val; env.PicSlaveInit = 2; }
                else if (env.PicSlaveInit == 2) { env.PicSlaveInit = env.PicSlaveIcw4 ? 3 : 0; }
                else if (env.PicSlaveInit == 3) { env.PicSlaveInit = 0; }
                else env.PicSlaveMask = val;
                break;
            case 0x1F0 when env.Ata != null:
                env.Ata.WriteData(1, val);
                break;
            case (>= 0x1F1 and <= 0x1F7) or 0x3F6 when env.Ata != null:
                env.Ata.WriteReg(port & 0xFFFF, val);
                break;
            default:
                env.IoPort[port & 0xFFFF] = val;
                break;
        }
    }

    // 搭載RAM(RamSize)外へのアクセスは未マップ領域として扱う:
    //   読み出しは 0xFF(オープンバス)、書き込みは無視。MMIO 等をエミュレートしない代わりに
    //   クラッシュを避ける。
    // アドレス引数は線形アドレス。ページング有効時はここで物理へ変換し、
    // 複数バイトのアクセスがページ境界を跨ぐ場合は 1 バイトずつ変換する。
    static private void EnvWriteByte(EmuEnvironment env, uint addr, byte val)
    {
        if (env.PagingOn) addr = EnvTranslate(env, addr, write: true);
        if (env.PagingOn && addr >= env.WatchLo && addr < env.WatchHi) env.WatchTriggered = true;
        if (env.WriteLog != null && addr >= env.WLogLo && addr < env.WLogHi)
            env.WriteLog.Add($"{env.CurEip:x8}: [{addr:x8}]={val:x2}");
        if (addr < (uint)env.OneMegaMemory_.Length) env.OneMegaMemory_[addr] = val;
    }

    static public byte EnvGetMemoryData8(EmuEnvironment env, uint addr)
    {
        if (env.PagingOn) addr = EnvTranslate(env, addr, write: false);
        return addr < (uint)env.OneMegaMemory_.Length ? env.OneMegaMemory_[addr] : (byte)0xFF;
    }

    static private ushort EnvGetMemoryData16(EmuEnvironment env, uint addr)
    {
        if (env.PagingOn)
        {
            if ((addr & 0xFFF) >= 0xFFF)
                return (ushort)(EnvGetMemoryData8(env, addr) | (EnvGetMemoryData8(env, addr + 1) << 8));
            addr = EnvTranslate(env, addr, write: false);
        }
        return addr + 2 <= (uint)env.OneMegaMemory_.Length ? BitConverter.ToUInt16(env.OneMegaMemory_, (int)addr) : (ushort)0xFFFF;
    }

    static private uint EnvGetMemoryData32(EmuEnvironment env, uint addr)
    {
        if (env.PagingOn)
        {
            if ((addr & 0xFFF) > 0xFFC)
                return (uint)(EnvGetMemoryData8(env, addr)
                    | (EnvGetMemoryData8(env, addr + 1) << 8)
                    | (EnvGetMemoryData8(env, addr + 2) << 16)
                    | (EnvGetMemoryData8(env, addr + 3) << 24));
            addr = EnvTranslate(env, addr, write: false);
        }
        return addr + 4 <= (uint)env.OneMegaMemory_.Length ? BitConverter.ToUInt32(env.OneMegaMemory_, (int)addr) : 0xFFFFFFFF;
    }
}

public class EmuEnvironment
{
    // 搭載RAM量。SeaBIOS の init 再配置は 1MB 超のRAMを要求するため、
    // 1MBちょうどではなく拡張メモリを持たせる。BIOS(bios.bin)は先頭1MBの末尾に配置される。
    public const int RamSize = 32 * 1024 * 1024; // 32MB

    public EmuEnvironment()
    {
        // bios: 埋め込みリソース "bios.bin" が存在すれば先頭1MB空間の末尾に配置する。
        //       リソースが無い場合はゼロ初期化のまま起動する。
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("bios.bin", StringComparison.OrdinalIgnoreCase));
        if (resName != null)
        {
            using var stream = asm.GetManifestResourceStream(resName);
            var biosdata = new byte[stream.Length];
            stream.ReadExactly(biosdata);
            Array.Copy(biosdata, 0, OneMegaMemory_, 0x100000 - biosdata.Length, biosdata.Length);
        }

        InitCmos();

        // ディスク: sample.vhdx / sample.vhd があればプライマリ ATA マスタとして接続する(vhdx 優先)。
        // 書き込みは差分 VHDX(sample.avhdx)へ蓄積され、ベースイメージ自体は変更されない。
        var diskImage = sourceArray.FirstOrDefault(File.Exists);
        if (diskImage != null)
        {
            var overlay = Path.ChangeExtension(diskImage, ".avhdx");
            DiskImage.EnsureOverlay(overlay, diskImage);
            Ata = new AtaDevice(new DiskImage(overlay, writable: true));
        }
    }

    // プライマリ ATA チャネルのマスタドライブ(未接続なら null)。
    public AtaDevice Ata;

    // SeaBIOS が CMOS(RTC)経由でメモリ量を検出できるよう、メモリサイズレジスタを設定する。
    //   0x15/0x16: ベースメモリ(KB)          … 640KB
    //   0x17/0x18, 0x30/0x31: 1-16MB の拡張メモリ(KB、最大15MB)
    //   0x34/0x35: 16MB超のメモリ(64KB単位)
    private void InitCmos()
    {
        int baseKB = 640;
        int extKB = Math.Min((RamSize - 0x100000) / 1024, 15 * 1024); // 1-16MB窓(KB)
        int ext64 = RamSize > 0x1000000 ? (RamSize - 0x1000000) / (64 * 1024) : 0; // 16MB超(64KB単位)

        Cmos[0x15] = (byte)baseKB; Cmos[0x16] = (byte)(baseKB >> 8);
        Cmos[0x17] = (byte)extKB; Cmos[0x18] = (byte)(extKB >> 8);
        Cmos[0x30] = (byte)extKB; Cmos[0x31] = (byte)(extKB >> 8);
        Cmos[0x34] = (byte)ext64; Cmos[0x35] = (byte)(ext64 >> 8);
    }

    // 名前は歴史的経緯で OneMegaMemory_ のままだが、実サイズは RamSize。
    public byte[] OneMegaMemory_ = new byte[RamSize];

    public byte[] IoPort = new byte[0x10000];

    // CMOS/RTC: 0x70 でインデックス選択、0x71 でデータ read/write。
    public byte[] Cmos = new byte[128];
    public int CmosIndex;

    // 仮想 8254 PIT(チャネル0)。実時間を持たないため、カウンタをラッチのたびに
    // 減算して単調に時刻が進むようにする。SeaBIOS は下向きカウンタからラップを
    // 検出して 32bit の単調時刻を作るので、これで遅延ループが完了する。
    // ページング変換状態(CPU の CR0.PG/CR3 のミラー。EnvSyncPaging で同期)。
    // TLB は直結マップ(tag = 0x80000000 | vpn、0 は無効)。読み取り用と、
    // Dirty ビット設定済みを保証する書き込み用を分けて持つ。
    // いずれも過渡状態なのでスナップショットには保存しない(復元時に再同期される)。
    public bool PagingOn;
    public bool PaeOn;
    public bool WpOn;
    public uint Cr3Base;

    // 物理アドレス書き込みウォッチポイント(デバッグ用)。WatchLo<=phys<WatchHi の
    // 書き込みで WatchTriggered を立て、Runner が原因命令の EIP を出力する。
    public uint WatchLo, WatchHi;
    public bool WatchTriggered;

    // プロテクトモード(ページング有効)割り込み配送のログ(デバッグ用、null で無効)。
    public System.Collections.Generic.List<string> IntLog;

    // 物理アドレス範囲への書き込みを値付きでログする(デバッグ用)。
    public uint WLogLo, WLogHi;
    public uint CurEip; // Runner が命令実行前に現在 EIP をセットする(ログの帰属用)
    public System.Collections.Generic.List<string> WriteLog;

    // タイムスタンプカウンタの代用(Runner が毎命令、実行済み命令数を書き込む)。
    public ulong Tsc;

    // MSR の汎用ストア(RDMSR/WRMSR)。未書き込みは 0 として読める。
    public Dictionary<uint, ulong> Msrs = new();

    // デバッグレジスタ DR0-DR7(保持のみ。ブレークポイント機能はなし)。
    // 過渡的な診断用途のためスナップショットには保存しない。
    public uint[] Dr = new uint[8];

    // 8259 PIC の状態。ベクタベース(ICW2)とマスク(OCW1)のみ。
    // BIOS 既定はマスタ=0x08(タイマは INT 08h)。Linux は再プログラムする。
    public byte PicMasterBase = 0x08, PicSlaveBase = 0x70;
    public byte PicMasterMask, PicSlaveMask;
    public int PicMasterInit, PicSlaveInit;   // ICW シーケンス位置(0=通常)
    public bool PicMasterIcw4, PicSlaveIcw4;
    // in-service ビット。IRQ 配送で立ち、ハンドラの EOI(OCW2)で降りる。
    // これが立っている間は同じ IRQ を再配送しない(多重ネスト=割り込みストーム防止)。
    public byte PicMasterIsr, PicSlaveIsr;
    public const int TlbSize = 4096;
    public uint[] TlbTagR = new uint[TlbSize], TlbPhysR = new uint[TlbSize];
    public uint[] TlbTagW = new uint[TlbSize], TlbPhysW = new uint[TlbSize];
    public void FlushTlb()
    {
        Array.Clear(TlbTagR);
        Array.Clear(TlbTagW);
    }

    public ushort PitCounter = 0xFFFF;
    public ushort PitLatched = 0xFFFF;
    public int PitReadPhase; // 0=下位バイト, 1=上位バイト

    // PIT チャネル2 + システム制御ポートB(0x61)。TSC/遅延校正で使われる。
    // 実時間を持たないため、ゲート有効かつ ch2 プログラム済みなら数回の 0x61 読みで
    // OUT2(bit5)を立てて校正ウェイトを終わらせる。値の正確さより「ループが抜けること」を優先。
    public byte Port61;          // 0x61 の書き込み値(bit0=ch2ゲート, bit1=スピーカ)
    public byte Port61Refresh;   // bit4 のリフレッシュトグル
    public ushort Pit2Counter = 0xFFFF;
    public int Pit2WritePhase;   // 0x42 書き込み位相(0=lo,1=hi)
    public int Pit2ReadPhase;    // 0x42 読み出し位相
    public bool Pit2Armed;       // ch2 がプログラムされゲート有効
    public int Pit2Wait;         // OUT2 を立てるまでの残り 0x61 読み回数
    // リードバックでラッチされたステータスバイトが未読かどうか。
    // 意図的にスナップショットへは保存しない(過渡状態であり、保存形式を変えると
    // 既存チェックポイントから --resume できなくなるため。再開後は次の OUT 0x43 で再設定される)。
    public bool PitStatusPending;
    private static readonly string[] sourceArray = ["sample.vhdx", "sample.vhd"];

    // スナップショット保存/復元。ディスクの中身(DiskImage)は書き込みのたびに
    // ファイルへ反映済みなので、ここでは RAM/IOポート/CMOS/PIT/ATA レジスタのみを扱う。
    public void SaveState(BinaryWriter w)
    {
        w.Write(OneMegaMemory_);
        w.Write(IoPort);
        w.Write(Cmos);
        w.Write(CmosIndex);
        w.Write(PitCounter);
        w.Write(PitLatched);
        w.Write(PitReadPhase);
        w.Write(Ata != null);
        Ata?.SaveState(w);
    }

    public void LoadState(BinaryReader r)
    {
        r.BaseStream.ReadExactly(OneMegaMemory_);
        r.BaseStream.ReadExactly(IoPort);
        r.BaseStream.ReadExactly(Cmos);
        CmosIndex = r.ReadInt32();
        PitCounter = r.ReadUInt16();
        PitLatched = r.ReadUInt16();
        PitReadPhase = r.ReadInt32();
        if (r.ReadBoolean() && Ata != null)
            Ata.LoadState(r);
    }
}
