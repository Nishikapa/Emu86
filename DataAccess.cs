using static Emu86.CPU;
using static Emu86.Mmu;
using static Emu86.Conversions;
using static Emu86.CpuStates;
using static Emu86.Fetch;
using static Emu86.AluStates;
using static Emu86.Decoder;

namespace Emu86;

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

    static internal readonly Accessor<CPU, ushort>[] ArraySreg =
    [
        _es, _cs, _ss, _ds, _fs, _gs
    ];

    static internal readonly Accessor<CPU, uint>[] ArraySregBase =
    [
        _es_base, _cs_base, _ss_base, _ds_base, _fs_base, _gs_base
    ];

    static public State<Unit> SetRegData16(int reg, ushort dw) => ArrayReg16[reg].Set(dw);

    static public State<Unit> SetRegData32(int reg, uint dd) => ArrayReg32[reg].Set(dd);

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

    // moffs のメモリアドレス。既定 DS(セグメントオーバーライドで置換)+ オフセット。
    static public State<MemAddr> MoffsAddress(uint offset) =>
        GetDataFromCpu(cpu => (MemAddr)(true, EnvSegBases(cpu).seg + offset));

    // セグメントレジスタ(ES/CS/SS/DS/FS/GS)を reg 番号で読み出す。
    static public State<ushort> GetSRegData(int reg) =>
        GetDataFromCpu(ArraySreg[reg].getter);

    static public State<Data> GetRegData(int reg, int type) =>
        GetDataFromCpu(GetTypeData(
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

    // ModRM の r/m を読む(実効アドレスのデコードを含む。幅は w と実効オペランドサイズから)。
    static public State<Data> GetMemOrRegData(int mod, int rm, bool w) =>
        from addr in GetMemOrRegAddr(mod, rm)
        from data in GetMemOrRegData(addr, w)
        select data;
}
