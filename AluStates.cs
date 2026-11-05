using static Emu86.CPU;
using static Emu86.Conversions;
using static Emu86.States;
using static Emu86.Alu;
using static Emu86.CpuStates;

namespace Emu86;

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

    // 幅 index(0/1/2)に応じた変換(値 → Data)を選ぶ。GetRegData が使う。
    static public Func<T, Data> GetTypeData<T>(
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
        return GetDataFromCpu(cpu => CalculateAlu(cpu, d1.type, d1.Value, d2.Value, kind).ToTypeData(d1.type));
    }

    // Group2 を実行し、フラグを更新して結果を type タプルで返す。
    static public State<Data> Group2(
        Data data, int count, int kind) =>
        from cf0 in _cf.Get()
        let cnt = count & 0x1F   // 80386 はシフト量を 5bit にマスクする
        let res = ComputeShift(data.Value, cnt, kind, Bits(data.type), cf0)
        // count==0 のときはフラグを変更しない。シフト系(kind>=4)は ZF/SF も更新する。
        from _f in Unless(cnt == 0, kind >= 4
            ? SetCpu((_cf, res.cf), (_of, res.of), (_zf, res.result == 0), (_sf, (res.result & Msb(data.type)) != 0), (_pf, Par(res.result)))
            : SetCpu((_cf, res.cf), (_of, res.of)))
        select res.result.ToTypeData(data.type);

    static public State<bool> Jcc(int type) =>
        GetDataFromCpu(cpu => EvaluateCondition(cpu, type));

    // 論理演算の結果 data のフラグ(CF=OF=0、ZF/SF/PF は結果から)。TEST。
    static public State<Unit> update_eflags(Data data) =>
        Effect(cpu => SetLogicFlags(cpu, data.type, data.Value));

    // 減算 left - right(type の幅)のフラグ。CMP/NEG/CMPS/SCAS。
    static public State<Unit> update_eflags_sub(int type, uint left, uint right) =>
        Effect(cpu => SetSubtractionFlags(cpu, type, left, right));

    // INC/DEC(delta = +1/-1)のフラグ。CF は変えない。
    static public State<Unit> update_eflags_incdec(Data data, int delta) =>
        Effect(cpu => SetIncDecFlags(cpu, data.type, data.Value, delta));
}
