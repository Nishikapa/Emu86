using static Emu86.Ext;

namespace Emu86;

// オペランド(Lens): 読み出す State と書き戻す関数の組。汎用レジスタ、ModRM で決まる r/m(メモリまたはレジスタ)、
// 読み出した即値などを同じ形で扱う。d ビットで向きが決まる命令(ALU の 00-3B、MOV の 88-8B)は、
// オペランドを入れ替えるだけで書ける。
public readonly record struct Operand(State<Data> Read, Func<Data, State<Unit>> Write);

static public partial class Ext
{
    // 汎用レジスタ reg(type: 0=8 ビット、1=16 ビット、2=32 ビット)。
    static public Operand Reg(int reg, int type) =>
        new(GetRegData(reg, type), data => SetRegData(reg, data));

    // ModRM の r/m(実効アドレスはデコード済み)。
    static public Operand Rm(MemAddr addr, int type) =>
        new(Choice(type, GetMemOrRegData8(addr), GetMemOrRegData16(addr), GetMemOrRegData32(addr)), data => SetMemOrRegData(addr, data));

    // 読み出し済みの値(即値など)。書き戻しはできない。
    static public Operand Const(Data data) =>
        new(data.ToState(), _ => throw new InvalidOperationException("constant operand is read-only"));

    // dst <- src
    static public State<Unit> Move(Operand dst, Operand src) =>
        from value in src.Read
        from _ in dst.Write(value)
        select Unit.unit;

    // dst <- dst op src。kind は Calc と同じ(0=ADD 1=OR 2=ADC 3=SBB 4=AND 5=SUB 6=XOR 7=CMP)。
    static public State<Unit> Binary(Operand dst, Operand src, int kind) =>
        from left in dst.Read
        from right in src.Read
        from result in Calc(left, right, kind)
        from _ in dst.Write(result)
        select Unit.unit;

    // x <-> y
    static public State<Unit> Swap(Operand x, Operand y) =>
        from a in x.Read
        from b in y.Read
        from _1 in x.Write(b)
        from _2 in y.Write(a)
        select Unit.unit;
}
