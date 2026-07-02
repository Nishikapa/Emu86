using System.Reflection;
using static Emu86.CPU;

namespace Emu86;

static public partial class Ext
{
    /// Mem /////////////////////////////////////
    // ArraySegment を使い、LINQ Skip の O(addr) 走査を避ける。
    static public IEnumerable<byte> EnvGetMemoryDatas(EmuEnvironment env, uint addr) =>
        new ArraySegment<byte>(env.OneMegaMemory_, (int)addr, env.OneMegaMemory_.Length - (int)addr);

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

    static public State<Unit> SetCrReg(int reg, uint data) =>
        Choice(
            reg,
            (0, _cr0),
            (2, _cr2),
            (3, _cr3)
        ).Set(data);

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
            uint val = 0;
            for (int i = 0; i < size; i++) val |= (uint)EnvInPort(env, cpu.dx + i) << (8 * i);
            EnvWriteN(env, StrDst(cpu), size, val);
            return StrAdvance(cpu, size, si: false, di: true);
        });

    // OUTS: [DS:SI] から 1 要素読み、ポート DX へ出力し、DF に従って SI を増減する。
    static public State<Unit> Outs(bool w) =>
        SetCpu((env, cpu) =>
        {
            int size = StrSize(cpu, w);
            uint val = EnvReadN(env, StrSrc(cpu), size);
            for (int i = 0; i < size; i++) EnvOutPort(env, cpu.dx + i, (byte)(val >> (8 * i)));
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

    // BSF/BSR: r/m16 内のセットビット位置を求めて reg へ書く。
    //   forward=true で最下位(BSF)、false で最上位(BSR)。
    //   ソースが 0 なら ZF=1 とし、結果は書き込まない（未定義）。
    static public State<Unit> BitScan(int reg, ushort src, bool forward) =>
        src == 0
            ? _zf.Set(true)
            : from _z in _zf.Set(false)
              from _w in SetRegData16(reg, (ushort)BitScanIndex(src, forward))
              select Unit.unit;

    static int BitScanIndex(ushort src, bool forward)
    {
        if (forward)
        {
            for (int i = 0; i < 16; i++)
                if ((src & (1 << i)) != 0) return i;
        }
        else
        {
            for (int i = 15; i >= 0; i--)
                if ((src & (1 << i)) != 0) return i;
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

    // INT vector: リアルモード割り込み。FLAGS,CS,IP を push し、IF/TF をクリアして
    //   IVT(物理 vector*4)から CS:IP を読んでジャンプする。
    static public State<Unit> Interrupt(int vector) =>
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

    // IRET: IP, CS, FLAGS を pop して割り込みから復帰する。
    static public State<Unit> Iret =>
        from ip in Pop16
        from _1 in _ip.Set(ip)
        from cs in Pop16
        from _2 in LoadSReg(1, cs)
        from fl in Pop16
        from _3 in SetCpu(cpu => { cpu.eflags = (cpu.eflags & 0xFFFF0000) | fl; return cpu; })
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
        GetDataFromCpu(GetTypeData_<CPU>(
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
            (3, Get(_cr3))
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

    // ModRM 16ビットアドレッシング。実効アドレスと追加で消費した disp バイト数を返す。
    static private (bool isMem, uint addr, int inc) EnvGetMemOrRegAddr16_(CPU cpu, int mod, int rm, IEnumerable<byte> disp)
    {
        if (mod == 3)
            return (false, (uint)rm, 0);

        var (segment_base, ss_base) = EnvSegBases(cpu);

        if (mod == 0 && rm == 6) // [d16]
            return (true, segment_base + disp.ToUint16(), 2);

        // rm ごとのレジスタ組み合わせ(BP を含む形は既定セグメントが SS)
        uint[] regsum =
        [
            (uint)(cpu.bx + cpu.si), (uint)(cpu.bx + cpu.di), (uint)(cpu.bp + cpu.si), (uint)(cpu.bp + cpu.di),
            cpu.si, cpu.di, cpu.bp, cpu.bx
        ];
        var base_ = (rm is 2 or 3 or 6 ? ss_base : segment_base) + regsum[rm];

        return mod switch
        {
            0 => (true, base_, 0),
            1 => (true, (uint)(base_ + (sbyte)disp.ElementAt(0)), 1),
            _ => (true, base_ + disp.ToUint16(), 2),
        };
    }
    // ModRM 32ビットアドレッシング(SIB 対応)。実効アドレスと追加で消費した disp バイト数を返す。
    static private (bool isMem, uint addr, int inc) EnvGetMemOrRegAddr32_(CPU cpu, int mod, int rm, IEnumerable<byte> disp)
    {
        if (mod == 3)
            return (false, (uint)rm, 0);

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
                (0, 5) => (true, segment_base + scaled + disp.Skip(1).ToUint32(), 5),
                (0, _) => (true, sb + scaled + EnvGetReg32(cpu, basef), 1),
                (1, _) => (true, (uint)(sb + scaled + EnvGetReg32(cpu, basef) + (sbyte)disp.ElementAt(1)), 2),
                _ => (true, sb + scaled + EnvGetReg32(cpu, basef) + disp.Skip(1).ToUint32(), 5),
            };
        }

        if (mod == 0 && rm == 5) // [d32]
            return (true, segment_base + disp.ToUint32(), 4);

        // ベースレジスタ(EBP ベースは SS)
        var base_ = (rm == 5 ? ss_base : segment_base) + EnvGetReg32(cpu, rm);
        return mod switch
        {
            0 => (true, base_, 0),
            1 => (true, (uint)(base_ + (sbyte)disp.ElementAt(0)), 1),
            _ => (true, base_ + disp.ToUint32(), 4),
        };
    }

    static public State<MemAddr> GetMemOrRegAddr(int mod, int rm) =>
        from data in GetDataFromEnvCpu(
            (env, cpu) =>
            // 32ビットコードではデフォルトが32ビットModRMになり、address_size_prefix(0x67)で反転する
            (cpu.code32 != cpu.address_size_prefix) ?
            EnvGetMemOrRegAddr32_(cpu, mod, rm, EnvGetMemoryDatas(env, GetCodeAddr(cpu).addr)) :
            EnvGetMemOrRegAddr16_(cpu, mod, rm, EnvGetMemoryDatas(env, GetCodeAddr(cpu).addr))
        )
        from _ in IpInc(data.inc)
        select (data.isMem, data.addr);

    static public State<byte> GetMemOrRegData8(MemAddr t) =>
        GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData8(env, t.addr) : EnvGetDataFromCPU(ArrayReg8)((int)t.addr)(cpu));

    static public State<ushort> GetMemOrRegData16(MemAddr t) =>
        GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData16(env, t.addr) : EnvGetDataFromCPU(ArrayReg16)((int)t.addr)(cpu));

    static public State<uint> GetMemOrRegData32(MemAddr t) =>
        GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData32(env, t.addr) : EnvGetDataFromCPU(ArrayReg32)((int)t.addr)(cpu));

    static Func<int, Func<CPU, CPU>> EnvSetRegData(Data data) =>
        SetTypeData<Func<int, Func<CPU, CPU>>>
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
    // CMOS(0x70/0x71)を特別扱いする以外は IoPort 配列を素通しする。
    static public byte EnvInPort(EmuEnvironment env, int port)
    {
        switch (port & 0xFFFF)
        {
            case 0x71: // CMOS データ
                return env.Cmos[env.CmosIndex];
            case 0x40: // PIT チャネル0: ラッチ済み値を下位→上位の順に返す
                byte b = (byte)(env.PitReadPhase == 0 ? env.PitLatched : env.PitLatched >> 8);
                env.PitReadPhase ^= 1;
                return b;
            default:
                return env.IoPort[port & 0xFFFF];
        }
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
                bool latch = (val & 0x30) == 0 || (val & 0xC0) == 0xC0;
                if (latch)
                {
                    env.PitLatched = env.PitCounter;
                    env.PitReadPhase = 0;
                    env.PitCounter -= 0x100; // 経過時間の代用として下向きに減算する
                }
                break;
            case 0x402:
                System.Console.Error.Write((char)val);
                break;
            default:
                env.IoPort[port & 0xFFFF] = val;
                break;
        }
    }

    // 搭載RAM(RamSize)外へのアクセスは未マップ領域として扱う:
    //   読み出しは 0xFF(オープンバス)、書き込みは無視。MMIO 等をエミュレートしない代わりに
    //   クラッシュを避ける。
    static private void EnvWriteByte(EmuEnvironment env, uint addr, byte val)
    {
        if (addr < (uint)env.OneMegaMemory_.Length) env.OneMegaMemory_[addr] = val;
    }

    static private byte EnvGetMemoryData8(EmuEnvironment env, uint addr) =>
        addr < (uint)env.OneMegaMemory_.Length ? env.OneMegaMemory_[addr] : (byte)0xFF;
    static private ushort EnvGetMemoryData16(EmuEnvironment env, uint addr) =>
        addr + 2 <= (uint)env.OneMegaMemory_.Length ? BitConverter.ToUInt16(env.OneMegaMemory_, (int)addr) : (ushort)0xFFFF;
    static private uint EnvGetMemoryData32(EmuEnvironment env, uint addr) =>
        addr + 4 <= (uint)env.OneMegaMemory_.Length ? BitConverter.ToUInt32(env.OneMegaMemory_, (int)addr) : 0xFFFFFFFF;
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
    }

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
    public ushort PitCounter = 0xFFFF;
    public ushort PitLatched = 0xFFFF;
    public int PitReadPhase; // 0=下位バイト, 1=上位バイト
}
