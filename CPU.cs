using static Emu86.CPU;
using static Emu86.Unit;
using static Emu86.Conversions;
using static Emu86.States;
using static Emu86.Alu;
using static Emu86.Mmu;
using static Emu86.DataAccess;
using static Emu86.Ports;
using static Emu86.CpuStates;

namespace Emu86;

// State で CPU を読み書きする基本操作(SetCpu/GetCpu、Accessor の Get/Set/Modify、EIP の前進、ログ・結果・オペコード列)。
static public class CpuStates
{
    static internal State<T> Get<T>(this Accessor<CPU, T> acc) =>
        GetDataFromCpu(acc.getter);

    static public State<Unit> Set<T>(this Accessor<CPU, T> acc, T value) =>
        SetCpu(cpu => acc.setter(cpu)(value));

    // アクセサが指す値を関数で更新する(CMC の cf = !cf、SP -= n など)。
    static public State<Unit> Modify<T>(this Accessor<CPU, T> acc, Func<T, T> update) =>
        SetCpu(cpu => acc.setter(cpu)(update(acc.getter(cpu))));

    static public State<Unit> IpInc(int inc) =>
        SetCpu(cpu => { cpu.eip = (uint)(cpu.eip + inc); return cpu; });

    static public State<Unit> SetCpu(Func<CPU, CPU> func) =>
        (env, cpu, opcodes) => (true, unit, func(cpu), string.Empty);

    static public State<T> GetDataFromCpu<T>(Func<CPU, T> func) =>
        (env, cpu, opcodes) => (true, func(cpu), cpu, string.Empty);

    static public State<Unit> SetCpu(Func<EmuEnvironment, CPU, CPU> func) =>
        (env, cpu, opcodes) => (true, unit, func(env, cpu), string.Empty);

    static public State<Unit> SetCpu<T>(params (Accessor<CPU, T> acc, T value)[] arr) =>
        (env, cpu, opcodes) =>
        {
            foreach (var item in arr)
                cpu = item.acc.setter(cpu)(item.value);
            return (true, unit, cpu, string.Empty);
        };

    static public readonly State<CPU> GetCpu = GetDataFromEnvCpu((env, cpu) => cpu);

    static public State<T> GetDataFromEnvCpu<T>(Func<EmuEnvironment, CPU, T> func) =>                              //無理
        (env, cpu, ope) => (true, func(env, cpu), cpu, string.Empty);

    static public State<Unit> SetCpu(CPU new_cpu) => (env, cpu, ope) => (true, unit, new_cpu, string.Empty); //無理

    static public State<Unit> SetResult(bool f) => (env, cpu, ope) => (f, unit, cpu, string.Empty);          //無理

    static public State<Unit> SetLog(string log) => (env, cpu, ope) => (true, unit, cpu, log);               //無理

    static public readonly State<byte[]> Opecodes = (env, cpu, ope) => (true, ope, cpu, string.Empty);       //無理
}

// 命令ストリーム(CS:EIP)からの読み取り: オペコード・即値・ModRM(EIP を進める State)。
static public partial class Fetch
{
    static internal readonly int[] arrLen = [1, 2, 4];

    static public State<(Data data, MemAddr input)> GetMemoryDataIp(bool w) =>
        from cpu in GetCpu
        let addr = GetCodeAddr(cpu)
        from data in GetMemOrRegData(addr, w)
        from _ in IpInc(arrLen[data.type])
        select (data, (addr.isMem, addr.addr));

    static public State<Data> GetMemoryDataIp_(int type) => MemoryDataIpByType[type];

    static public State<(int mod, int reg, int rm)> ModRegRm() =>
        from value in GetMemoryDataIp8
        let mod = (value >> 6) & 0x3
        let reg = (value >> 3) & 0x7
        let rm = value & 0x7
        select (mod, reg, rm);

    static public State<IEnumerable<byte>> GetMemoryDataIp(int length) =>
        from data in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryDatas(env, GetCodeAddr(cpu).addr).Take(length))
        from _ in IpInc(data.Count())
        select data;

    // ホットパスで毎命令アクセスされるため、プロパティではなくキャッシュ済みフィールドにする
    // (プロパティだと参照のたびに SelectMany のデリゲート鎖を再構築してしまう)。
    static public readonly State<byte> GetMemoryDataIp8 = (env, cpu, opcodes) =>
    {
        var data = EnvGetMemoryData8(env, GetCodeAddr(cpu).addr);
        cpu.eip += 1;
        return (true, data, cpu, string.Empty);
    };

    static public readonly State<ushort> GetMemoryDataIp16 = (env, cpu, opcodes) =>
    {
        var data = EnvGetMemoryData16(env, GetCodeAddr(cpu).addr);
        cpu.eip += 2;
        return (true, data, cpu, string.Empty);
    };

    static public readonly State<uint> GetMemoryDataIp32 = (env, cpu, opcodes) =>
    {
        var data = EnvGetMemoryData32(env, GetCodeAddr(cpu).addr);
        cpu.eip += 4;
        return (true, data, cpu, string.Empty);
    };

    static readonly State<Data>[] MemoryDataIpByType =
    [
        GetMemoryDataIp8.Select(Conversions.ToTypeData),
        GetMemoryDataIp16.Select(Conversions.ToTypeData),
        GetMemoryDataIp32.Select(Conversions.ToTypeData)
    ];
}

// State で書いた演算とフラグ更新(Calc、Group2 のシフト、条件判定、フラグ更新の State)。
static public class AluStates
{
    static public State<Data> Choice(
        int index, State<byte> dbState, State<ushort> dwState, State<uint> ddState) => index switch
        {
            0 => dbState.Select(Conversions.ToTypeData),
            1 => dwState.Select(Conversions.ToTypeData),
            2 => ddState.Select(Conversions.ToTypeData),
            _ => throw new IndexOutOfRangeException()
        };

    static public Func<T, Data> GetTypeData_<T>(
        int index,
        Func<T, byte> func_db,
        Func<T, ushort> func_dw,
        Func<T, uint> func_dd) => index switch
        {
            0 => value => func_db(value).ToTypeData(),
            1 => value => func_dw(value).ToTypeData(),
            2 => value => func_dd(value).ToTypeData(),
            _ => throw new IndexOutOfRangeException()
        };

    static public State<Data> Calc(Data d1, Data d2, int kind)
    {
        if (d1.type != d2.type)
            throw new Exception();
        return GetDataFromCpu(cpu => CalculateAlu(cpu, d1.type, d1.Value(), d2.Value(), kind).ToTypeData(d1.type));
    }

    // Group2 を実行し、フラグを更新して結果を type タプルで返す。
    static public State<Data> Group2(
        Data data, int count, int kind) =>
        from cf0 in _cf.Get()
        let cnt = count & 0x1F   // 80386 はシフト量を 5bit にマスクする
        let res = ComputeShift(data.Value(), cnt, kind, Bits(data.type), cf0)
        // count==0 のときはフラグを変更しない。シフト系(kind>=4)は ZF/SF も更新する。
        from _f in Unless(cnt == 0, kind >= 4
            ? SetCpu((_cf, res.cf), (_of, res.of), (_zf, res.result == 0), (_sf, (res.result & Msb(data.type)) != 0), (_pf, Par(res.result)))
            : SetCpu((_cf, res.cf), (_of, res.of)))
        select res.result.ToTypeData(data.type);

    static public State<bool> Not(State<bool> s) =>
        s.Select(b => !b);

    static public State<bool> Jcc(int type) =>
        GetDataFromCpu(cpu => EvaluateCondition(cpu, type));

    static public State<Unit> update_eflags(byte value) => update_eflags(value.ToTypeData());

    static public State<Unit> update_eflags(ushort value) => update_eflags(value.ToTypeData());

    static public State<Unit> update_eflags(uint value) => update_eflags(value.ToTypeData());

    static public State<Unit> update_eflags(Data data) =>
        SetCpu(cpu =>
        {
            SetLogicFlags(cpu, data.type, data.Value());
            return cpu;
        });

    static public State<Unit> update_eflags_sub(byte left, byte right) =>
        UpdateSubtractionFlags(0, left, right);

    static public State<Unit> update_eflags_sub(uint left, uint right) =>
        UpdateSubtractionFlags(2, left, right);

    static public State<Unit> update_eflags_sub(ushort left, ushort right) =>
        UpdateSubtractionFlags(1, left, right);

    static State<Unit> UpdateSubtractionFlags(int type, uint left, uint right) =>
        SetCpu(cpu =>
        {
            SetSubtractionFlags(cpu, type, left, right);
            return cpu;
        });

    static public State<Unit> update_eflags_inc(Data data) => update_eflags_incdec(data, +1);

    static public State<Unit> update_eflags_dec(Data data) => update_eflags_incdec(data, -1);

    static State<Unit> update_eflags_incdec(Data data, int delta) =>
        SetCpu(cpu =>
        {
            SetIncDecFlags(cpu, data.type, data.Value(), delta);
            return cpu;
        });
}


public class Accessor<O, P>(Func<O, P> g, Func<O, Func<P, O>> s)
{
    public Func<O, P> getter { get; set; } = g;
    public Func<O, Func<P, O>> setter { get; set; } = s;
}

// CPU 状態。参照型(class)であることに意味がある:
// State モナドの各バインドを CPU の実体コピーではなく参照の受け渡しで通すことで、
// 1命令あたり数十回発生していた大きな構造体コピーをなくしている。
// アクセサの setter は同一インスタンスをその場で書き換えて返す。
public partial class CPU
{
    // セグメントレジスタのセッタは、リアルモードの基底(セレクタ*16)もあわせて更新する。
    // プロテクトモードでは LoadSReg が GDT 記述子から読んだ基底で上書きする(記述子キャッシュ相当)。
    static public readonly Accessor<CPU, ushort> _cs = new(c => c.cs, c => v => { c.cs = v; c.cs_base = (uint)v << 4; return c; });
    static public readonly Accessor<CPU, ushort> _ds = new(c => c.ds, c => v => { c.ds = v; c.ds_base = (uint)v << 4; return c; });
    static public readonly Accessor<CPU, ushort> _es = new(c => c.es, c => v => { c.es = v; c.es_base = (uint)v << 4; return c; });
    static public readonly Accessor<CPU, ushort> _ss = new(c => c.ss, c => v => { c.ss = v; c.ss_base = (uint)v << 4; return c; });
    static public readonly Accessor<CPU, ushort> _fs = new(c => c.fs, c => v => { c.fs = v; c.fs_base = (uint)v << 4; return c; });
    static public readonly Accessor<CPU, ushort> _gs = new(c => c.gs, c => v => { c.gs = v; c.gs_base = (uint)v << 4; return c; });

    public ushort cs { get; private set; }
    public ushort ds { get; private set; }
    public ushort es { get; private set; }
    public ushort ss { get; private set; }
    public ushort fs { get; private set; }
    public ushort gs { get; private set; }

    // 各セグメントの基底物理アドレス(記述子キャッシュ)。
    public uint cs_base, ds_base, es_base, ss_base, fs_base, gs_base;

    static public readonly Accessor<CPU, uint> _cs_base = new(c => c.cs_base, c => v => { c.cs_base = v; return c; });
    static public readonly Accessor<CPU, uint> _ds_base = new(c => c.ds_base, c => v => { c.ds_base = v; return c; });
    static public readonly Accessor<CPU, uint> _es_base = new(c => c.es_base, c => v => { c.es_base = v; return c; });
    static public readonly Accessor<CPU, uint> _ss_base = new(c => c.ss_base, c => v => { c.ss_base = v; return c; });
    static public readonly Accessor<CPU, uint> _fs_base = new(c => c.fs_base, c => v => { c.fs_base = v; return c; });
    static public readonly Accessor<CPU, uint> _gs_base = new(c => c.gs_base, c => v => { c.gs_base = v; return c; });

    static public readonly Accessor<CPU, ushort> _ip = new(c => c.ip, c => v => { c.ip = v; return c; });

    static public readonly Accessor<CPU, uint> _eip = new(c => c.eip, c => v => { c.eip = v; return c; });

    // FastStep(高速コア)が直接書き込むため、セッターは公開している。
    public uint eip { get; set; }
    public ushort ip { get { return (ushort)(this.eip & 0xFFFF); } private set { this.eip = (this.eip & 0xFFFF0000) + value; } }

    // プロテクトモードで32ビットコードセグメント(D=1)を実行中かどうか。
    // CSがプロテクトモードでロードされたときにセットされる。
    public bool code32;

    // 直前の命令が HLT だった(ランナーが見て、配送できる割り込みが無ければ次の割り込みまで仮想時間を進める)。
    // 過渡状態なのでスナップショットには含めない。
    public bool Halted;

    // スタックが32ビット(SS の B ビット=1)かどうか。スタック操作(PUSH/POP/CALL/RET/
    // 割り込み)で SP/ESP のどちらを使うかは CS.D ではなく SS.B で決まる。
    // 例: PnP BIOS は 16ビットCS + 32ビットSS で動くため両者が食い違う。
    public bool stack32;
    // タスクレジスタ / LDT レジスタのセレクタ。タスク切替や LDT 自体は未実装だが、
    // LTR/LLDT で保持し STR/SLDT で返す(NT カーネルは STR→GDT から TSS の番地を求める)。
    public ushort tr;
    public ushort ldtr;

    // x87 FPU の状態。ST(i) = fpu_st[(fpu_top + i) & 7]。
    // 値は C# の double で保持する(実機の内部 80 ビットではなく倍精度)。
    // fpu_sw のうち TOP フィールド(bit11-13)は fpu_top から合成する(FpuSw)。
    // fpu_valid は物理レジスタごとの「空でない」ビットマスク(タグワードの元)。
    public ushort fpu_cw = 0x037F;
    public ushort fpu_sw;
    public double[] fpu_st = new double[8];
    public int fpu_top;
    public byte fpu_valid;

    public ushort idt_limit { get; set; }
    public uint idt_base { get; set; }
    public ushort gdt_limit { get; set; }
    public uint gdt_base { get; set; }

    static public readonly Accessor<CPU, uint> _cr0 = new(c => c.cr0, c => v => { c.cr0 = v; return c; });
    static public readonly Accessor<CPU, uint> _cr2 = new(c => c.cr2, c => v => { c.cr2 = v; return c; });
    static public readonly Accessor<CPU, uint> _cr3 = new(c => c.cr3, c => v => { c.cr3 = v; return c; });
    static public readonly Accessor<CPU, uint> _cr4 = new(c => c.cr4, c => v => { c.cr4 = v; return c; });

    private uint cr0 { get; set; }
    public uint cr2 { get; set; }          // #PF 発生時にフォルトした線形アドレスを保持する
    public uint cr3 { get; private set; }  // EnvSyncPaging がページテーブル基底として参照する
    public uint cr4 { get; private set; }  // EnvSyncPaging が PAE ビットを参照する

    static public readonly Accessor<CPU, ushort> _sp = new(c => c.sp, c => v => { c.sp = v; return c; });
    static public readonly Accessor<CPU, ushort> _bp = new(c => c.bp, c => v => { c.bp = v; return c; });

    public ushort bp { get { return (ushort)(this.ebp & 0xFFFF); } set { this.ebp = (this.ebp & 0xFFFF0000) + value; } }
    public ushort sp { get { return (ushort)(this.esp & 0xFFFF); } set { this.esp = (this.esp & 0xFFFF0000) + value; } }

    static public readonly Accessor<CPU, uint> _esp = new(c => c.esp, c => v => { c.esp = v; return c; });
    static public readonly Accessor<CPU, uint> _ebp = new(c => c.ebp, c => v => { c.ebp = v; return c; });

    public uint ebp { get; set; }
    public uint esp { get; set; }

    // EFLAGS。bit1 は実 CPU では常に 1(PUSHF/トラップフレーム経由で見える。難読化コードが
    // フラグ値を演算に使うことがあるため、読み書きの両方で強制する)。
    uint eflags_ = 2;
    public uint eflags { get => eflags_ | 2; set => eflags_ = value | 2; }

    private const uint CF = 1;
    private const uint PF = 4;
    private const uint AF = 0x10;
    private const uint ZF = 0x40;
    private const uint SF = 0x80;
    private const uint TF = 0x100;
    private const uint JF = 0x200;
    private const uint DF = 0x400;
    private const uint OF = 0x800;
    private const uint NT = 0x4000;

    private void UpdateEflags(uint flag, bool f)
    {
        this.eflags = (this.eflags & ~flag) | (f ? flag : 0);
    }

    private bool GetEflags(uint flag) => 0 != (this.eflags & flag);

    public bool pe => 0 != (this.cr0 & 0x1);
    // 仮想 8086 モード(EFLAGS.VM)。セグメントはリアルモード同様 セレクタ*16 が基底で、特権レベルは常に 3。
    public bool vm => 0 != (this.eflags & 0x20000);
    // 現在の特権レベル。V86 モードでは CS の RPL に関わらず 3。
    public int cpl => this.vm ? 3 : this.cs & 3;

    // ページング有効(CR0.PG)。変換に使う実際の状態は EmuEnvironment 側に
    // ミラーされる(EnvSyncPaging)。CR 書き込みとスナップショット復元時に同期する。
    public bool pg => 0 != (this.cr0 & 0x80000000u);

    // 書き込み保護(CR0.WP)。リング0 でも読み取り専用ページへの書き込みを #PF にする。
    public bool wp => 0 != (this.cr0 & 0x10000);

    public DecodeContext Decode;
    public bool cs_prefix { get => Decode.Cs; set => Decode.Cs = value; }
    public bool es_prefix { get => Decode.Es; set => Decode.Es = value; }
    public bool ss_prefix { get => Decode.Ss; set => Decode.Ss = value; }
    public bool ds_prefix { get => Decode.Ds; set => Decode.Ds = value; }
    public bool fs_prefix { get => Decode.Fs; set => Decode.Fs = value; }
    public bool gs_prefix { get => Decode.Gs; set => Decode.Gs = value; }
    public bool operand_size_prefix { get => Decode.OperandSize; set => Decode.OperandSize = value; }
    public bool address_size_prefix { get => Decode.AddressSize; set => Decode.AddressSize = value; }
    // LOCK (F0)。単一CPUなのでアトミック性は自明に満たされ、実質無視でよい。
    // 命令間では常に false のためスナップショットには保存しない。
    public bool lock_prefix { get => Decode.Lock; set => Decode.Lock = value; }
    static public readonly Accessor<CPU, bool> _lock_prefix = new(c => c.lock_prefix, c => v => { c.lock_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _cs_prefix = new(c => c.cs_prefix, c => v => { c.cs_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _es_prefix = new(c => c.es_prefix, c => v => { c.es_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _ss_prefix = new(c => c.ss_prefix, c => v => { c.ss_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _ds_prefix = new(c => c.ds_prefix, c => v => { c.ds_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _fs_prefix = new(c => c.fs_prefix, c => v => { c.fs_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _gs_prefix = new(c => c.gs_prefix, c => v => { c.gs_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _operand_size_prefix = new(c => c.operand_size_prefix, c => v => { c.operand_size_prefix = v; return c; });
    static public readonly Accessor<CPU, bool> _address_size_prefix = new(c => c.address_size_prefix, c => v => { c.address_size_prefix = v; return c; });

    public bool cf { get { return GetEflags(CF); } set { UpdateEflags(CF, value); } }
    public bool pf { get { return GetEflags(PF); } set { UpdateEflags(PF, value); } }
    public bool af { get { return GetEflags(AF); } set { UpdateEflags(AF, value); } }
    public bool zf { get { return GetEflags(ZF); } set { UpdateEflags(ZF, value); } }
    public bool sf { get { return GetEflags(SF); } set { UpdateEflags(SF, value); } }
    public bool tf { get { return GetEflags(TF); } set { UpdateEflags(TF, value); } }
    public bool jf { get { return GetEflags(JF); } set { UpdateEflags(JF, value); } }
    public bool df { get { return GetEflags(DF); } set { UpdateEflags(DF, value); } }
    public bool of { get { return GetEflags(OF); } set { UpdateEflags(OF, value); } }
    public bool nt { get { return GetEflags(NT); } set { UpdateEflags(NT, value); } }

    static public readonly Accessor<CPU, bool> _cf = new(c => c.cf, c => v => { c.cf = v; return c; });
    static public readonly Accessor<CPU, bool> _pf = new(c => c.pf, c => v => { c.pf = v; return c; });
    static public readonly Accessor<CPU, bool> _af = new(c => c.af, c => v => { c.af = v; return c; });
    static public readonly Accessor<CPU, bool> _zf = new(c => c.zf, c => v => { c.zf = v; return c; });
    static public readonly Accessor<CPU, bool> _sf = new(c => c.sf, c => v => { c.sf = v; return c; });
    static public readonly Accessor<CPU, bool> _tf = new(c => c.tf, c => v => { c.tf = v; return c; });
    static public readonly Accessor<CPU, bool> _jf = new(c => c.jf, c => v => { c.jf = v; return c; });
    static public readonly Accessor<CPU, bool> _df = new(c => c.df, c => v => { c.df = v; return c; });
    static public readonly Accessor<CPU, bool> _of = new(c => c.of, c => v => { c.of = v; return c; });
    static public readonly Accessor<CPU, bool> _nt = new(c => c.nt, c => v => { c.nt = v; return c; });

    static public readonly Accessor<CPU, byte> _al = new(c => c.al, c => v => { c.al = v; return c; });
    static public readonly Accessor<CPU, byte> _bl = new(c => c.bl, c => v => { c.bl = v; return c; });
    static public readonly Accessor<CPU, byte> _cl = new(c => c.cl, c => v => { c.cl = v; return c; });
    static public readonly Accessor<CPU, byte> _dl = new(c => c.dl, c => v => { c.dl = v; return c; });

    public byte al { get { return (byte)(this.eax & 0xFF); } set { this.eax = (this.eax & 0xFFFFFF00) + value; } }
    public byte bl { get { return (byte)(this.ebx & 0xFF); } set { this.ebx = (this.ebx & 0xFFFFFF00) + value; } }
    public byte cl { get { return (byte)(this.ecx & 0xFF); } set { this.ecx = (this.ecx & 0xFFFFFF00) + value; } }
    public byte dl { get { return (byte)(this.edx & 0xFF); } set { this.edx = (this.edx & 0xFFFFFF00) + value; } }

    static public readonly Accessor<CPU, byte> _ah = new(c => c.ah, c => v => { c.ah = v; return c; });
    static public readonly Accessor<CPU, byte> _bh = new(c => c.bh, c => v => { c.bh = v; return c; });
    static public readonly Accessor<CPU, byte> _ch = new(c => c.ch, c => v => { c.ch = v; return c; });
    static public readonly Accessor<CPU, byte> _dh = new(c => c.dh, c => v => { c.dh = v; return c; });

    public byte ah { get { return (byte)((this.eax >> 8) & 0xFF); } set { this.eax = (this.eax & 0xFFFF00FF) + ((uint)value << 8); } }
    public byte bh { get { return (byte)((this.ebx >> 8) & 0xFF); } set { this.ebx = (this.ebx & 0xFFFF00FF) + ((uint)value << 8); } }
    public byte ch { get { return (byte)((this.ecx >> 8) & 0xFF); } set { this.ecx = (this.ecx & 0xFFFF00FF) + ((uint)value << 8); } }
    public byte dh { get { return (byte)((this.edx >> 8) & 0xFF); } set { this.edx = (this.edx & 0xFFFF00FF) + ((uint)value << 8); } }

    static public readonly Accessor<CPU, ushort> _ax = new(c => c.ax, c => v => { c.ax = v; return c; });
    static public readonly Accessor<CPU, ushort> _bx = new(c => c.bx, c => v => { c.bx = v; return c; });
    static public readonly Accessor<CPU, ushort> _cx = new(c => c.cx, c => v => { c.cx = v; return c; });
    static public readonly Accessor<CPU, ushort> _dx = new(c => c.dx, c => v => { c.dx = v; return c; });

    public ushort ax { get { return (ushort)(this.eax & 0xFFFF); } set { this.eax = (this.eax & 0xFFFF0000) + value; } }
    public ushort bx { get { return (ushort)(this.ebx & 0xFFFF); } set { this.ebx = (this.ebx & 0xFFFF0000) + value; } }
    public ushort cx { get { return (ushort)(this.ecx & 0xFFFF); } set { this.ecx = (this.ecx & 0xFFFF0000) + value; } }
    public ushort dx { get { return (ushort)(this.edx & 0xFFFF); } set { this.edx = (this.edx & 0xFFFF0000) + value; } }

    static public readonly Accessor<CPU, uint> _eax = new(c => c.eax, c => v => { c.eax = v; return c; });
    static public readonly Accessor<CPU, uint> _ebx = new(c => c.ebx, c => v => { c.ebx = v; return c; });
    static public readonly Accessor<CPU, uint> _ecx = new(c => c.ecx, c => v => { c.ecx = v; return c; });
    static public readonly Accessor<CPU, uint> _edx = new(c => c.edx, c => v => { c.edx = v; return c; });

    public uint eax { get; set; }
    public uint ebx { get; set; }
    public uint ecx { get; set; }
    public uint edx { get; set; }

    static public readonly Accessor<CPU, ushort> _si = new(c => c.si, c => v => { c.si = v; return c; });
    static public readonly Accessor<CPU, ushort> _di = new(c => c.di, c => v => { c.di = v; return c; });

    public ushort si { get { return (ushort)(this.esi & 0xFFFF); } set { this.esi = (this.esi & 0xFFFF0000) + value; } }
    public ushort di { get { return (ushort)(this.edi & 0xFFFF); } set { this.edi = (this.edi & 0xFFFF0000) + value; } }

    static public readonly Accessor<CPU, uint> _esi = new(c => c.esi, c => v => { c.esi = v; return c; });
    static public readonly Accessor<CPU, uint> _edi = new(c => c.edi, c => v => { c.edi = v; return c; });

    public uint esi { get; set; }
    public uint edi { get; set; }

    public void CopyTo(CPU d)
    {
        d.cs = cs; d.ds = ds; d.es = es; d.ss = ss; d.fs = fs; d.gs = gs;
        d.cs_base = cs_base; d.ds_base = ds_base; d.es_base = es_base;
        d.ss_base = ss_base; d.fs_base = fs_base; d.gs_base = gs_base;
        d.eip = eip; d.code32 = code32; d.stack32 = stack32;
        d.tr = tr; d.ldtr = ldtr;
        d.idt_limit = idt_limit; d.idt_base = idt_base;
        d.gdt_limit = gdt_limit; d.gdt_base = gdt_base;
        d.cr0 = cr0; d.cr2 = cr2; d.cr3 = cr3; d.cr4 = cr4;
        d.fpu_cw = fpu_cw; d.fpu_sw = fpu_sw;
        d.fpu_top = fpu_top; d.fpu_valid = fpu_valid;
        Array.Copy(fpu_st, d.fpu_st, 8);
        d.ebp = ebp; d.esp = esp; d.eflags = eflags;
        d.eax = eax; d.ebx = ebx; d.ecx = ecx; d.edx = edx;
        d.esi = esi; d.edi = edi;
        d.Decode = Decode;
    }
}
