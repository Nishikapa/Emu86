using static Emu86.Unit;
using static Emu86.Operands;
using static Emu86.DataAccess;
using static Emu86.CpuStates;
using static Emu86.Fetch;

namespace Emu86;

// ポート入出力命令: IN/OUT(即値ポートと DX)。
static partial class Instructions
{
    // IN acc, port / OUT port, acc。port は imm8(E4-E7)か DX(EC-EF)。ポートはオペランド(Port: 読むと IN、書くと OUT)。
    // 幅は width で与える: imm8 形式は w ビットだけで決まり(8/16 ビット)、DX 形式はオペランドサイズに従う。
    static State<Unit> In(string name, State<int> width, State<int> port) =>
        from _1 in SetLog(name)
        from type in width
        from _2 in Move(Reg(0, type), Port(port, type))
        select unit;

    static State<Unit> Out(string name, State<int> width, State<int> port) =>
        from _1 in SetLog(name)
        from type in width
        from _2 in Move(Port(port, type), Reg(0, type))
        select unit;

    static State<int> ByteOrWord => Opecodes.Select(opecode => opecode[0] & 0x01);

    static State<int> PortImm8 => GetMemoryDataIp8.Select(port => (int)port);

    static State<Unit> In_E4_E5 => In("In_E4_E5", ByteOrWord, PortImm8);

    static State<Unit> Out_E6_E7 => Out("Out_E6_E7", ByteOrWord, PortImm8);

    static State<Unit> In_EC_ED => In("In_EC_ED", OperandTypeW, Dx);

    static State<Unit> Out_EE_EF => Out("Out_EE_EF", OperandTypeW, Dx);
}
