using static Emu86.CPU;
using static Emu86.States;
using static Emu86.DataAccess;
using static Emu86.Conversions;

namespace Emu86;

// ビット操作命令(BT 系、BSF/BSR)。
static public class BitOps
{
    // BT系: r/m の bit 番目を CF にコピーし、op に応じて値を変更して書き戻す。
    //   op: 0=BT(変更なし) 1=BTS(セット) 2=BTR(クリア) 3=BTC(反転)
    static public State<Unit> BitTest(MemAddr addr, Data data, int bit, int op) =>
        from _f in _cf.Set(((data.type == 1 ? data.dw : data.dd) & (1u << (bit & (data.type == 1 ? 15 : 31)))) != 0)
        from _w in Unless(op == 0, SetMemOrRegData(addr, BitModify(data, bit, op)))
        select Unit.unit;

    static Data BitModify(Data data, int bit, int op)
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

    // BSF/BSR: r/m 内のセットビット位置を求めて reg へ書く(実効オペランドサイズに従う)。
    //   forward=true で最下位(BSF)、false で最上位(BSR)。
    //   ソースが 0 なら ZF=1 とし、結果は書き込まない（未定義）。
    static public State<Unit> BitScan(int reg, Data src, bool forward) =>
        src.Value == 0
            ? _zf.Set(true)
            : from _z in _zf.Set(false)
              from _w in SetRegData(reg, ((uint)BitScanIndex(src.Value, forward, Bits(src.type))).ToTypeData(src.type))
              select Unit.unit;

    static internal int BitScanIndex(uint src, bool forward, int bits)
    {
        if (forward)
        {
            for (int i = 0; i < bits; i++)
                if ((src & (1u << i)) != 0) return i;
        }
        else
        {
            for (int i = bits - 1; i >= 0; i--)
                if ((src & (1u << i)) != 0) return i;
        }
        return 0;
    }
}
