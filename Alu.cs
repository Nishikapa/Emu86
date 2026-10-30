using static Emu86.Ext;

namespace Emu86;

static public class Alu
{
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
}
