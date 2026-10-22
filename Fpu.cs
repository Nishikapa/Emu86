using static Emu86.CPU;
using static Emu86.Unit;

namespace Emu86;

// x87 FPU (0xD8-0xDF + WAIT)。
// 8 本のレジスタスタックを倍精度 double で保持する。実機の内部 80 ビット拡張精度とは
// 丸めが異なりうるが、カーネル/ユーザランドの通常用途には十分。
// FNSAVE/FRSTOR・m80 のロード/ストアは 80 ビット拡張実数と相互変換する。
// 命令は整数命令と同じく表で引く: メモリ形式は (op, reg)、レジスタ形式は (op, reg, rm) か (op, reg) で、
// 各命令はオペランドの Lens(Real: ST(i) とメモリ形式)に対する Load/Store/Compute/Compare の State で書く。
// 未実装のエンコーディングは失敗を返し、STOP 診断に載る(Execute が EIP を巻き戻す)。
static public partial class Ext
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

    // --- FNSTENV/FLDENV/FNSAVE/FRSTOR(32 ビット保護モード形式) ----------------
    // env: cw(4) sw(4) tag(4) fip(4) fcs(4) fdp(4) fds(4) = 28 バイト
    // save: env + ST(0)..ST(7) の 80 ビット値 ×8 = 108 バイト
    static uint FpuTagWord(CPU c)
    {
        uint tag = 0;
        for (var j = 0; j < 8; j++)
        {
            uint t;
            if ((c.fpu_valid & (1 << j)) == 0) t = 3;                       // empty
            else if (c.fpu_st[j] == 0) t = 1;                               // zero
            else if (double.IsNaN(c.fpu_st[j]) || double.IsInfinity(c.fpu_st[j])) t = 2; // special
            else t = 0;                                                     // valid
            tag |= t << (2 * j);
        }
        return tag;
    }

    static void FpuStEnv(EmuEnvironment env, CPU c, uint a)
    {
        EnvWriteDword(env, a + 0, c.fpu_cw);
        EnvWriteDword(env, a + 4, FpuSw(c));
        EnvWriteDword(env, a + 8, FpuTagWord(c));
        EnvWriteDword(env, a + 12, 0); // fip(未追跡)
        EnvWriteDword(env, a + 16, 0); // fcs/opcode
        EnvWriteDword(env, a + 20, 0); // fdp
        EnvWriteDword(env, a + 24, 0); // fds
    }

    static void FpuLdEnv(EmuEnvironment env, CPU c, uint a)
    {
        c.fpu_cw = (ushort)EnvGetMemoryData32(env, a);
        var sw = EnvGetMemoryData32(env, a + 4);
        c.fpu_sw = (ushort)(sw & 0xC7FF);
        c.fpu_top = (int)((sw >> 11) & 7);
        var tag = EnvGetMemoryData32(env, a + 8);
        c.fpu_valid = 0;
        for (var j = 0; j < 8; j++)
            if (((tag >> (2 * j)) & 3) != 3)
                c.fpu_valid |= (byte)(1 << j);
    }

    static void FpuSave(EmuEnvironment env, CPU c, uint a)
    {
        FpuStEnv(env, c, a);
        for (var i = 0; i < 8; i++)
        {
            var (mant, se) = DoubleToF80(St(c, i));
            FpuW64(env, a + 28 + (uint)i * 10, mant);
            EnvWriteWord(env, a + 28 + (uint)i * 10 + 8, se);
        }
        FpuInit(c); // FNSAVE は保存後に初期化する
    }

    static void FpuRstor(EmuEnvironment env, CPU c, uint a)
    {
        FpuLdEnv(env, c, a);
        for (var i = 0; i < 8; i++)
        {
            var mant = FpuR64(env, a + 28 + (uint)i * 10);
            var se = EnvGetMemoryData16(env, a + 28 + (uint)i * 10 + 8);
            StSet(c, i, F80ToDouble(mant, se));
        }
    }

    // --- 命令の記述: オペランド(Lens)と操作 ------------------------------------
    // Real は double として読む State と書き戻す関数の組(整数命令の Operand と同じ考え方)。ST(i) とメモリ形式
    // (f32/f64/int16/int32/int64/f80)を同じ形で扱うので、命令は「どこから読み、どこへ書き、何を計算するか」だけで書ける。
    static State<Unit> Fpu(Action<CPU> action) => SetCpu(c => { action(c); return c; });
    static State<Unit> FpuEnv(Action<EmuEnvironment, CPU> action) => SetCpu((e, c) => { action(e, c); return c; });

    static Real StReal(int i) => new(GetDataFromCpu(c => St(c, i)), v => Fpu(c => StSet(c, i, v)));
    static readonly Real St0 = StReal(0), St1 = StReal(1);
    static Real Constant(double value) => new(value.ToState(), _ => throw new InvalidOperationException("constant operand is read-only"));
    static readonly Real Zero = Constant(0.0);
    static Real F32At(uint a) => new(GetDataFromEnvCpu((e, _) => FpuReadF32(e, a)), v => FpuEnv((e, _) => FpuWriteF32(e, a, v)));
    static Real F64At(uint a) => new(GetDataFromEnvCpu((e, _) => FpuReadF64(e, a)), v => FpuEnv((e, _) => FpuWriteF64(e, a, v)));
    static Real I16At(uint a) => new(GetDataFromEnvCpu((e, _) => (double)(short)EnvGetMemoryData16(e, a)), v => FpuEnv((e, c) => EnvWriteWord(e, a, FpuToInt16(c, v))));
    static Real I32At(uint a) => new(GetDataFromEnvCpu((e, _) => (double)(int)EnvGetMemoryData32(e, a)), v => FpuEnv((e, c) => EnvWriteDword(e, a, FpuToInt32(c, v))));
    static Real I64At(uint a) => new(GetDataFromEnvCpu((e, _) => (double)(long)FpuR64(e, a)), v => FpuEnv((e, c) => FpuW64(e, a, FpuToInt64(c, v))));
    static Real F80At(uint a) => new(GetDataFromEnvCpu((e, _) => F80ToDouble(FpuR64(e, a), EnvGetMemoryData16(e, a + 8))),
        v => FpuEnv((e, _) => { var (mant, se) = DoubleToF80(v); FpuW64(e, a, mant); EnvWriteWord(e, a + 8, se); }));

    // 何もしない State。State.cs の Pass は別ファイルの静的フィールドなので、表の初期化順に依存しないよう自前で持つ。
    static readonly State<Unit> FpuNop = unit.ToState();
    static readonly State<Unit> PopSt = Fpu(FpuPop);
    static State<Unit> Pops(int count) => count == 0 ? FpuNop : count == 1 ? PopSt : PopSt.Then(PopSt);
    static State<Unit> Push(double value) => Fpu(c => FpuPush(c, value));
    static readonly State<Unit> ClearC2 = Fpu(c => c.fpu_sw &= unchecked((ushort)~FpuC2));

    // push src
    static State<Unit> Load(Real src) =>
        from v in src.Read
        from _ in Fpu(c => FpuPush(c, v))
        select unit;

    // dst <- ST0、pop なら続けて pop
    static State<Unit> Store(Real dst, bool pop = false) =>
        from v in St0.Read
        from _1 in dst.Write(v)
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
        from _1 in Fpu(c => FpuCompare(c, x, y))
        from _2 in Pops(pops)
        select unit;

    // EFLAGS(ZF/PF/CF)へ比較結果を入れる(FCOMI 系)
    static State<Unit> CompareEflags(Real other, int pops = 0) =>
        from x in St0.Read
        from y in other.Read
        from _1 in Fpu(c => FpuCompareEflags(c, x, y))
        from _2 in Pops(pops)
        select unit;

    // ST0 <-> ST(i)
    static State<Unit> Exchange(int i) =>
        from a in St0.Read
        from b in StReal(i).Read
        from _1 in St0.Write(b)
        from _2 in StReal(i).Write(a)
        select unit;

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
        var table = new Dictionary<(int op, int reg), Func<uint, State<Unit>>>
        {
            [(0xD9, 0)] = a => Load(F32At(a)), [(0xD9, 2)] = a => Store(F32At(a)), [(0xD9, 3)] = a => Store(F32At(a), pop: true),
            [(0xD9, 4)] = a => FpuEnv((e, c) => FpuLdEnv(e, c, a)),                        // FLDENV
            [(0xD9, 5)] = a => FpuEnv((e, c) => c.fpu_cw = EnvGetMemoryData16(e, a)),       // FLDCW
            [(0xD9, 6)] = a => FpuEnv((e, c) => { FpuStEnv(e, c, a); c.fpu_cw |= 0x3F; }),  // FNSTENV(例外を全マスク)
            [(0xD9, 7)] = a => FpuEnv((e, c) => EnvWriteWord(e, a, c.fpu_cw)),             // FNSTCW
            [(0xDB, 0)] = a => Load(I32At(a)), [(0xDB, 2)] = a => Store(I32At(a)), [(0xDB, 3)] = a => Store(I32At(a), pop: true),
            [(0xDB, 5)] = a => Load(F80At(a)), [(0xDB, 7)] = a => Store(F80At(a), pop: true),
            [(0xDD, 0)] = a => Load(F64At(a)), [(0xDD, 2)] = a => Store(F64At(a)), [(0xDD, 3)] = a => Store(F64At(a), pop: true),
            [(0xDD, 4)] = a => FpuEnv((e, c) => FpuRstor(e, c, a)),                         // FRSTOR
            [(0xDD, 6)] = a => FpuEnv((e, c) => FpuSave(e, c, a)),                          // FNSAVE
            [(0xDD, 7)] = a => FpuEnv((e, c) => EnvWriteWord(e, a, FpuSw(c))),              // FNSTSW m16
            [(0xDF, 0)] = a => Load(I16At(a)), [(0xDF, 2)] = a => Store(I16At(a)), [(0xDF, 3)] = a => Store(I16At(a), pop: true),
            [(0xDF, 5)] = a => Load(I64At(a)), [(0xDF, 7)] = a => Store(I64At(a), pop: true),
        };
        // 算術グループ: D8 = f32、DA = int32、DC = f64、DE = int16 のメモリオペランドと ST0
        foreach (var (op, source) in new (int, Func<uint, Real>)[] { (0xD8, F32At), (0xDA, I32At), (0xDC, F64At), (0xDE, I16At) })
            for (var kind = 0; kind < 8; kind++)
            {
                var (k, src) = (kind, source);
                table[(op, kind)] = a => Arith(k, src(a), St0, pop: false);
            }
        return table;
    }

    // レジスタ形式のうち rm まで固定の命令: (op, reg, rm) → 命令。
    static readonly Dictionary<(int op, int reg, int rm), State<Unit>> FpuFixedForms = new()
    {
        [(0xD9, 2, 0)] = FpuNop,                                                      // FNOP
        [(0xD9, 4, 0)] = Unary(x => -x),                                              // FCHS
        [(0xD9, 4, 1)] = Unary(Math.Abs),                                             // FABS
        [(0xD9, 4, 4)] = Compare(Zero),                                               // FTST
        [(0xD9, 4, 5)] = Fpu(FpuExamine),                                             // FXAM
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
        [(0xD9, 6, 4)] = Fpu(FpuExtract),                                             // FXTRACT
        [(0xD9, 6, 5)] = Compute(St0, St1, Math.IEEERemainder).Then(ClearC2),         // FPREM1
        [(0xD9, 6, 6)] = Fpu(c => c.fpu_top = (c.fpu_top - 1) & 7),                   // FDECSTP
        [(0xD9, 6, 7)] = Fpu(c => c.fpu_top = (c.fpu_top + 1) & 7),                   // FINCSTP
        [(0xD9, 7, 0)] = Fpu(FpuRemainder),                                           // FPREM
        [(0xD9, 7, 1)] = Compute(St1, St1, (x, y) => y * Math.Log2(x + 1.0), pops: 1), // FYL2XP1
        [(0xD9, 7, 2)] = Unary(Math.Sqrt),                                            // FSQRT
        [(0xD9, 7, 3)] = Fpu(FpuSinCos),                                              // FSINCOS
        [(0xD9, 7, 4)] = Fpu(c => StSet(c, 0, FpuRound(c, St(c, 0)))),                // FRNDINT
        [(0xD9, 7, 5)] = Compute(St0, St1, (x, y) => x * Math.Pow(2.0, Math.Truncate(y))), // FSCALE
        [(0xD9, 7, 6)] = Unary(Math.Sin).Then(ClearC2),                               // FSIN
        [(0xD9, 7, 7)] = Unary(Math.Cos).Then(ClearC2),                               // FCOS
        [(0xDA, 5, 1)] = Compare(St1, pops: 2),                                       // FUCOMPP
        [(0xDB, 4, 2)] = Fpu(c => c.fpu_sw &= 0x7F00),                                // FNCLEX
        [(0xDB, 4, 3)] = Fpu(FpuInit),                                                // FNINIT
        [(0xDE, 3, 1)] = Compare(St1, pops: 2),                                       // FCOMPP
        [(0xDF, 4, 0)] = Fpu(c => c.ax = FpuSw(c)),                                   // FNSTSW AX
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
            [(0xDD, 0)] = i => Fpu(c => c.fpu_valid &= (byte)~(1 << StIdx(c, i))), // FFREE
            [(0xDD, 2)] = i => Store(StReal(i)),                                  // FST ST(i)
            [(0xDD, 3)] = i => Store(StReal(i), pop: true),                       // FSTP ST(i)
            [(0xDD, 4)] = i => Compare(StReal(i)),                                // FUCOM
            [(0xDD, 5)] = i => Compare(StReal(i), pops: 1),                       // FUCOMP
            [(0xDF, 5)] = i => CompareEflags(StReal(i), pops: 1),                 // FUCOMIP
            [(0xDF, 6)] = i => CompareEflags(StReal(i), pops: 1),                 // FCOMIP
        };
        for (var kind = 0; kind < 8; kind++)
        {
            var k = kind;
            table[(0xD8, kind)] = i => Arith(k, StReal(i), St0, pop: false);           // ST0 <- ST0 op ST(i)
            table[(0xDC, kind)] = i => Arith(k, StReal(i), StReal(i), pop: false);     // ST(i) <- ST(i) op ST0
            if (kind != 3) table[(0xDE, kind)] = i => Arith(k, StReal(i), StReal(i), pop: true); // …して pop(FCOMPP は固定形式)
        }
        return table;
    }

    static readonly State<Unit> FpuUndefined = (env, cpu, ope) => (false, default, cpu, string.Empty);

    // ModRM で表を引く。未定義のエンコーディングは失敗(STOP 診断に載り、Execute が EIP を巻き戻す)。
    static State<Unit> FpuInstruction(int op, int mod, int reg, int rm) =>
        mod != 3
            ? FpuMemoryForms.TryGetValue((op, reg), out var memory)
                ? (from address in GetMemOrRegAddr(mod, rm) from _ in memory(address.addr) select unit)
                : FpuUndefined
            : FpuFixedForms.TryGetValue((op, reg, rm), out var fixedForm) ? fixedForm
            : FpuRegisterForms.TryGetValue((op, reg), out var register) ? register(rm)
            : FpuUndefined;

    // --- 本体 ----------------------------------------------------------------
    static public State<Unit> Fpu_D8_DF =>
        from ope in Opecodes
        from m in ModRegRm()
        from _ in FpuInstruction(ope[0], m.mod, m.reg, m.rm)
        select unit;
}

// FPU のオペランド(Lens): double として読む State と書き戻す関数の組。
public readonly record struct Real(State<double> Read, Func<double, State<Unit>> Write);
