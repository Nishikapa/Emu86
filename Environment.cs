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

    static public (bool isMem, uint addr) GetMemoryAddr(ushort segment, ushort offset) =>
        (true, (uint)(0x10 * segment + offset));

    // コードフェッチ用アドレス。
    // 32ビットコードセグメント実行中はフラットモデル(ベース0)を仮定してeipをそのまま使う。
    static public (bool isMem, uint addr) GetCodeAddr(CPU cpu) =>
        cpu.code32 ? (true, cpu.eip) : GetMemoryAddr(cpu.cs, cpu.ip);

    /// Reg /////////////////////////////////////
    static private Accessor<CPU, byte>[] ArrayReg8 =>
    [
        _al, _cl, _dl, _bl,
        _ah, _ch, _dh, _bh
    ];
    static private Accessor<CPU, ushort>[] ArrayReg16 =>
    [
        _ax, _cx, _dx, _bx,
        _sp, _bp, _si, _di
    ];
    static private Accessor<CPU, uint>[] ArrayReg32 =>
    [
        _eax, _ecx, _edx, _ebx,
        _esp, _ebp, _esi, _edi
    ];
    static private Accessor<CPU, ushort>[] ArraySreg =>
    [
        _es, _cs, _ss, _ds, _fs, _gs
    ];

    static private Func<int, Func<CPU, ushort>> GetSReg3 => EnvGetDataFromCPU(ArraySreg);
    static public Func<byte, Func<int, Func<CPU, CPU>>> EnvSetRegData8 => EnvSetDataFromCPU(ArrayReg8);
    static public Func<ushort, Func<int, Func<CPU, CPU>>> EnvSetRegData16 => EnvSetDataFromCPU(ArrayReg16);
    static public Func<uint, Func<int, Func<CPU, CPU>>> EnvSetRegData32 => EnvSetDataFromCPU(ArrayReg32);

    static public Func<ushort, Func<int, Func<CPU, CPU>>> EnvSetSReg3 => EnvSetDataFromCPU(ArraySreg);

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

    static public State<Unit> SetRegData(int reg, (int type, byte db, ushort dw, uint dd) data) =>
        SetCpu(EnvSetRegData(data)(reg));

    static public State<ushort> GetRegData16(int reg) =>
        GetDataFromCpu(EnvGetDataFromCPU(ArrayReg16)(reg));

    static public State<uint> GetRegData32(int reg) =>
        GetDataFromCpu(EnvGetDataFromCPU(ArrayReg32)(reg));

    /// Stack ////////////////////////////////////
    // スタックトップの物理アドレス。32ビットコード実行中はフラットモデル(ベース0)で ESP を使う。
    static private (bool isMem, uint addr) EnvGetStackAddr(CPU cpu) =>
        (true, cpu.code32 ? cpu.esp : (uint)(0x10 * cpu.ss + cpu.sp));

    // SP/ESP をデータ長分減らしてからスタックトップへ書き込む（PUSH）。
    static public State<Unit> Push((int type, byte db, ushort dw, uint dd) data) =>
        from cpu0 in GetCpu
        from _1 in cpu0.code32 ?
            _esp.Set((uint)(cpu0.esp - arrLen[data.type])) :
            _sp.Set((ushort)(cpu0.sp - arrLen[data.type]))
        from cpu in GetCpu
        from _2 in SetMemOrRegData(EnvGetStackAddr(cpu), data)
        select Unit.unit;

    // スタックトップから type 長を読み出してから SP/ESP を増やす（POP）。
    static public State<(int type, byte db, ushort dw, uint dd)> Pop(int type) =>
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
    // MOVS: [DS:SI] -> [ES:DI] を 1 要素コピーし、DF に従って SI/DI を増減する。
    static public State<Unit> Movs(bool w) =>
        SetCpu((env, cpu) =>
        {
            var src = GetMemoryAddr(_ds.getter(cpu), _si.getter(cpu)).addr;
            var dst = GetMemoryAddr(_es.getter(cpu), _di.getter(cpu)).addr;
            var size = w ? 2 : 1;

            var bytes = w ? EnvGetMemoryData16(env, src).ToByteArray()
                          : EnvGetMemoryData8(env, src).ToByteArray();
            EnvSetMemoryDatas(env, dst, bytes);

            var delta = _df.getter(cpu) ? -size : size;
            cpu = _si.setter(cpu)((ushort)(_si.getter(cpu) + delta));
            cpu = _di.setter(cpu)((ushort)(_di.getter(cpu) + delta));
            return cpu;
        });

    // STOS: AL/AX -> [ES:DI]、DF に従って DI を増減する。
    static public State<Unit> Stos(bool w) =>
        SetCpu((env, cpu) =>
        {
            var dst = GetMemoryAddr(_es.getter(cpu), _di.getter(cpu)).addr;
            var size = w ? 2 : 1;

            var bytes = w ? _ax.getter(cpu).ToByteArray() : _al.getter(cpu).ToByteArray();
            EnvSetMemoryDatas(env, dst, bytes);

            var delta = _df.getter(cpu) ? -size : size;
            cpu = _di.setter(cpu)((ushort)(_di.getter(cpu) + delta));
            return cpu;
        });

    // LODS: [DS:SI] -> AL/AX、DF に従って SI を増減する。
    static public State<Unit> Lods(bool w) =>
        SetCpu((env, cpu) =>
        {
            var src = GetMemoryAddr(_ds.getter(cpu), _si.getter(cpu)).addr;
            var size = w ? 2 : 1;

            cpu = w ? _ax.setter(cpu)(EnvGetMemoryData16(env, src))
                    : _al.setter(cpu)(EnvGetMemoryData8(env, src));

            var delta = _df.getter(cpu) ? -size : size;
            cpu = _si.setter(cpu)((ushort)(_si.getter(cpu) + delta));
            return cpu;
        });

    // DF に従って SI/DI を size 分だけ増減する補助。
    static State<Unit> AdvanceSiDi(bool w, bool si, bool di) =>
        SetCpu(cpu =>
        {
            var delta = _df.getter(cpu) ? -(w ? 2 : 1) : (w ? 2 : 1);
            if (si) cpu = _si.setter(cpu)((ushort)(_si.getter(cpu) + delta));
            if (di) cpu = _di.setter(cpu)((ushort)(_di.getter(cpu) + delta));
            return cpu;
        });

    // CMPS: [DS:SI] - [ES:DI] でフラグを更新（結果は破棄）し、SI/DI を増減する。
    static public State<Unit> Cmps(bool w) =>
        from vals in GetDataFromEnvCpu((env, cpu) =>
        {
            var src = GetMemoryAddr(_ds.getter(cpu), _si.getter(cpu)).addr;
            var dst = GetMemoryAddr(_es.getter(cpu), _di.getter(cpu)).addr;
            return w
                ? (s: (uint)EnvGetMemoryData16(env, src), d: (uint)EnvGetMemoryData16(env, dst))
                : (s: (uint)EnvGetMemoryData8(env, src), d: (uint)EnvGetMemoryData8(env, dst));
        })
        from _f in w ? update_eflags_sub((ushort)vals.s, (ushort)vals.d)
                     : update_eflags_sub((byte)vals.s, (byte)vals.d)
        from _adv in AdvanceSiDi(w, si: true, di: true)
        select Unit.unit;

    // BT系: r/m の bit 番目を CF にコピーし、op に応じて値を変更して書き戻す。
    //   op: 0=BT(変更なし) 1=BTS(セット) 2=BTR(クリア) 3=BTC(反転)
    static public State<Unit> BitTest((bool isMem, uint addr) addr, (int type, byte db, ushort dw, uint dd) data, int bit, int op) =>
        from _f in _cf.Set(((data.type == 1 ? data.dw : data.dd) & (1u << (bit & (data.type == 1 ? 15 : 31)))) != 0)
        from _w in op == 0
            ? Unit.unit.ToState()
            : SetMemOrRegData(addr, BitModify(data, bit, op))
        select Unit.unit;

    static (int type, byte db, ushort dw, uint dd) BitModify((int type, byte db, ushort dw, uint dd) data, int bit, int op)
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
                  from v in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData16(env, GetMemoryAddr(cpu.ss, cpu.bp).addr))
                  from _p in Push16(v)
                  select Unit.unit
                : Push16(frame))
            .Sequence()
            .Ignore();

    // INT vector: リアルモード割り込み。FLAGS,CS,IP を push し、
    //   IVT(物理 vector*4)から CS:IP を読んでジャンプする。
    static public State<Unit> Interrupt(int vector) =>
        from fl in GetDataFromCpu(cpu => (ushort)cpu.eflags)
        from _1 in Push16(fl)
        from cs in GetSRegData(1)
        from _2 in Push16(cs)
        from ip in GetDataFromCpu(cpu => cpu.ip)
        from _3 in Push16(ip)
        from newip in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData16(env, (uint)(vector * 4)))
        from newcs in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData16(env, (uint)(vector * 4 + 2)))
        from _4 in _ip.Set(newip)
        from _5 in _cs.Set(newcs)
        select Unit.unit;

    // IRET: IP, CS, FLAGS を pop して割り込みから復帰する。
    static public State<Unit> Iret =>
        from ip in Pop16
        from _1 in _ip.Set(ip)
        from cs in Pop16
        from _2 in _cs.Set(cs)
        from fl in Pop16
        from _3 in SetCpu(cpu => { cpu.eflags = (cpu.eflags & 0xFFFF0000) | fl; return cpu; })
        select Unit.unit;

    // XLAT: AL <- [DS:BX + AL]（バイト変換テーブル参照）。
    static public State<Unit> Xlat =>
        SetCpu((env, cpu) =>
        {
            var addr = GetMemoryAddr(cpu.ds, (ushort)(cpu.bx + cpu.al)).addr;
            return _al.setter(cpu)(EnvGetMemoryData8(env, addr));
        });

    // moffs の物理アドレス。32ビットコードはフラットモデル(ベース0)で offset をそのまま使う。
    static uint EnvMoffsAddr(CPU cpu, uint offset) =>
        cpu.code32 ? offset : GetMemoryAddr(cpu.ds, (ushort)offset).addr;

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
            var dst = GetMemoryAddr(_es.getter(cpu), _di.getter(cpu)).addr;
            return w
                ? (a: (uint)_ax.getter(cpu), m: (uint)EnvGetMemoryData16(env, dst))
                : (a: (uint)_al.getter(cpu), m: (uint)EnvGetMemoryData8(env, dst));
        })
        from _f in w ? update_eflags_sub((ushort)vals.a, (ushort)vals.m)
                     : update_eflags_sub((byte)vals.a, (byte)vals.m)
        from _adv in AdvanceSiDi(w, si: false, di: true)
        select Unit.unit;

    // セグメントレジスタ(ES/CS/SS/DS/FS/GS)を reg 番号で読み出す。
    static public State<ushort> GetSRegData(int reg) =>
        GetDataFromCpu(GetSReg3(reg));

    static public State<(int type, byte db, ushort dw, uint dd)> GetRegData(int reg, int type) =>
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

    static public State<Unit> SetSReg3(int reg, (int type, byte db, ushort dw, uint dd) data) =>
        SetCpu(
            cpu =>
            {
                var (type, db, dw, dd) = data;
                return type switch
                {
                    0 or 1 => EnvSetSReg3(dw)(reg)(cpu),
                    // MOV Sreg, r/m は常に下位16ビットのみロードする
                    2 => EnvSetSReg3((ushort)dd)(reg)(cpu),
                    _ => throw new Exception(),
                };
            }
        );

    /// MemReg //////////////////////////////////
    static private (bool isMem, uint addr, int inc) EnvGetMemOrRegAddr16_(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp)
    {
        // 32ビットコード実行中はフラットモデル(ベース0)を仮定する
        var segment_base =
            cpu.code32 ? 0 :
            segment == default(ushort?) ?
            (uint)cpu.ds * 0x10 :
            (uint)segment * 0x10;

        var ss_base = cpu.code32 ? 0 : (uint)cpu.ss * 0x10;

        switch (mod)
        {
            case 0:
                {
                    (Func<uint> func, int inc)[] array =
                    [
                        (() => segment_base + cpu.bx + cpu.si, 0),                                              // DS:[BX+SI]
                        (() => segment_base + cpu.bx + cpu.di, 0),                                              // DS:[BX+DI]
                        (() => segment_base + cpu.bp + cpu.si, 0),                                              // DS:[BP+SI]
                        (() => segment_base + cpu.bp + cpu.di, 0),                                              // DS:[BP+DI]
                        (() => segment_base + cpu.si, 0),                                                       // DS:[SI]
                        (() => segment_base + cpu.di, 0),                                                       // DS:[DI]
                        (() => segment_base + (uint)disp.ElementAt(0) + 0x100 * (uint)disp.ElementAt(1), 2),    // DS:[d16]
                        (() => segment_base + cpu.bx, 0),                                                       // DS:[BX]
                    ];
                    var (func, inc) = array[rm];
                    return (true, func(), inc);
                }
            case 1:
                {
                    var d8 = (int)(sbyte)disp.ElementAt(0);
                    Func<uint>[] array =
                    [
                        () => (uint)(segment_base + cpu.bx + cpu.si + d8),   // DS:[BX+SI+d8]
                        () => (uint)(segment_base + cpu.bx + cpu.di + d8),   // DS:[BX+DI+d8]
                        () => (uint)(segment_base + cpu.bp + cpu.si + d8),   // DS:[BP+SI+d8]
                        () => (uint)(segment_base + cpu.bp + cpu.di + d8),   // DS:[BP+DI+d8]
                        () => (uint)(segment_base + cpu.si + d8),            // DS:[SI+d8]
                        () => (uint)(segment_base + cpu.di + d8),            // DS:[DI+d8]
                        () => (uint)(ss_base + cpu.bp + d8),                 // SS:[bp+d8]
                        () => (uint)(segment_base + cpu.di + d8),            // DS:[DI+d8]
                    ];
                    return (true, array[rm](), 1);
                }
            case 2:
                {
                    var d16 = (uint)disp.ElementAt(0) + 0x100 * (uint)disp.ElementAt(1);
                    Func<uint>[] array =
                    [
                        () => (uint)(segment_base + cpu.bx + cpu.si + d16),     // DS:[BX+SI+d16]
                        () => (uint)(segment_base + cpu.bx + cpu.di + d16),     // DS:[BX+DI+d16]
                        () => (uint)(segment_base + cpu.bp + cpu.si + d16),     // DS:[BP+SI+d16]
                        () => (uint)(segment_base + cpu.bp + cpu.di + d16),     // DS:[BP+DI+d16]
                        () => (uint)(segment_base + cpu.si + d16),              // DS:[SI+d16]
                        () => (uint)(segment_base + cpu.di + d16),              // DS:[DI+d16]
                        () => (uint)(ss_base + cpu.bp + d16),                   // SS:[bp+d16]
                        () => (uint)(segment_base + cpu.di + d16),              // DS:[DI+d16]
                    ];
                    return (true, array[rm](), 2);
                }
            case 3:
                return (false, (uint)rm, 0);
            default:
                throw new Exception();
        }
    }
    static private (bool isMem, uint addr, int inc) EnvGetMemOrRegAddr32_(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp)
    {
        // 32ビットコード実行中はフラットモデル(ベース0)を仮定する
        var segment_base =
            cpu.code32 ? 0 :
            segment == default(ushort?) ?
            (uint)cpu.ds * 0x10 :
            (uint)segment * 0x10;

        var ss_base = cpu.code32 ? 0 : (uint)cpu.ss * 0x10;

        int inc = 0;

        switch (mod)
        {
            case 0:
                {
                    uint addr = 0;
                    switch (rm)
                    {
                        case 0: // DS:[EAX]
                            addr = segment_base + cpu.eax;
                            break;
                        case 1: // DS:[ECX]
                            addr = segment_base + cpu.ecx;
                            break;
                        case 2: // DS:[EDX]
                            addr = segment_base + cpu.edx;
                            break;
                        case 3: // DS:[EBX]
                            addr = segment_base + cpu.ebx;
                            break;
                        case 4: // SIB
                            {
                                var sib = disp.ElementAt(0);

                                var ss = (sib >> 6) & 0x3;
                                var indexf = (sib >> 3) & 0x7;
                                var basef = sib & 0x7;

                                var index_ = EnvGetIndexRegData32(cpu, indexf);

                                // base=5 かつ mod=0 は「ベースレジスタなし、SIB の後に disp32 が続く」特殊ケース
                                if (basef == 5)
                                {
                                    addr = (uint)(segment_base + disp.Skip(1).ToUint32() + (1 << ss) * index_);
                                    inc = 5; // SIB(1) + disp32(4)
                                }
                                else
                                {
                                    var base_ = EnvGetDataFromCPU(ArrayReg32)(basef)(cpu);
                                    addr = (uint)(segment_base + base_ + (1 << ss) * index_);
                                    inc = 1;
                                }
                            }
                            break;
                        case 5: // DS:[d32]
                            {
                                addr = segment_base + disp.ToUint32();
                                inc = 4;
                            }
                            break;
                        case 6: // DS:[ESI]
                            addr = segment_base + cpu.esi;
                            break;
                        case 7: // DS:[EDI]
                            addr = segment_base + cpu.edi;
                            break;
                        default:
                            throw new Exception();
                    }
                    return (true, addr, inc);
                }
            case 1:
                {
                    uint addr = 0;
                    var d8 = (int)(sbyte)disp.ElementAt(0);
                    switch (rm)
                    {
                        case 0: // DS:[EAX+d8]
                            addr = (uint)(segment_base + cpu.eax + d8);
                            break;
                        case 1: // DS:[ECX+d8]
                            addr = (uint)(segment_base + cpu.ecx + d8);
                            break;
                        case 2: // DS:[EDX+d8]
                            addr = (uint)(segment_base + cpu.edx + d8);
                            break;
                        case 3: // DS:[EBX+d8]
                            addr = (uint)(segment_base + cpu.ebx + d8);
                            break;
                        case 4: // SS:[EBP+d8]
                            {
                                var sib = (int)(sbyte)disp.ElementAt(0);

                                var ss = (sib >> 6) & 0x3;
                                var indexf = (sib >> 3) & 0x7;
                                var basef = sib & 0x7;

                                var base_ = EnvGetDataFromCPU(ArrayReg32)(basef)(cpu);

                                var index_ = EnvGetIndexRegData32(cpu, indexf);

                                d8 = (int)(sbyte)disp.ElementAt(1);

                                addr = (uint)(base_ + (1 << ss) * index_ + d8);

                                return (true, addr, 2);
                            }
                        case 5: // SS:[EBP+d8]
                            addr = (uint)(ss_base + cpu.ebp + d8);
                            break;
                        case 6: // DS:[ESI+d8]
                            addr = (uint)(segment_base + cpu.esi + d8);
                            break;
                        case 7: // DS:[EDI+d8]
                            addr = (uint)(segment_base + cpu.edi + d8);
                            break;
                        default:
                            throw new Exception();
                    }
                    return (true, addr, 1);
                }
            case 2:
                {
                    uint addr = 0;

                    var d32 = disp.ToUint32();

                    switch (rm)
                    {
                        case 0: // DS:[EAX+d32]
                            addr = (uint)(segment_base + cpu.eax + d32);
                            break;
                        case 1: // DS:[ECX+d32]
                            addr = (uint)(segment_base + cpu.ecx + d32);
                            break;
                        case 2: // DS:[EDX+d32]
                            addr = (uint)(segment_base + cpu.edx + d32);
                            break;
                        case 3: // DS:[EBX+d32]
                            addr = (uint)(segment_base + cpu.ebx + d32);
                            break;
                        case 4: // SS:[ESP+d32]
                            throw new NotImplementedException();
                        case 5: // SS:[EBP+d32]
                            addr = (uint)(ss_base + cpu.ebp + d32);
                            break;
                        case 6: // DS:[ESI+d16]
                            addr = (uint)(segment_base + cpu.esi + d32);
                            break;
                        case 7: // DS:[EDI+d32]
                            addr = (uint)(segment_base + cpu.edi + d32);
                            break;
                        default:
                            throw new Exception();
                    }
                    return (true, addr, 4);
                }
            case 3:
                return (false, (uint)rm, 0);
            default:
                throw new Exception();
        }
    }
    static public State<(bool isMem, uint addr)> GetMemOrRegAddr(int mod, int rm) =>
        from data in GetDataFromEnvCpu(
            (env, cpu) =>
            // 32ビットコードではデフォルトが32ビットModRMになり、address_size_prefix(0x67)で反転する
            (cpu.code32 != cpu.address_size_prefix) ?
            EnvGetMemOrRegAddr32_(cpu, mod, rm, cpu.cs, EnvGetMemoryDatas(env, GetCodeAddr(cpu).addr)) :
            EnvGetMemOrRegAddr16_(cpu, mod, rm, cpu.cs, EnvGetMemoryDatas(env, GetCodeAddr(cpu).addr))
        )
        from _ in IpInc(data.inc)
        select (data.isMem, data.addr);

    static public State<byte> GetMemOrRegData8((bool isMem, uint addr) t) =>
        GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData8(env, t.addr) : EnvGetDataFromCPU(ArrayReg8)((int)t.addr)(cpu));

    static public State<ushort> GetMemOrRegData16((bool isMem, uint addr) t) =>
        GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData16(env, t.addr) : EnvGetDataFromCPU(ArrayReg16)((int)t.addr)(cpu));

    static public State<uint> GetMemOrRegData32((bool isMem, uint addr) t) =>
        GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData32(env, t.addr) : EnvGetDataFromCPU(ArrayReg32)((int)t.addr)(cpu));

    static Func<int, Func<CPU, CPU>> EnvSetRegData((int type, byte db, ushort dw, uint dd) data) =>
        SetTypeData<Func<int, Func<CPU, CPU>>>
        (
            data,
            EnvSetRegData8,
            EnvSetRegData16,
            EnvSetRegData32
        );

    static Action<EmuEnvironment, uint> EnvSetMemoryData((int type, byte db, ushort dw, uint dd) data) =>
        SetTypeData<Action<EmuEnvironment, uint>>
        (
            data,
            db => (env, addr) => { env.OneMegaMemory_[addr] = db; },
            dw => (env, addr) => { EnvSetMemoryDatas(env, addr, dw.ToByteArray()); },
            dd => (env, addr) => { EnvSetMemoryDatas(env, addr, dd.ToByteArray()); }
        );

    static public State<Unit> SetMemOrRegData(
        (bool isMem, uint addr) t,
        (int type, byte db, ushort dw, uint dd) data) =>
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

    static public State<(int type, byte db, ushort dw, uint dd)> GetMemOrRegData(
        (bool isMem, uint addr) t,
        bool w) =>
        from type in OperandType(w)
        from ret in Choice(
            type,
            GetMemOrRegData8(t),
            GetMemOrRegData16(t),
            GetMemOrRegData32(t)
        )
        select ret;

    static public State<((int type, byte db, ushort dw, uint dd) data, (bool isMem, uint addr) input)> GetMemOrRegData(
        ushort segment,
        ushort offset,
        bool w) =>
        from addr in GetMemoryAddr(segment, offset).ToState()
        from data in GetMemOrRegData(addr, w)
        select (data, addr);

    static public State<((int type, byte db, ushort dw, uint dd) data, (bool isMem, uint addr) input)> GetMemOrRegData(
        int mod,
        int rm,
        bool w) =>
        from addr in GetMemOrRegAddr(mod, rm)
        from data in GetMemOrRegData(addr, w)
        select (data, addr);

    ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    static private uint EnvGetIndexRegData32(CPU cpu, int reg) =>
        new uint[]
        {
            cpu.eax,
            cpu.ecx,
            cpu.edx,
            cpu.ebx,
            0,
            cpu.ebp,
            cpu.esi,
            cpu.edi,
        }[reg];

    static private Func<T, Func<int, Func<CPU, CPU>>> EnvSetDataFromCPU<T>(Accessor<CPU, T>[] array) =>
        data => reg => cpu => array[reg].setter(cpu)(data);

    static private void EnvSetMemoryDatas(EmuEnvironment env, uint addr, byte[] data)
    {
        for (int i = 0; i < data.Length; ++i)
        {
            env.OneMegaMemory_[addr + i] = data[i];
        }
    }

    static private byte EnvGetMemoryData8(EmuEnvironment env, uint addr) => env.OneMegaMemory_[addr];
    static private ushort EnvGetMemoryData16(EmuEnvironment env, uint addr) => BitConverter.ToUInt16(env.OneMegaMemory_, (int)addr);
    static private uint EnvGetMemoryData32(EmuEnvironment env, uint addr) => BitConverter.ToUInt32(env.OneMegaMemory_, (int)addr);
}

public class EmuEnvironment
{
    public EmuEnvironment()
    {
        // bios: 埋め込みリソース "bios.bin" が存在すれば 1MB メモリ空間の末尾に配置する。
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
    }

    public byte[] OneMegaMemory_ = new byte[1024 * 1024];

    public byte[] IoPort = new byte[0x10000];
}
