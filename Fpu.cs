using static Emu86.CPU;
using static Emu86.Unit;
using static Emu86.Conversions;
using static Emu86.States;
using static Emu86.Operands;
using static Emu86.Ports;
using static Emu86.Mmu;
using static Emu86.CpuStates;
using static Emu86.Fetch;

namespace Emu86;

// x87 FPU (0xD8-0xDF + WAIT)。
// 8 本のレジスタスタックを倍精度 double で保持する。実機の内部 80 ビット拡張精度とは
// 丸めが異なりうるが、カーネル/ユーザランドの通常用途には十分。
// FNSAVE/FRSTOR・m80 のロード/ストアは 80 ビット拡張実数と相互変換する。
// 命令は整数命令と同じく表で引く: メモリ形式は (op, reg)、レジスタ形式は (op, reg, rm) か (op, reg) で、
// 各命令はオペランドの Lens(Real: ST(i) とメモリ形式)に対する Load/Store/Compute/Compare の State で書く。
// 環境と全体の保存・復元(FNSTENV/FLDENV、FNSAVE/FRSTOR)は、項目とメモリの対応表を Move の向きだけ変えてたどる。
// 未実装のエンコーディングは失敗を返し、STOP 診断に載る(Execute が EIP を巻き戻す)。
static public class Fpu
{
    // 状態ワードの条件コードビット
    const ushort FpuC0 = 0x0100, FpuC1 = 0x0200, FpuC2 = 0x0400, FpuC3 = 0x4000;

    // --- スタック操作 ------------------------------------------------------
    static int StIdx(CPU c, int i) => (c.fpu_top + i) & 7;
    static double St(CPU c, int i) => c.fpu_st[StIdx(c, i)];
    static void StSet(CPU c, int i, double v) => c.fpu_st[StIdx(c, i)] = v;

    static void FpuPush(CPU c, double v)
    {
        c.fpu_top = (c.fpu_top - 1) & 7;
        c.fpu_st[c.fpu_top] = v;
        c.fpu_valid |= (byte)(1 << c.fpu_top);
    }

    static void FpuPop(CPU c)
    {
        c.fpu_valid &= (byte)~(1 << c.fpu_top);
        c.fpu_top = (c.fpu_top + 1) & 7;
    }

    static void FpuInit(CPU c)
    {
        c.fpu_cw = 0x037F;
        c.fpu_sw = 0;
        c.fpu_top = 0;
        c.fpu_valid = 0;
    }

    // 状態ワード(TOP フィールドを合成)
    static ushort FpuSw(CPU c) => (ushort)((c.fpu_sw & 0xC7FF) | ((c.fpu_top & 7) << 11));

    // --- 比較 ---------------------------------------------------------------
    static void FpuCompare(CPU c, double a, double b)
    {
        c.fpu_sw &= unchecked((ushort)~(FpuC0 | FpuC2 | FpuC3));
        if (double.IsNaN(a) || double.IsNaN(b)) c.fpu_sw |= FpuC0 | FpuC2 | FpuC3;
        else if (a < b) c.fpu_sw |= FpuC0;
        else if (a == b) c.fpu_sw |= FpuC3;
    }

    // FCOMI/FUCOMI 系: 結果を EFLAGS(ZF/PF/CF)へ入れる。OF/SF はクリア。
    static void FpuCompareEflags(CPU c, double a, double b)
    {
        c.of = false;
        c.sf = false;
        if (double.IsNaN(a) || double.IsNaN(b)) { c.zf = true; c.pf = true; c.cf = true; }
        else { c.zf = a == b; c.pf = false; c.cf = a < b; }
    }

    // --- 丸めと整数変換(制御ワードの RC フィールドに従う) --------------------
    static double FpuRound(CPU c, double v) => ((c.fpu_cw >> 10) & 3) switch
    {
        0 => Math.Round(v, MidpointRounding.ToEven),
        1 => Math.Floor(v),
        2 => Math.Ceiling(v),
        _ => Math.Truncate(v),
    };

    // 範囲外は「整数不定値」(最上位ビットのみ)を書くのが x87 の仕様。
    static ushort FpuToInt16(CPU c, double v)
    {
        var r = FpuRound(c, v);
        return r is >= -32768 and <= 32767 ? (ushort)(short)r : (ushort)0x8000;
    }

    static uint FpuToInt32(CPU c, double v)
    {
        var r = FpuRound(c, v);
        return r is >= int.MinValue and <= int.MaxValue ? (uint)(int)r : 0x80000000u;
    }

    static ulong FpuToInt64(CPU c, double v)
    {
        var r = FpuRound(c, v);
        return r is >= -9.2233720368547758E18 and < 9.2233720368547758E18 ? (ulong)(long)r : 0x8000000000000000UL;
    }

    // --- メモリアクセス(線形アドレス、ページング対応の共通経路を使う) --------
    static ulong FpuR64(EmuEnvironment env, uint a) =>
        EnvGetMemoryData32(env, a) | ((ulong)EnvGetMemoryData32(env, a + 4) << 32);

    static void FpuW64(EmuEnvironment env, uint a, ulong v)
    {
        EnvWriteDword(env, a, (uint)v);
        EnvWriteDword(env, a + 4, (uint)(v >> 32));
    }

    static double FpuReadF32(EmuEnvironment env, uint a) =>
        BitConverter.Int32BitsToSingle((int)EnvGetMemoryData32(env, a));

    static double FpuReadF64(EmuEnvironment env, uint a) =>
        BitConverter.Int64BitsToDouble((long)FpuR64(env, a));

    static void FpuWriteF32(EmuEnvironment env, uint a, double v) =>
        EnvWriteDword(env, a, (uint)BitConverter.SingleToInt32Bits((float)v));

    static void FpuWriteF64(EmuEnvironment env, uint a, double v) =>
        FpuW64(env, a, (ulong)BitConverter.DoubleToInt64Bits(v));

    // --- 80 ビット拡張実数との変換 -------------------------------------------
    // 形式: 符号1 + 指数15(バイアス16383) + 仮数64(明示的な整数ビットつき)
    static double F80ToDouble(ulong mant, ushort se)
    {
        var neg = (se & 0x8000) != 0;
        var exp = se & 0x7FFF;
        if (exp == 0 && mant == 0) return neg ? -0.0 : 0.0;
        if (exp == 0x7FFF)
            return (mant << 1) == 0
                ? (neg ? double.NegativeInfinity : double.PositiveInfinity)
                : double.NaN;
        var d = mant * Math.Pow(2.0, exp - 16383 - 63);
        return neg ? -d : d;
    }

    static (ulong mant, ushort se) DoubleToF80(double v)
    {
        var bits = (ulong)BitConverter.DoubleToInt64Bits(v);
        var sign = (ushort)((bits >> 63) != 0 ? 0x8000 : 0);
        var exp = (int)((bits >> 52) & 0x7FF);
        var frac = bits & 0x000FFFFFFFFFFFFFUL;
        if (exp == 0x7FF) // Inf/NaN
            return (0x8000000000000000UL | (frac << 11), (ushort)(sign | 0x7FFF));
        if (exp == 0)
        {
            if (frac == 0) return (0UL, sign);
            // 非正規化数: 最上位ビットを bit63 へ正規化する
            var hb = 63 - System.Numerics.BitOperations.LeadingZeroCount(frac);
            return (frac << (63 - hb), (ushort)(sign | (16383 + hb - 1074)));
        }
        return (0x8000000000000000UL | (frac << 11), (ushort)(sign | (exp - 1023 + 16383)));
    }

    // --- タグワード ------------------------------------------------------------
    // 物理レジスタ j のタグ: 0 valid、1 zero、2 special(NaN/Inf)、3 empty。
    static uint FpuTag(CPU c, int j) =>
        (c.fpu_valid & (1 << j)) == 0 ? 3u
        : c.fpu_st[j] == 0 ? 1u
        : double.IsNaN(c.fpu_st[j]) || double.IsInfinity(c.fpu_st[j]) ? 2u
        : 0u;

    static uint FpuTagWord(CPU c) =>
        Enumerable.Range(0, 8).Aggregate(0u, (tag, j) => tag | FpuTag(c, j) << (2 * j));

    // --- 命令の記述: オペランド(Lens)と操作 ------------------------------------
    // Real は double を読み書きする Lens(整数命令の Operand と同じ Lens<T>。Move/Swap も共有)。ST(i) とメモリ形式
    // (f32/f64/int16/int32/int64/f80)を同じ形で扱うので、命令は「どこから読み、どこへ書き、何を計算するか」だけで書ける。
    static Real StReal(int i) => new(GetDataFromCpu(c => St(c, i)), v => Effect(c => StSet(c, i, v)));
    static readonly Real St0 = StReal(0), St1 = StReal(1);
    static Real Constant(double value) => new(value.ToState(), _ => throw new InvalidOperationException("constant operand is read-only"));
    static readonly Real Zero = Constant(0.0);
    static Real F32At(uint a) => new(GetDataFromEnvCpu((e, _) => FpuReadF32(e, a)), v => Effect((e, _) => FpuWriteF32(e, a, v)));
    static Real F64At(uint a) => new(GetDataFromEnvCpu((e, _) => FpuReadF64(e, a)), v => Effect((e, _) => FpuWriteF64(e, a, v)));
    static Real I16At(uint a) => new(GetDataFromEnvCpu((e, _) => (double)(short)EnvGetMemoryData16(e, a)), v => Effect((e, c) => EnvWriteWord(e, a, FpuToInt16(c, v))));
    static Real I32At(uint a) => new(GetDataFromEnvCpu((e, _) => (double)(int)EnvGetMemoryData32(e, a)), v => Effect((e, c) => EnvWriteDword(e, a, FpuToInt32(c, v))));
    static Real I64At(uint a) => new(GetDataFromEnvCpu((e, _) => (double)(long)FpuR64(e, a)), v => Effect((e, c) => FpuW64(e, a, FpuToInt64(c, v))));
    static Real F80At(uint a) => new(GetDataFromEnvCpu((e, _) => F80ToDouble(FpuR64(e, a), EnvGetMemoryData16(e, a + 8))),
        v => Effect((e, _) => { var (mant, se) = DoubleToF80(v); FpuW64(e, a, mant); EnvWriteWord(e, a + 8, se); }));

    // --- 制御・状態・タグワードと、FNSTENV/FLDENV/FNSAVE/FRSTOR(32 ビット保護モード形式) ---------
    // 環境は 4 バイトの項が 7 つ(cw sw tag fip fcs fdp fds = 28 バイト)。FNSAVE/FRSTOR はその後ろに ST(0)..ST(7) の 80 ビット値が 8 つ(計 108 バイト)。
    // 項とメモリ上の位置の対応を 1 度だけ書き、保存は メモリ <- 項、読み込みは 項 <- メモリ の Move にする
    // (FLDCW/FNSTCW/FNSTSW は同じ Lens を 2 バイトのメモリと組み合わせる)。
    static Lens<uint> WordAt(uint a) => new(GetDataFromEnvCpu((e, _) => (uint)EnvGetMemoryData16(e, a)), v => Effect((e, _) => EnvWriteWord(e, a, (ushort)v)));
    static Lens<uint> DwordAt(uint a) => new(GetDataFromEnvCpu((e, _) => EnvGetMemoryData32(e, a)), v => Effect((e, _) => EnvWriteDword(e, a, v)));

    static readonly Lens<uint> ControlWord = new(GetDataFromCpu(c => (uint)c.fpu_cw), v => Effect(c => c.fpu_cw = (ushort)v));
    // 状態ワードは、読むと TOP フィールドを合成し、書くと TOP を取り出す。
    static readonly Lens<uint> StatusWord = new(GetDataFromCpu(c => (uint)FpuSw(c)),
        v => Effect(c => { c.fpu_sw = (ushort)(v & 0xC7FF); c.fpu_top = (int)((v >> 11) & 7); }));
    // タグワードは、読むと各レジスタの状態、書くと empty(3)以外のレジスタを有効にする。
    static readonly Lens<uint> TagWord = new(GetDataFromCpu(FpuTagWord),
        v => Effect(c => c.fpu_valid = (byte)Enumerable.Range(0, 8).Where(j => ((v >> (2 * j)) & 3) != 3).Sum(j => 1 << j)));

    // 環境の各項。追跡していない fip/fcs/fdp/fds は null(保存では 0 を書き、読み込みでは触れない)。
    static readonly Lens<uint>?[] EnvironmentLayout = [ControlWord, StatusWord, TagWord, null, null, null, null];

    static State<Unit> StoreEnvironment(uint a) =>
        (from slot in EnvironmentLayout.Index()
         let memory = DwordAt(a + 4 * (uint)slot.Index)
         select slot.Item is { } field ? Move(memory, field) : memory.Write(0))
        .Sequence().Ignore();

    static State<Unit> LoadEnvironment(uint a) =>
        (from slot in EnvironmentLayout.Index()
         where slot.Item != null
         select Move(slot.Item.Value, DwordAt(a + 4 * (uint)slot.Index)))
        .Sequence().Ignore();

    // 環境の後ろに並ぶ 80 ビット値(10 バイトずつ)と、対応する ST(i)。
    static IEnumerable<(Real memory, Real register)> RegisterImage(uint a) =>
        from i in Enumerable.Range(0, 8)
        select (F80At(a + 28 + 10 * (uint)i), StReal(i));

    // FNSAVE: 環境とレジスタを保存してから初期化する。FRSTOR はその逆向き。
    static State<Unit> Save(uint a) =>
        StoreEnvironment(a)
            .Then(RegisterImage(a).Select(pair => Move(pair.memory, pair.register)).Sequence().Ignore())
            .Then(Effect(FpuInit));

    static State<Unit> Restore(uint a) =>
        LoadEnvironment(a)
            .Then(RegisterImage(a).Select(pair => Move(pair.register, pair.memory)).Sequence().Ignore());

    // 何もしない State。State.cs の Pass は別ファイルの静的フィールドなので、表の初期化順に依存しないよう自前で持つ。
    static readonly State<Unit> FpuNop = unit.ToState();
    static readonly State<Unit> PopSt = Effect(FpuPop);
    static State<Unit> Pops(int count) => Enumerable.Repeat(PopSt, count).Aggregate(FpuNop, States.Then);
    static State<Unit> Push(double value) => Effect(c => FpuPush(c, value));
    static readonly State<Unit> ClearC2 = Effect(c => c.fpu_sw &= unchecked((ushort)~FpuC2));

    // push src
    static State<Unit> Load(Real src) =>
        from v in src.Read
        from _ in Effect(c => FpuPush(c, v))
        select unit;

    // dst <- ST0、pop なら続けて pop
    static State<Unit> Store(Real dst, bool pop = false) =>
        from _1 in Move(dst, St0)
        from _2 in Pops(pop ? 1 : 0)
        select unit;

    // dst <- f(ST0, other)、続けて pops 回 pop
    static State<Unit> Compute(Real dst, Real other, Func<double, double, double> f, int pops = 0) =>
        from x in St0.Read
        from y in other.Read
        from _1 in dst.Write(f(x, y))
        from _2 in Pops(pops)
        select unit;

    // ST0 <- f(ST0)
    static State<Unit> Unary(Func<double, double> f) =>
        from x in St0.Read
        from _ in St0.Write(f(x))
        select unit;

    // 条件コード(C0/C2/C3)へ ST0 と other の比較結果を入れ、pops 回 pop
    static State<Unit> Compare(Real other, int pops = 0) =>
        from x in St0.Read
        from y in other.Read
        from _1 in Effect(c => FpuCompare(c, x, y))
        from _2 in Pops(pops)
        select unit;

    // EFLAGS(ZF/PF/CF)へ比較結果を入れる(FCOMI 系)
    static State<Unit> CompareEflags(Real other, int pops = 0) =>
        from x in St0.Read
        from y in other.Read
        from _1 in Effect(c => FpuCompareEflags(c, x, y))
        from _2 in Pops(pops)
        select unit;

    // ST0 <-> ST(i)
    static State<Unit> Exchange(int i) => Swap(St0, StReal(i));

    // 条件が成り立てば ST0 <- ST(i)(FCMOVcc)
    static State<Unit> ConditionalMove(Func<CPU, bool> condition, int i) =>
        from take in GetDataFromCpu(condition)
        from v in StReal(i).Read
        from _ in When(take, St0.Write(v))
        select unit;

    // 算術グループ(D8/DA/DC/DE の reg フィールド): 0=ADD 1=MUL 2=COM 3=COMP 4=SUB 5=SUBR 6=DIV 7=DIVR。
    // f(x, y) の x は ST0、y はもう一方のオペランド。DC/DE のレジスタ形式は書き込み先が ST(i) になるだけで、
    // SUB/DIV の向き(ST(i) <- ST0 - ST(i) が FSUBR)も同じ式で表せる。
    static readonly Func<double, double, double>[] ArithOp =
        [(x, y) => x + y, (x, y) => x * y, null, null, (x, y) => x - y, (x, y) => y - x, (x, y) => x / y, (x, y) => y / x];

    static State<Unit> Arith(int kind, Real other, Real dst, bool pop) => kind switch
    {
        2 => Compare(other, pop ? 1 : 0),
        3 => Compare(other, pop ? 2 : 1),
        _ => Compute(dst, other, ArithOp[kind], pop ? 1 : 0),
    };

    // 条件コードを使う少数の命令(FXAM/FPREM/FXTRACT/FSINCOS)は CPU を直接触る。
    static void FpuExamine(CPU c)
    {
        var v = St(c, 0);
        c.fpu_sw &= unchecked((ushort)~(FpuC0 | FpuC1 | FpuC2 | FpuC3));
        if (double.IsNegative(v)) c.fpu_sw |= FpuC1;
        if ((c.fpu_valid & (1 << StIdx(c, 0))) == 0) c.fpu_sw |= FpuC3 | FpuC0;
        else if (double.IsNaN(v)) c.fpu_sw |= FpuC0;
        else if (double.IsInfinity(v)) c.fpu_sw |= FpuC2 | FpuC0;
        else if (v == 0) c.fpu_sw |= FpuC3;
        else c.fpu_sw |= FpuC2; // normal(非正規化は区別しない)
    }

    // FPREM: 切り捨て剰余。商の下位ビットを C0/C3/C1 に返す。
    static void FpuRemainder(CPU c)
    {
        var q = (long)(St(c, 0) / St(c, 1));
        StSet(c, 0, St(c, 0) % St(c, 1));
        c.fpu_sw &= unchecked((ushort)~(FpuC0 | FpuC1 | FpuC2 | FpuC3));
        if ((q & 4) != 0) c.fpu_sw |= FpuC0;
        if ((q & 2) != 0) c.fpu_sw |= FpuC3;
        if ((q & 1) != 0) c.fpu_sw |= FpuC1;
    }

    // FXTRACT: ST0 を指数と仮数に分解する。
    static void FpuExtract(CPU c)
    {
        var v = St(c, 0);
        var e = Math.Floor(Math.Log2(Math.Abs(v)));
        StSet(c, 0, e);
        FpuPush(c, v / Math.Pow(2.0, e));
    }

    static void FpuSinCos(CPU c)
    {
        var v = St(c, 0);
        StSet(c, 0, Math.Sin(v));
        FpuPush(c, Math.Cos(v));
        c.fpu_sw &= unchecked((ushort)~FpuC2);
    }

    // --- 表 ------------------------------------------------------------------
    // メモリ形式: (op, reg) → 実効アドレスから命令を作る。
    static readonly Dictionary<(int op, int reg), Func<uint, State<Unit>>> FpuMemoryForms = BuildFpuMemoryForms();

    static Dictionary<(int op, int reg), Func<uint, State<Unit>>> BuildFpuMemoryForms()
    {
        // 算術グループ(reg 0-7)のメモリ形式: D8 = f32、DA = int32、DC = f64、DE = int16 のメモリオペランドと ST0。
        (int op, Func<uint, Real> source)[] arithmeticForms = [(0xD8, F32At), (0xDA, I32At), (0xDC, F64At), (0xDE, I16At)];
        var table = new Dictionary<(int op, int reg), Func<uint, State<Unit>>>
        {
            [(0xD9, 0)] = a => Load(F32At(a)), [(0xD9, 2)] = a => Store(F32At(a)), [(0xD9, 3)] = a => Store(F32At(a), pop: true),
            [(0xD9, 4)] = LoadEnvironment,                                                  // FLDENV
            [(0xD9, 5)] = a => Move(ControlWord, WordAt(a)),                                // FLDCW
            [(0xD9, 6)] = a => StoreEnvironment(a).Then(Effect(c => c.fpu_cw |= 0x3F)),     // FNSTENV(保存後に例外を全マスク)
            [(0xD9, 7)] = a => Move(WordAt(a), ControlWord),                                // FNSTCW
            [(0xDB, 0)] = a => Load(I32At(a)), [(0xDB, 2)] = a => Store(I32At(a)), [(0xDB, 3)] = a => Store(I32At(a), pop: true),
            [(0xDB, 5)] = a => Load(F80At(a)), [(0xDB, 7)] = a => Store(F80At(a), pop: true),
            [(0xDD, 0)] = a => Load(F64At(a)), [(0xDD, 2)] = a => Store(F64At(a)), [(0xDD, 3)] = a => Store(F64At(a), pop: true),
            [(0xDD, 4)] = Restore,                                                          // FRSTOR
            [(0xDD, 6)] = Save,                                                             // FNSAVE
            [(0xDD, 7)] = a => Move(WordAt(a), StatusWord),                                 // FNSTSW m16
            [(0xDF, 0)] = a => Load(I16At(a)), [(0xDF, 2)] = a => Store(I16At(a)), [(0xDF, 3)] = a => Store(I16At(a), pop: true),
            [(0xDF, 5)] = a => Load(I64At(a)), [(0xDF, 7)] = a => Store(I64At(a), pop: true),
        };
        var arithmetic =
            from form in arithmeticForms
            from kind in Enumerable.Range(0, 8)
            select (key: (form.op, kind), instruction: (Func<uint, State<Unit>>)(a => Arith(kind, form.source(a), St0, pop: false)));
        foreach (var (key, instruction) in arithmetic) table[key] = instruction;
        return table;
    }

    // レジスタ形式のうち rm まで固定の命令: (op, reg, rm) → 命令。
    static readonly Dictionary<(int op, int reg, int rm), State<Unit>> FpuFixedForms = new()
    {
        [(0xD9, 2, 0)] = FpuNop,                                                      // FNOP
        [(0xD9, 4, 0)] = Unary(x => -x),                                              // FCHS
        [(0xD9, 4, 1)] = Unary(Math.Abs),                                             // FABS
        [(0xD9, 4, 4)] = Compare(Zero),                                               // FTST
        [(0xD9, 4, 5)] = Effect(FpuExamine),                                             // FXAM
        [(0xD9, 5, 0)] = Push(1.0),                                                   // FLD1
        [(0xD9, 5, 1)] = Push(3.321928094887362),                                     // FLDL2T
        [(0xD9, 5, 2)] = Push(1.4426950408889634),                                    // FLDL2E
        [(0xD9, 5, 3)] = Push(Math.PI),                                               // FLDPI
        [(0xD9, 5, 4)] = Push(0.3010299956639812),                                    // FLDLG2
        [(0xD9, 5, 5)] = Push(0.6931471805599453),                                    // FLDLN2
        [(0xD9, 5, 6)] = Push(0.0),                                                   // FLDZ
        [(0xD9, 6, 0)] = Unary(x => Math.Pow(2.0, x) - 1.0),                          // F2XM1
        [(0xD9, 6, 1)] = Compute(St1, St1, (x, y) => y * Math.Log2(x), pops: 1),      // FYL2X
        [(0xD9, 6, 2)] = Unary(Math.Tan).Then(Push(1.0)).Then(ClearC2),               // FPTAN
        [(0xD9, 6, 3)] = Compute(St1, St1, (x, y) => Math.Atan2(y, x), pops: 1),      // FPATAN
        [(0xD9, 6, 4)] = Effect(FpuExtract),                                             // FXTRACT
        [(0xD9, 6, 5)] = Compute(St0, St1, Math.IEEERemainder).Then(ClearC2),         // FPREM1
        [(0xD9, 6, 6)] = Effect(c => c.fpu_top = (c.fpu_top - 1) & 7),                   // FDECSTP
        [(0xD9, 6, 7)] = Effect(c => c.fpu_top = (c.fpu_top + 1) & 7),                   // FINCSTP
        [(0xD9, 7, 0)] = Effect(FpuRemainder),                                           // FPREM
        [(0xD9, 7, 1)] = Compute(St1, St1, (x, y) => y * Math.Log2(x + 1.0), pops: 1), // FYL2XP1
        [(0xD9, 7, 2)] = Unary(Math.Sqrt),                                            // FSQRT
        [(0xD9, 7, 3)] = Effect(FpuSinCos),                                              // FSINCOS
        [(0xD9, 7, 4)] = Effect(c => StSet(c, 0, FpuRound(c, St(c, 0)))),                // FRNDINT
        [(0xD9, 7, 5)] = Compute(St0, St1, (x, y) => x * Math.Pow(2.0, Math.Truncate(y))), // FSCALE
        [(0xD9, 7, 6)] = Unary(Math.Sin).Then(ClearC2),                               // FSIN
        [(0xD9, 7, 7)] = Unary(Math.Cos).Then(ClearC2),                               // FCOS
        [(0xDA, 5, 1)] = Compare(St1, pops: 2),                                       // FUCOMPP
        [(0xDB, 4, 2)] = Effect(c => c.fpu_sw &= 0x7F00),                                // FNCLEX
        [(0xDB, 4, 3)] = Effect(FpuInit),                                                // FNINIT
        [(0xDE, 3, 1)] = Compare(St1, pops: 2),                                       // FCOMPP
        [(0xDF, 4, 0)] = Effect(c => c.ax = FpuSw(c)),                                   // FNSTSW AX
    };

    // レジスタ形式: (op, reg) → ST(i) の番号から命令を作る。
    static readonly Dictionary<(int op, int reg), Func<int, State<Unit>>> FpuRegisterForms = BuildFpuRegisterForms();

    static Dictionary<(int op, int reg), Func<int, State<Unit>>> BuildFpuRegisterForms()
    {
        var table = new Dictionary<(int op, int reg), Func<int, State<Unit>>>
        {
            [(0xD9, 0)] = i => Load(StReal(i)),                                   // FLD ST(i)
            [(0xD9, 1)] = Exchange,                                               // FXCH
            [(0xD9, 3)] = i => Store(StReal(i), pop: true),                       // FSTP ST(i)(別名)
            [(0xDA, 0)] = i => ConditionalMove(c => c.cf, i),                     // FCMOVB
            [(0xDA, 1)] = i => ConditionalMove(c => c.zf, i),                     // FCMOVE
            [(0xDA, 2)] = i => ConditionalMove(c => c.cf || c.zf, i),             // FCMOVBE
            [(0xDA, 3)] = i => ConditionalMove(c => c.pf, i),                     // FCMOVU
            [(0xDB, 0)] = i => ConditionalMove(c => !c.cf, i),                    // FCMOVNB
            [(0xDB, 1)] = i => ConditionalMove(c => !c.zf, i),                    // FCMOVNE
            [(0xDB, 2)] = i => ConditionalMove(c => !c.cf && !c.zf, i),           // FCMOVNBE
            [(0xDB, 3)] = i => ConditionalMove(c => !c.pf, i),                    // FCMOVNU
            [(0xDB, 5)] = i => CompareEflags(StReal(i)),                          // FUCOMI
            [(0xDB, 6)] = i => CompareEflags(StReal(i)),                          // FCOMI
            [(0xDD, 0)] = i => Effect(c => c.fpu_valid &= (byte)~(1 << StIdx(c, i))), // FFREE
            [(0xDD, 2)] = i => Store(StReal(i)),                                  // FST ST(i)
            [(0xDD, 3)] = i => Store(StReal(i), pop: true),                       // FSTP ST(i)
            [(0xDD, 4)] = i => Compare(StReal(i)),                                // FUCOM
            [(0xDD, 5)] = i => Compare(StReal(i), pops: 1),                       // FUCOMP
            [(0xDF, 5)] = i => CompareEflags(StReal(i), pops: 1),                 // FUCOMIP
            [(0xDF, 6)] = i => CompareEflags(StReal(i), pops: 1),                 // FCOMIP
        };
        // 算術グループのレジスタ形式: D8 は ST0 <- ST0 op ST(i)、DC は ST(i) <- ST(i) op ST0、DE はそれに pop(FCOMPP は固定形式)。
        var arithmetic =
            from form in new (int op, bool toSti, bool pop)[] { (0xD8, false, false), (0xDC, true, false), (0xDE, true, true) }
            from kind in Enumerable.Range(0, 8)
            where !(form.op == 0xDE && kind == 3)
            select (key: (form.op, kind), instruction: (Func<int, State<Unit>>)(i => Arith(kind, StReal(i), form.toSti ? StReal(i) : St0, form.pop)));
        foreach (var (key, instruction) in arithmetic) table[key] = instruction;
        return table;
    }

    // レジスタ形式を表で引く((op, reg, rm) の固定形式が先)。未定義のエンコーディングは null。
    static State<Unit> RegisterForm(int op, int reg, int rm) =>
        FpuFixedForms.TryGetValue((op, reg, rm), out var fixedForm) ? fixedForm
        : FpuRegisterForms.TryGetValue((op, reg), out var register) ? register(rm)
        : null;

    // ModRM で表を引く。未定義のエンコーディングは失敗(STOP 診断に載り、Execute が EIP を巻き戻す)。
    static State<Unit> FpuInstruction(int op, int mod, int reg, int rm) =>
        mod != 3
            ? FpuMemoryForms.TryGetValue((op, reg), out var memory)
                ? (from address in GetMemOrRegAddr(mod, rm) from _ in memory(address.addr) select unit)
                : Undefined
            : RegisterForm(op, reg, rm) ?? Undefined;

    // IR(Semantics.Lower)が 1 つの文として呼ぶ入口: #NM の検査に続けて命令を実行する。レジスタ形式は State、メモリ形式は
    // 実効アドレス(線形)から State を作る関数。未定義のエンコーディングは null(IR には下ろさず、通常コアが #UD にする)。
    static public State<Unit> RegisterInstruction(int op, int reg, int rm) =>
        RegisterForm(op, reg, rm) is { } form ? FpuAvailable.Then(form) : null;

    static public Func<uint, State<Unit>> MemoryInstruction(int op, int reg) =>
        FpuMemoryForms.TryGetValue((op, reg), out var memory) ? address => FpuAvailable.Then(memory(address)) : null;

    // --- 利用できるか(#NM) ------------------------------------------------
    // CR0.EM(FPU なし)か CR0.TS(タスク切り替え後で、FPU の中身が前のスレッドのまま)が立っていれば、x87 命令は実行せずに
    // #NM(ベクタ 7)になる。OS はこの例外で FPU の状態を入れ替える(遅延保存)。FWAIT は CR0.MP と CR0.TS の両方が
    // 立っているときだけ #NM。例外は IDT 経由でしか配送しないので、リアルモードでは検査しない。
    static State<Unit> NotAvailableWhen(Func<CPU, bool> blocked) => (env, cpu, ope) =>
        cpu.pe && blocked(cpu) ? throw new CpuFaultException(Fault.DeviceNotAvailable) : (true, unit, cpu, string.Empty);

    static readonly State<Unit> FpuAvailable = NotAvailableWhen(cpu => cpu.em || cpu.ts);
    static public readonly State<Unit> WaitAvailable = NotAvailableWhen(cpu => cpu.mp && cpu.ts);

    // --- 本体 ----------------------------------------------------------------
    static public State<Unit> Fpu_D8_DF =>
        from _a in FpuAvailable
        from ope in Opecodes
        from m in ModRegRm()
        from _ in FpuInstruction(ope[0], m.mod, m.reg, m.rm)
        select unit;
}
