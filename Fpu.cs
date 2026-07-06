using static Emu86.CPU;
using static Emu86.Unit;

namespace Emu86;

// x87 FPU (0xD8-0xDF + WAIT)。
// 8 本のレジスタスタックを倍精度 double で保持する。実機の内部 80 ビット拡張精度とは
// 丸めが異なりうるが、カーネル/ユーザランドの通常用途には十分。
// FNSAVE/FRSTOR・m80 のロード/ストアは 80 ビット拡張実数と相互変換する。
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
        FWrite32(env, a, (uint)v);
        FWrite32(env, a + 4, (uint)(v >> 32));
    }

    static double FpuReadF32(EmuEnvironment env, uint a) =>
        BitConverter.Int32BitsToSingle((int)EnvGetMemoryData32(env, a));

    static double FpuReadF64(EmuEnvironment env, uint a) =>
        BitConverter.Int64BitsToDouble((long)FpuR64(env, a));

    static void FpuWriteF32(EmuEnvironment env, uint a, double v) =>
        FWrite32(env, a, (uint)BitConverter.SingleToInt32Bits((float)v));

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
        FWrite32(env, a + 0, c.fpu_cw);
        FWrite32(env, a + 4, FpuSw(c));
        FWrite32(env, a + 8, FpuTagWord(c));
        FWrite32(env, a + 12, 0); // fip(未追跡)
        FWrite32(env, a + 16, 0); // fcs/opcode
        FWrite32(env, a + 20, 0); // fdp
        FWrite32(env, a + 24, 0); // fds
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
            FWrite16(env, a + 28 + (uint)i * 10 + 8, se);
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

    // --- 算術グループ(D8/DA/DC/DE の reg フィールド共通) ----------------------
    //   0=ADD 1=MUL 2=COM 3=COMP 4=SUB 5=SUBR 6=DIV 7=DIVR (ST0 と src)
    static void FpuArith(CPU c, int reg, double src)
    {
        var st0 = St(c, 0);
        switch (reg)
        {
            case 0: StSet(c, 0, st0 + src); break;
            case 1: StSet(c, 0, st0 * src); break;
            case 2: FpuCompare(c, st0, src); break;
            case 3: FpuCompare(c, st0, src); FpuPop(c); break;
            case 4: StSet(c, 0, st0 - src); break;
            case 5: StSet(c, 0, src - st0); break;
            case 6: StSet(c, 0, st0 / src); break;
            case 7: StSet(c, 0, src / st0); break;
        }
    }

    // --- 本体 ----------------------------------------------------------------
    static public State<Unit> Fpu_D8_DF => (env, cpu0, ope) =>
    {
        var op = ope[0];
        var (okM, m, cpu, _) = ModRegRm()(env, cpu0, ope);
        if (!okM)
            return (false, default, cpu0, string.Empty);
        var (mod, reg, rm) = m;

        (bool, Unit, CPU, string) Ok() => (true, unit, cpu, string.Empty);
        (bool, Unit, CPU, string) Fail() => (false, default, cpu, string.Empty);

        if (mod != 3)
        {
            var (okA, maddr, cpu2, _) = GetMemOrRegAddr(mod, rm)(env, cpu, ope);
            if (!okA)
                return (false, default, cpu, string.Empty);
            cpu = cpu2;
            var a = maddr.addr;
            switch (op, reg)
            {
                // 算術(メモリオペランド)
                case (0xD8, _): FpuArith(cpu, reg, FpuReadF32(env, a)); return Ok();
                case (0xDC, _): FpuArith(cpu, reg, FpuReadF64(env, a)); return Ok();
                case (0xDA, _): FpuArith(cpu, reg, (int)EnvGetMemoryData32(env, a)); return Ok();
                case (0xDE, _): FpuArith(cpu, reg, (short)EnvGetMemoryData16(env, a)); return Ok();
                // ロード/ストア
                case (0xD9, 0): FpuPush(cpu, FpuReadF32(env, a)); return Ok();
                case (0xD9, 2): FpuWriteF32(env, a, St(cpu, 0)); return Ok();
                case (0xD9, 3): FpuWriteF32(env, a, St(cpu, 0)); FpuPop(cpu); return Ok();
                case (0xD9, 4): FpuLdEnv(env, cpu, a); return Ok();
                case (0xD9, 5): cpu.fpu_cw = EnvGetMemoryData16(env, a); return Ok();   // FLDCW
                case (0xD9, 6): FpuStEnv(env, cpu, a); cpu.fpu_cw |= 0x3F; return Ok(); // FNSTENV(例外を全マスク)
                case (0xD9, 7): FWrite16(env, a, cpu.fpu_cw); return Ok();              // FNSTCW
                case (0xDB, 0): FpuPush(cpu, (int)EnvGetMemoryData32(env, a)); return Ok();  // FILD m32
                case (0xDB, 2): FWrite32(env, a, FpuToInt32(cpu, St(cpu, 0))); return Ok();  // FIST m32
                case (0xDB, 3): FWrite32(env, a, FpuToInt32(cpu, St(cpu, 0))); FpuPop(cpu); return Ok();
                case (0xDB, 5): // FLD m80
                    FpuPush(cpu, F80ToDouble(FpuR64(env, a), EnvGetMemoryData16(env, a + 8)));
                    return Ok();
                case (0xDB, 7): // FSTP m80
                {
                    var (mant, se) = DoubleToF80(St(cpu, 0));
                    FpuW64(env, a, mant);
                    FWrite16(env, a + 8, se);
                    FpuPop(cpu);
                    return Ok();
                }
                case (0xDD, 0): FpuPush(cpu, FpuReadF64(env, a)); return Ok();
                case (0xDD, 2): FpuWriteF64(env, a, St(cpu, 0)); return Ok();
                case (0xDD, 3): FpuWriteF64(env, a, St(cpu, 0)); FpuPop(cpu); return Ok();
                case (0xDD, 4): FpuRstor(env, cpu, a); return Ok();                     // FRSTOR
                case (0xDD, 6): FpuSave(env, cpu, a); return Ok();                      // FNSAVE
                case (0xDD, 7): FWrite16(env, a, FpuSw(cpu)); return Ok();              // FNSTSW m16
                case (0xDF, 0): FpuPush(cpu, (short)EnvGetMemoryData16(env, a)); return Ok(); // FILD m16
                case (0xDF, 2): FWrite16(env, a, FpuToInt16(cpu, St(cpu, 0))); return Ok();
                case (0xDF, 3): FWrite16(env, a, FpuToInt16(cpu, St(cpu, 0))); FpuPop(cpu); return Ok();
                case (0xDF, 5): FpuPush(cpu, (long)FpuR64(env, a)); return Ok();        // FILD m64
                case (0xDF, 7): FpuW64(env, a, FpuToInt64(cpu, St(cpu, 0))); FpuPop(cpu); return Ok(); // FISTP m64
                default:
                    return Fail();
            }
        }

        // mod == 3: レジスタ形式
        switch (op)
        {
            case 0xD8: // ST0 op ST(rm)
                FpuArith(cpu, reg, St(cpu, rm));
                return Ok();

            case 0xD9:
                switch (reg, rm)
                {
                    case (0, _): FpuPush(cpu, St(cpu, rm)); return Ok();       // FLD ST(i)
                    case (1, _): // FXCH
                    {
                        var t = St(cpu, 0);
                        StSet(cpu, 0, St(cpu, rm));
                        StSet(cpu, rm, t);
                        return Ok();
                    }
                    case (2, 0): return Ok();                                   // FNOP
                    case (3, _): StSet(cpu, rm, St(cpu, 0)); FpuPop(cpu); return Ok(); // FSTP ST(i)(別名)
                    case (4, 0): StSet(cpu, 0, -St(cpu, 0)); return Ok();       // FCHS
                    case (4, 1): StSet(cpu, 0, Math.Abs(St(cpu, 0))); return Ok(); // FABS
                    case (4, 4): FpuCompare(cpu, St(cpu, 0), 0.0); return Ok(); // FTST
                    case (4, 5): // FXAM
                    {
                        var v = St(cpu, 0);
                        cpu.fpu_sw &= unchecked((ushort)~(FpuC0 | FpuC1 | FpuC2 | FpuC3));
                        if (double.IsNegative(v)) cpu.fpu_sw |= FpuC1;
                        if ((cpu.fpu_valid & (1 << StIdx(cpu, 0))) == 0) cpu.fpu_sw |= FpuC3 | FpuC0;
                        else if (double.IsNaN(v)) cpu.fpu_sw |= FpuC0;
                        else if (double.IsInfinity(v)) cpu.fpu_sw |= FpuC2 | FpuC0;
                        else if (v == 0) cpu.fpu_sw |= FpuC3;
                        else cpu.fpu_sw |= FpuC2; // normal(非正規化は区別しない)
                        return Ok();
                    }
                    case (5, 0): FpuPush(cpu, 1.0); return Ok();                          // FLD1
                    case (5, 1): FpuPush(cpu, 3.321928094887362); return Ok();            // FLDL2T
                    case (5, 2): FpuPush(cpu, 1.4426950408889634); return Ok();           // FLDL2E
                    case (5, 3): FpuPush(cpu, Math.PI); return Ok();                      // FLDPI
                    case (5, 4): FpuPush(cpu, 0.3010299956639812); return Ok();           // FLDLG2
                    case (5, 5): FpuPush(cpu, 0.6931471805599453); return Ok();           // FLDLN2
                    case (5, 6): FpuPush(cpu, 0.0); return Ok();                          // FLDZ
                    case (6, 0): StSet(cpu, 0, Math.Pow(2.0, St(cpu, 0)) - 1.0); return Ok(); // F2XM1
                    case (6, 1): StSet(cpu, 1, St(cpu, 1) * Math.Log2(St(cpu, 0))); FpuPop(cpu); return Ok(); // FYL2X
                    case (6, 2): StSet(cpu, 0, Math.Tan(St(cpu, 0))); FpuPush(cpu, 1.0); cpu.fpu_sw &= unchecked((ushort)~FpuC2); return Ok(); // FPTAN
                    case (6, 3): StSet(cpu, 1, Math.Atan2(St(cpu, 1), St(cpu, 0))); FpuPop(cpu); return Ok(); // FPATAN
                    case (6, 4): // FXTRACT: ST0 を指数と仮数に分解
                    {
                        var v = St(cpu, 0);
                        var e = Math.Floor(Math.Log2(Math.Abs(v)));
                        StSet(cpu, 0, e);
                        FpuPush(cpu, v / Math.Pow(2.0, e));
                        return Ok();
                    }
                    case (6, 5): // FPREM1(IEEE 剰余)
                        StSet(cpu, 0, Math.IEEERemainder(St(cpu, 0), St(cpu, 1)));
                        cpu.fpu_sw &= unchecked((ushort)~FpuC2);
                        return Ok();
                    case (6, 6): cpu.fpu_top = (cpu.fpu_top - 1) & 7; return Ok(); // FDECSTP
                    case (6, 7): cpu.fpu_top = (cpu.fpu_top + 1) & 7; return Ok(); // FINCSTP
                    case (7, 0): // FPREM(切り捨て剰余。商の下位ビットを C0/C3/C1 に返す)
                    {
                        var q = (long)(St(cpu, 0) / St(cpu, 1));
                        StSet(cpu, 0, St(cpu, 0) % St(cpu, 1));
                        cpu.fpu_sw &= unchecked((ushort)~(FpuC0 | FpuC1 | FpuC2 | FpuC3));
                        if ((q & 4) != 0) cpu.fpu_sw |= FpuC0;
                        if ((q & 2) != 0) cpu.fpu_sw |= FpuC3;
                        if ((q & 1) != 0) cpu.fpu_sw |= FpuC1;
                        return Ok();
                    }
                    case (7, 1): StSet(cpu, 1, St(cpu, 1) * Math.Log2(St(cpu, 0) + 1.0)); FpuPop(cpu); return Ok(); // FYL2XP1
                    case (7, 2): StSet(cpu, 0, Math.Sqrt(St(cpu, 0))); return Ok();       // FSQRT
                    case (7, 3): // FSINCOS
                    {
                        var v = St(cpu, 0);
                        StSet(cpu, 0, Math.Sin(v));
                        FpuPush(cpu, Math.Cos(v));
                        cpu.fpu_sw &= unchecked((ushort)~FpuC2);
                        return Ok();
                    }
                    case (7, 4): StSet(cpu, 0, FpuRound(cpu, St(cpu, 0))); return Ok();   // FRNDINT
                    case (7, 5): StSet(cpu, 0, St(cpu, 0) * Math.Pow(2.0, Math.Truncate(St(cpu, 1)))); return Ok(); // FSCALE
                    case (7, 6): StSet(cpu, 0, Math.Sin(St(cpu, 0))); cpu.fpu_sw &= unchecked((ushort)~FpuC2); return Ok(); // FSIN
                    case (7, 7): StSet(cpu, 0, Math.Cos(St(cpu, 0))); cpu.fpu_sw &= unchecked((ushort)~FpuC2); return Ok(); // FCOS
                    default: return Fail();
                }

            case 0xDA: // FCMOVcc / FUCOMPP
                switch (reg, rm)
                {
                    case (0, _): if (cpu.cf) StSet(cpu, 0, St(cpu, rm)); return Ok();               // FCMOVB
                    case (1, _): if (cpu.zf) StSet(cpu, 0, St(cpu, rm)); return Ok();               // FCMOVE
                    case (2, _): if (cpu.cf || cpu.zf) StSet(cpu, 0, St(cpu, rm)); return Ok();     // FCMOVBE
                    case (3, _): if (cpu.pf) StSet(cpu, 0, St(cpu, rm)); return Ok();               // FCMOVU
                    case (5, 1): FpuCompare(cpu, St(cpu, 0), St(cpu, 1)); FpuPop(cpu); FpuPop(cpu); return Ok(); // FUCOMPP
                    default: return Fail();
                }

            case 0xDB:
                switch (reg, rm)
                {
                    case (0, _): if (!cpu.cf) StSet(cpu, 0, St(cpu, rm)); return Ok();              // FCMOVNB
                    case (1, _): if (!cpu.zf) StSet(cpu, 0, St(cpu, rm)); return Ok();              // FCMOVNE
                    case (2, _): if (!cpu.cf && !cpu.zf) StSet(cpu, 0, St(cpu, rm)); return Ok();   // FCMOVNBE
                    case (3, _): if (!cpu.pf) StSet(cpu, 0, St(cpu, rm)); return Ok();              // FCMOVNU
                    case (4, 2): cpu.fpu_sw &= 0x7F00; return Ok();                                 // FNCLEX
                    case (4, 3): FpuInit(cpu); return Ok();                                         // FNINIT
                    case (5, _): FpuCompareEflags(cpu, St(cpu, 0), St(cpu, rm)); return Ok();       // FUCOMI
                    case (6, _): FpuCompareEflags(cpu, St(cpu, 0), St(cpu, rm)); return Ok();       // FCOMI
                    default: return Fail();
                }

            case 0xDC: // ST(rm) op ST0(SUB/DIV は D8 と逆向き)
                switch (reg)
                {
                    case 0: StSet(cpu, rm, St(cpu, rm) + St(cpu, 0)); return Ok();
                    case 1: StSet(cpu, rm, St(cpu, rm) * St(cpu, 0)); return Ok();
                    case 2: FpuCompare(cpu, St(cpu, 0), St(cpu, rm)); return Ok();
                    case 3: FpuCompare(cpu, St(cpu, 0), St(cpu, rm)); FpuPop(cpu); return Ok();
                    case 4: StSet(cpu, rm, St(cpu, 0) - St(cpu, rm)); return Ok(); // FSUBR
                    case 5: StSet(cpu, rm, St(cpu, rm) - St(cpu, 0)); return Ok(); // FSUB
                    case 6: StSet(cpu, rm, St(cpu, 0) / St(cpu, rm)); return Ok(); // FDIVR
                    case 7: StSet(cpu, rm, St(cpu, rm) / St(cpu, 0)); return Ok(); // FDIV
                    default: return Fail();
                }

            case 0xDD:
                switch (reg)
                {
                    case 0: cpu.fpu_valid &= (byte)~(1 << StIdx(cpu, rm)); return Ok(); // FFREE
                    case 2: StSet(cpu, rm, St(cpu, 0)); return Ok();                    // FST ST(i)
                    case 3: StSet(cpu, rm, St(cpu, 0)); FpuPop(cpu); return Ok();       // FSTP ST(i)
                    case 4: FpuCompare(cpu, St(cpu, 0), St(cpu, rm)); return Ok();      // FUCOM
                    case 5: FpuCompare(cpu, St(cpu, 0), St(cpu, rm)); FpuPop(cpu); return Ok(); // FUCOMP
                    default: return Fail();
                }

            case 0xDE: // 演算して pop(SUB/DIV は DC と同じ逆向き規約)
                switch (reg, rm)
                {
                    case (0, _): StSet(cpu, rm, St(cpu, rm) + St(cpu, 0)); FpuPop(cpu); return Ok(); // FADDP
                    case (1, _): StSet(cpu, rm, St(cpu, rm) * St(cpu, 0)); FpuPop(cpu); return Ok(); // FMULP
                    case (2, _): FpuCompare(cpu, St(cpu, 0), St(cpu, rm)); FpuPop(cpu); return Ok();
                    case (3, 1): FpuCompare(cpu, St(cpu, 0), St(cpu, 1)); FpuPop(cpu); FpuPop(cpu); return Ok(); // FCOMPP
                    case (4, _): StSet(cpu, rm, St(cpu, 0) - St(cpu, rm)); FpuPop(cpu); return Ok(); // FSUBRP
                    case (5, _): StSet(cpu, rm, St(cpu, rm) - St(cpu, 0)); FpuPop(cpu); return Ok(); // FSUBP
                    case (6, _): StSet(cpu, rm, St(cpu, 0) / St(cpu, rm)); FpuPop(cpu); return Ok(); // FDIVRP
                    case (7, _): StSet(cpu, rm, St(cpu, rm) / St(cpu, 0)); FpuPop(cpu); return Ok(); // FDIVP
                    default: return Fail();
                }

            case 0xDF:
                switch (reg, rm)
                {
                    case (4, 0): cpu.ax = FpuSw(cpu); return Ok();                                  // FNSTSW AX
                    case (5, _): FpuCompareEflags(cpu, St(cpu, 0), St(cpu, rm)); FpuPop(cpu); return Ok(); // FUCOMIP
                    case (6, _): FpuCompareEflags(cpu, St(cpu, 0), St(cpu, rm)); FpuPop(cpu); return Ok(); // FCOMIP
                    default: return Fail();
                }

            default:
                return Fail();
        }
    };
}
