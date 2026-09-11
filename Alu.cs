namespace Emu86;

static public partial class Ext
{
    static uint CalculateAlu(CPU cpu, int type, uint left, uint right, int kind)
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
        cpu.cf = isAdd ? (ulong)left + right + carryIn > mask : isSub && (ulong)right + carryIn > left;
        cpu.zf = flagResult == 0;
        cpu.sf = (flagResult & signBit) != 0;
        cpu.of = isAdd ? ((left ^ right) & signBit) == 0 && ((left ^ flagResult) & signBit) != 0
                      : isSub && ((left ^ right) & signBit) != 0 && ((left ^ flagResult) & signBit) != 0;
        cpu.pf = Par(flagResult);
        cpu.af = (isAdd || isSub) && ((left ^ right ^ flagResult) & 0x10) != 0;
        return result;
    }

    static void SetSubtractionFlags(CPU cpu, int type, uint left, uint right)
    {
        var mask = Mask(type);
        var signBit = Msb(type);
        left &= mask;
        right &= mask;
        var result = (left - right) & mask;
        cpu.cf = left < right;
        cpu.zf = left == right;
        cpu.sf = (result & signBit) != 0;
        cpu.of = ((left ^ right) & signBit) != 0 && ((left ^ result) & signBit) != 0;
        cpu.pf = Par(result);
        cpu.af = ((left ^ right ^ result) & 0x10) != 0;
    }

    static void SetLogicFlags(CPU cpu, int type, uint result)
    {
        result &= Mask(type);
        cpu.cf = false;
        cpu.zf = result == 0;
        cpu.sf = (result & Msb(type)) != 0;
        cpu.of = false;
        cpu.pf = Par(result);
        cpu.af = false;
    }

    static void SetIncDecFlags(CPU cpu, int type, uint value, int delta)
    {
        var signBit = Msb(type);
        var result = (uint)(value + delta) & Mask(type);
        cpu.zf = result == 0;
        cpu.sf = (result & signBit) != 0;
        cpu.of = (value & Mask(type)) == (delta > 0 ? signBit - 1 : signBit);
        cpu.pf = Par(result);
        cpu.af = ((value ^ result) & 0x10) != 0;
    }

    static bool EvaluateCondition(CPU cpu, int condition) => condition switch
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
