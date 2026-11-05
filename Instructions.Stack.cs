using static Emu86.CPU;
using static Emu86.Conversions;
using static Emu86.Unit;
using static Emu86.Operands;
using static Emu86.States;
using static Emu86.DataAccess;
using static Emu86.Segments;
using static Emu86.CpuStates;
using static Emu86.Fetch;
using static Emu86.Decoder;
using static Emu86.Alu;

namespace Emu86;

// スタック命令: PUSH/POP の各形、PUSHA/POPA、ENTER/LEAVE、PUSHF/POPF。
static partial class Instructions
{
    // PUSHA/PUSHAD (0x60): AX,CX,DX,BX,(開始時 SP),BP,SI,DI をオペランドサイズ幅で push する。
    static State<Unit> Pusha_60 =>
        from _1 in SetLog("Pusha_60")
        from type in OperandType(true)
        from sp0 in GetRegData(4, type) // 開始時点の (E)SP
        from _2 in Enumerable.Range(0, 8)
            .Select(reg => Move(Stack(type), reg == 4 ? Const(sp0) : Reg(reg, type)))
            .Sequence()
            .Ignore()
        select unit;

    // POPA/POPAD (0x61): DI,SI,BP,(SP読み飛ばし),BX,DX,CX,AX の順でオペランドサイズ幅で pop する。SP は破棄。
    static State<Unit> Popa_61 =>
        from _1 in SetLog("Popa_61")
        from type in OperandType(true)
        from _2 in Enumerable.Sequence(7, 0, -1) // DI(7) から AX(0) へ
            .Select(reg => reg == 4 ? Pop(type).Ignore() : Move(Reg(reg, type), Stack(type)))
            .Sequence()
            .Ignore()
        select unit;

    // ENTER imm16, imm8 (0xC8): スタックフレームを構築する。
    static State<Unit> Enter_C8 =>
        from _1 in SetLog("Enter_C8")
        from alloc in GetMemoryDataIp16
        from level in GetMemoryDataIp8
        from _2 in Enter(alloc, level)
        select unit;

    // LEAVE (0xC9): (E)SP <- (E)BP(幅は SS.B); (E)BP <- pop()(幅はオペランドサイズ)。スタックフレームを破棄する。
    static State<Unit> Leave_C9 =>
        from _1 in SetLog("Leave_C9")
        from type in OperandType(true)
        from stackType in GetDataFromCpu(cpu => cpu.stack32 ? 2 : 1)
        from _2 in Move(Reg(4, stackType), Reg(5, stackType)) // SP <- BP
        from _3 in Move(Reg(5, type), Stack(type))            // BP <- pop
        select unit;

    // スタックとオペランドの間の Move(幅はオペランドサイズ): pop なら operand <- スタック、そうでなければ スタック <- operand。
    // PUSH と POP の対は、オペコードのどのビットが POP かと、相手のオペランドだけが違う。
    static State<Unit> PushPop(string name, Func<byte[], bool> pop, Func<byte[], int, Operand> operand) =>
        from _1 in SetLog(name)
        from opecode in Opecodes
        from type in OperandType(true)
        let stack = Stack(type)
        let target = operand(opecode, type)
        from _2 in pop(opecode) ? Move(target, stack) : Move(stack, target)
        select unit;

    // PUSH r (50-57) / POP r (58-5F): bit3 が POP、下位 3 ビットがレジスタ。
    static State<Unit> PushPop_50_5F =>
        PushPop("PushPop_50_5F", opecode => (opecode[0] & 0x08) != 0, (opecode, type) => Reg(opecode[0] & 0x07, type));

    // PUSH Sreg (06/0E/16/1E) / POP Sreg (07/17/1F): bit0 が POP、opcode>>3 が ES/CS/SS/DS の順のセグメント番号。
    // push は(32 ビットなら)セレクタをゼロ拡張し、pop は下位 16 ビットをロードする(プロテクトモードでは記述子を読み直す)。
    static State<Unit> PushPopSreg =>
        PushPop("PushPopSreg", opecode => (opecode[0] & 0x01) != 0, (opecode, type) => Sreg(opecode[0] >> 3, type));

    // PUSH/POP FS/GS (0F A0/A1/A8/A9): bit0 が POP、bit3 が GS(セグメント番号 FS=4、GS=5)。
    static State<Unit> PushPopFsGs =>
        PushPop("PushPopFsGs", opecode => (opecode[1] & 0x01) != 0, (opecode, type) => Sreg((opecode[1] & 0x08) != 0 ? 5 : 4, type));

    // PUSHF (9C) / POPF (9D): bit0 が POP。32 ビットコードでは EFLAGS 全体を 4 バイトで push/pop する。
    static State<Unit> PushfPopf_9C_9D =>
        PushPop("PushfPopf_9C_9D", opecode => (opecode[0] & 0x01) != 0, (_, type) => Flags(type));

    // セグメントレジスタを pop する(IR からも使う)。
    internal static State<Unit> PopSregInto(int reg, int type) => Move(Sreg(reg, type), Stack(type));

    // POP r/m (0x8F /0)
    static State<Unit> Pop_8F =>
        from _1 in SetLog("Pop_8F")
        from m in ModRegRm()
        where 0 == m.reg
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandType(true)
        from _3 in Move(Rm(addr, type), Stack(type))
        select unit;

    // PUSH imm: 即値をオペランドサイズの幅で読み、その幅で push する。0x68 は imm16/imm32、0x6A は符号拡張した imm8。
    static State<Unit> PushImmediate(string name, Func<int, State<Data>> immediate) =>
        from _1 in SetLog(name)
        from type in OperandType(true)
        from imm in immediate(type)
        from _2 in Push(imm)
        select unit;

    static State<Unit> PushImm_68 => PushImmediate("PushImm_68", GetMemoryDataIp);

    static State<Unit> PushImm_6A => PushImmediate("PushImm_6A", Imm8SignExtended);
}
