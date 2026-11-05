using static Emu86.Unit;
using static Emu86.Operands;
using static Emu86.DataAccess;
using static Emu86.CpuStates;
using static Emu86.Fetch;

namespace Emu86;

// ポート入出力命令: IN/OUT(即値ポートと DX)。
static partial class Instructions
{
    // IN acc, port / OUT port, acc: アキュムレータとポートの間の Move。ポートはオペランド(Port: 読むと IN、書くと OUT)で、
    // bit1 が向き(1 = OUT)。port は imm8(E4-E7)か DX(EC-EF)。
    // 幅は width で与える: imm8 形式は w ビットだけで決まり(8/16 ビット)、DX 形式はオペランドサイズに従う。
    static State<Unit> InOut(string name, State<int> width, State<int> port) =>
        from _1 in SetLog(name)
        from opecode in Opecodes
        from type in width
        let accumulator = Reg(0, type)
        let device = Port(port, type)
        from _2 in (opecode[0] & 0x02) != 0 ? Move(device, accumulator) : Move(accumulator, device)
        select unit;

    static State<int> ByteOrWord => Opecodes.Select(opecode => opecode[0] & 0x01);

    static State<int> PortImm8 => GetMemoryDataIp8.Select(port => (int)port);

    static State<Unit> InOut_E4_E7 => InOut("InOut_E4_E7", ByteOrWord, PortImm8);

    static State<Unit> InOut_EC_EF => InOut("InOut_EC_EF", OperandTypeW, Dx);
}
