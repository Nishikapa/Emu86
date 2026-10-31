using static Emu86.Conversions;

namespace Emu86;

static public class Alu
{
    // 算術/論理グループを uint 上で幅共通に計算する。
    // kind: 0=ADD 1=OR 2=ADC 3=SBB 4=AND 5=SUB 6=XOR 7=CMP
    // ADC/SBB のキャリー入力はオペランドに畳み込まず個別に扱う
    // (b=0xFF..FF かつ CF=1 のときにキャリー出力が失われるのを防ぐ)。
    static internal uint CalculateAlu(CPU cpu, int type, uint left, uint right, int kind)
    {
        var mask = Mask(type);
        var signBit = Msb(type);
        left &= mask;
        right &= mask;
        var carryIn = kind is 2 or 3 && cpu.cf ? 1u : 0u;
        var result = kind switch
        {
            0 or 2 => (left + right + carryIn) & mask,
            1 => left | right,
            3 or 5 => (left - right - carryIn) & mask,
            4 => left & right,
            6 => left ^ right,
            _ => left,
        };
        var isAdd = kind is 0 or 2;
        var isSub = kind is 3 or 5 or 7;
        var flagResult = isSub ? (left - right - carryIn) & mask : result;
        SetArithmeticFlags(cpu,
            cf: isAdd ? (ulong)left + right + carryIn > mask : isSub && (ulong)right + carryIn > left,
            pf: Par(flagResult),
            af: (isAdd || isSub) && ((left ^ right ^ flagResult) & 0x10) != 0,
            zf: flagResult == 0,
            sf: (flagResult & signBit) != 0,
            of: isAdd ? ((left ^ right) & signBit) == 0 && ((left ^ flagResult) & signBit) != 0
                      : isSub && ((left ^ right) & signBit) != 0 && ((left ^ flagResult) & signBit) != 0);
        return result;
    }

    // CF/PF/AF/ZF/SF/OF をまとめて 1 回で書く(フラグごとのプロパティ更新より速く、結果は同じ)。
    const uint ArithmeticFlags = 0x8D5;
    static internal void SetArithmeticFlags(CPU cpu, bool cf, bool pf, bool af, bool zf, bool sf, bool of) =>
        cpu.eflags = (cpu.eflags & ~ArithmeticFlags)
            | (cf ? 0x1u : 0) | (pf ? 0x4u : 0) | (af ? 0x10u : 0) | (zf ? 0x40u : 0) | (sf ? 0x80u : 0) | (of ? 0x800u : 0);

    static internal void SetSubtractionFlags(CPU cpu, int type, uint left, uint right)
    {
        var mask = Mask(type);
        var signBit = Msb(type);
        left &= mask;
        right &= mask;
        var result = (left - right) & mask;
        SetArithmeticFlags(cpu,
            cf: left < right,
            pf: Par(result),
            af: ((left ^ right ^ result) & 0x10) != 0,
            zf: left == right,
            sf: (result & signBit) != 0,
            of: ((left ^ right) & signBit) != 0 && ((left ^ result) & signBit) != 0);
    }

    static internal void SetLogicFlags(CPU cpu, int type, uint result)
    {
        result &= Mask(type);
        SetArithmeticFlags(cpu, cf: false, pf: Par(result), af: false, zf: result == 0, sf: (result & Msb(type)) != 0, of: false);
    }

    static internal void SetIncDecFlags(CPU cpu, int type, uint value, int delta)
    {
        var signBit = Msb(type);
        var result = (uint)(value + delta) & Mask(type);
        // CF は変えない。
        cpu.eflags = (cpu.eflags & ~(ArithmeticFlags & ~0x1u))
            | (Par(result) ? 0x4u : 0) | (((value ^ result) & 0x10) != 0 ? 0x10u : 0) | (result == 0 ? 0x40u : 0)
            | ((result & signBit) != 0 ? 0x80u : 0) | ((value & Mask(type)) == (delta > 0 ? signBit - 1 : signBit) ? 0x800u : 0);
    }

    static internal bool EvaluateCondition(CPU cpu, int condition) => condition switch
    {
        0 => cpu.of,
        1 => !cpu.of,
        2 => cpu.cf,
        3 => !cpu.cf,
        4 => cpu.zf,
        5 => !cpu.zf,
        6 => cpu.cf || cpu.zf,
        7 => !cpu.cf && !cpu.zf,
        8 => cpu.sf,
        9 => !cpu.sf,
        10 => cpu.pf,
        11 => !cpu.pf,
        12 => cpu.sf != cpu.of,
        13 => cpu.sf == cpu.of,
        14 => cpu.zf || cpu.sf != cpu.of,
        15 => cpu.sf == cpu.of && !cpu.zf,
        _ => throw new IndexOutOfRangeException(),
    };

    // PF: 結果の下位 8 ビットの 1 の個数が偶数なら 1。
    static public bool Par(uint v) => (System.Numerics.BitOperations.PopCount(v & 0xFF) & 1) == 0;

    // DIV/IDIV: AX/AL、DX:AX/AX、EDX:EAX/EAX を商と剰余で上書きする。0 除算は例外(ランナーが停止する)。
    static internal void Divide(CPU c, int type, uint divisor, bool signed)
    {
        if (divisor == 0) throw new DivideByZeroException();
        if (!signed)
        {
            if (type == 0) { uint dividend = c.ax; c.al = (byte)(dividend / divisor); c.ah = (byte)(dividend % divisor); }
            else if (type == 1) { var dividend = ((uint)c.dx << 16) | c.ax; c.ax = (ushort)(dividend / divisor); c.dx = (ushort)(dividend % divisor); }
            else { var dividend = ((ulong)c.edx << 32) | c.eax; c.eax = (uint)(dividend / divisor); c.edx = (uint)(dividend % divisor); }
            return;
        }
        if (type == 0) { int dividend = (short)c.ax; c.al = (byte)(sbyte)(dividend / (sbyte)(byte)divisor); c.ah = (byte)(sbyte)(dividend % (sbyte)(byte)divisor); }
        else if (type == 1) { var dividend = (int)(((uint)c.dx << 16) | c.ax); c.ax = (ushort)(short)(dividend / (short)(ushort)divisor); c.dx = (ushort)(short)(dividend % (short)(ushort)divisor); }
        else { var dividend = (long)(((ulong)c.edx << 32) | c.eax); c.eax = (uint)(dividend / (int)divisor); c.edx = (uint)(dividend % (int)divisor); }
    }

    // BCD 調整: 0 AAA、1 AAS、2 DAA、3 DAS、4 AAM(基数 imm。0 は呼び出し側で #DE)、5 AAD。
    static internal void AdjustDecimal(CPU cpu, int kind, uint imm)
    {
        switch (kind)
        {
            case 0 or 1:
            {
                var adjust = (cpu.al & 0x0F) > 9 || cpu.af;
                if (adjust)
                {
                    if (kind == 0) { cpu.al = (byte)(cpu.al + 6); cpu.ah = (byte)(cpu.ah + 1); }
                    else { cpu.al = (byte)(cpu.al - 6); cpu.ah = (byte)(cpu.ah - 1); }
                }
                cpu.af = adjust; cpu.cf = adjust;
                cpu.al = (byte)(cpu.al & 0x0F);
                return;
            }
            case 2 or 3:
            {
                byte old = cpu.al; var subtract = kind == 3;
                var af = (old & 0x0F) > 9 || cpu.af;
                var cf = cpu.cf || old > 0x99;
                int al = old;
                if (af) al = subtract ? al - 6 : al + 6;
                if (cf) al = subtract ? al - 0x60 : al + 0x60;
                cpu.al = (byte)al;
                cpu.af = af; cpu.cf = cf;
                break;
            }
            case 4: { byte al = cpu.al; cpu.ah = (byte)(al / imm); cpu.al = (byte)(al % imm); break; }
            default: cpu.al = (byte)(cpu.al + cpu.ah * imm); cpu.ah = 0; break;
        }
        cpu.zf = cpu.al == 0; cpu.sf = (cpu.al & 0x80) != 0; cpu.pf = Par(cpu.al);
    }

    // POPF: CPL>0(V86 モードも CPL=3)では IOPL は変更不可、CPL>IOPL なら IF も変更不可。VM/RF は POPF では変わらない。
    static internal void PopFlags(CPU cpu, int type, uint value)
    {
        var next = type == 2 ? value : (cpu.eflags & 0xFFFF0000) | (value & 0xFFFF);
        if (cpu.pe && cpu.cpl != 0)
        {
            const uint IOPL = 0x3000, IF = 0x200, VM = 0x20000, RF = 0x10000;
            var keep = IOPL | VM | RF;
            if (cpu.cpl > ((cpu.eflags >> 12) & 3)) keep |= IF;
            next = (next & ~keep) | (cpu.eflags & keep);
        }
        cpu.eflags = next;
    }

    // SAHF: FLAGS の下位 8 ビットを AH で置き換える(bit1 は常に 1)。
    static internal void Sahf(CPU cpu) => cpu.eflags = (cpu.eflags & 0xFFFFFF00) | cpu.ah | 0x02u;

    // SHLD/SHRD の結果と CF/OF(count は 1..bits-1 を想定。16 ビットで count>=16 は未定義なので bits に丸める)。
    static internal (uint result, bool cf, bool of) DoubleShift(uint dst, uint src, int count, int bits, bool left)
    {
        uint mask = bits == 16 ? 0xFFFFu : 0xFFFFFFFFu;
        uint msb = bits == 16 ? 0x8000u : 0x80000000u;
        ulong d = dst & mask, s = src & mask;
        int rest = bits - count;
        if (rest <= 0) rest = bits;
        uint result;
        bool cf;
        if (left)
        {
            result = (uint)(((d << count) | (s >> rest)) & mask);
            cf = ((dst >> rest) & 1) != 0;         // dst から押し出された最後のビット
        }
        else
        {
            result = (uint)(((d >> count) | (s << rest)) & mask);
            cf = ((dst >> (count - 1)) & 1) != 0;
        }
        bool of = count == 1 && (((result & msb) != 0) != ((dst & msb) != 0));
        return (result, cf, of);
    }

    // Group2 シフト/ローテートを 1bit ずつ count 回適用する。
    // kind: 0=ROL 1=ROR 2=RCL 3=RCR 4=SHL/SAL 5=SHR 6=SAL 7=SAR
    static internal (uint result, bool cf, bool of) ComputeShift(uint v, int count, int kind, int bits, bool cfIn)
    {
        var mask = bits == 8 ? 0xFFu : bits == 16 ? 0xFFFFu : 0xFFFFFFFFu;
        var msb = 1u << (bits - 1);
        var result = v & mask;
        var cf = cfIn;
        for (int i = 0; i < count; i++)
        {
            switch (kind)
            {
                case 0: // ROL
                    cf = (result & msb) != 0;
                    result = ((result << 1) | (cf ? 1u : 0u)) & mask;
                    break;
                case 1: // ROR
                    cf = (result & 1) != 0;
                    result = ((result >> 1) | (cf ? msb : 0u)) & mask;
                    break;
                case 2: // RCL
                    {
                        var newcf = (result & msb) != 0;
                        result = ((result << 1) | (cf ? 1u : 0u)) & mask;
                        cf = newcf;
                    }
                    break;
                case 3: // RCR
                    {
                        var newcf = (result & 1) != 0;
                        result = ((result >> 1) | (cf ? msb : 0u)) & mask;
                        cf = newcf;
                    }
                    break;
                case 4: // SHL / SAL
                case 6:
                    cf = (result & msb) != 0;
                    result = (result << 1) & mask;
                    break;
                case 5: // SHR
                    cf = (result & 1) != 0;
                    result = (result >> 1) & mask;
                    break;
                case 7: // SAR (算術右シフト: 符号ビットを保持)
                    cf = (result & 1) != 0;
                    result = ((result >> 1) | (result & msb)) & mask;
                    break;
            }
        }
        // OF は count==1 のときのみ定義される。
        var of = false;
        if (count == 1)
        {
            of = kind switch
            {
                0 or 2 or 4 or 6 => ((result & msb) != 0) ^ cf,                          // ROL/RCL/SHL
                1 or 3 => ((result & msb) != 0) ^ ((result & (msb >> 1)) != 0),          // ROR/RCR: 上位2bit
                5 => (v & msb) != 0,                                                      // SHR: 元の MSB
                _ => false,                                                              // SAR
            };
        }
        return (result, cf, of);
    }
}
