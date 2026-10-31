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

    // LEAVE (0xC9): (E)SP <- (E)BP; (E)BP <- pop()。スタックフレームを破棄する。
    static State<Unit> Leave_C9 =>
        from _1 in SetLog("Leave_C9")
        from type in OperandType(true)
        from _2 in SetCpu(cpu => { if (cpu.stack32) { cpu.esp = cpu.ebp; } else { cpu.sp = cpu.bp; } return cpu; })
        from val in Pop(type)
        from _3 in type == 2 ? _ebp.Set(val.dd) : _bp.Set(val.dw)
        select unit;

    // PUSH/POP FS/GS (0F A0/A1/A8/A9)。オペランドサイズ幅で push/pop する。
    //   A0=PUSH FS, A1=POP FS, A8=PUSH GS, A9=POP GS。セグメント番号 FS=4, GS=5。
    static State<Unit> PushPopFsGs =>
        from _1 in SetLog("PushPopFsGs")
        from opecode in Opecodes
        let reg = (opecode[1] & 0x08) != 0 ? 5 : 4   // A8/A9=GS, A0/A1=FS
        let pop = (opecode[1] & 0x01) != 0           // A1/A9=POP
        from type in OperandType(true)
        from _2 in pop ? PopSregInto(reg, type) : PushSregOf(reg, type)
        select unit;

    // セグメントレジスタをオペランドサイズ幅で push する(32 ビットならセレクタをゼロ拡張)/ pop して下位 16 ビットをロードする。
    static State<Unit> PushSregOf(int reg, int type) =>
        from selector in GetSRegData(reg)
        from _ in Push(((uint)selector).ToTypeData(type))
        select unit;

    internal static State<Unit> PopSregInto(int reg, int type) =>
        from value in Pop(type)
        from _ in LoadSReg(reg, (ushort)value.Value)
        select unit;

    // PUSH Sreg (06/0E/16/1E): opcode>>3 が ES/CS/SS/DS の順のセグメント番号になる。
    static State<Unit> PushSreg =>
        from _1 in SetLog("PushSreg")
        from opecode in Opecodes
        from type in OperandType(true)
        from _2 in PushSregOf(opecode[0] >> 3, type)
        select unit;

    // POP Sreg (07/17/1F)。オペランドサイズ幅で pop し、下位16ビットをロードする。
    // プロテクトモードでは記述子を再ロードする。
    static State<Unit> PopSreg =>
        from _1 in SetLog("PopSreg")
        from opecode in Opecodes
        from type in OperandType(true)
        from _2 in PopSregInto(opecode[0] >> 3, type)
        select unit;

    // PUSH r (50-57) / POP r (58-5F): スタックとレジスタの間の Move。
    static State<Unit> Push_50_57 =>
        from _1 in SetLog("Push_50_57")
        from opecode in Opecodes
        from type in OperandType(true)
        from _2 in Move(Stack(type), Reg(opecode[0] & 0x07, type))
        select unit;

    static State<Unit> Pop_58_5F =>
        from _1 in SetLog("Pop_58_5F")
        from opecode in Opecodes
        from type in OperandType(true)
        from _2 in Move(Reg(opecode[0] & 0x07, type), Stack(type))
        select unit;

    // POP r/m (0x8F /0)
    static State<Unit> Pop_8F =>
        from _1 in SetLog("Pop_8F")
        from m in ModRegRm()
        where 0 == m.reg
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandType(true)
        from _3 in Move(Rm(addr, type), Stack(type))
        select unit;

    // PUSH imm16/imm32 (0x68): オペランドサイズで imm を読み、その幅で push する。
    static State<Unit> PushImm_68 =>
        from _1 in SetLog("PushImm_68")
        from type in OperandType(true)
        from value in GetMemoryDataIp_(type)
        from _2 in Push(value)
        select unit;

    // PUSH imm8 (0x6A): imm8 を符号拡張し、オペランドサイズ幅で push する。
    static State<Unit> PushImm_6A =>
        from _1 in SetLog("PushImm_6A")
        from type in OperandType(true)
        from imm in Imm8SignExtended(type)
        from _2 in Push(imm)
        select unit;

    // PUSHF/POPF はオペランドサイズに従う(32ビットコードでは EFLAGS 全体を 4 バイトで push/pop)。
    static State<Unit> Pushf_9C =>
        from _1 in SetLog("Pushf_9C")
        from type in OperandType(true)
        from fl in GetDataFromCpu(cpu => cpu.eflags)
        from _2 in Push(fl.ToTypeData(type))
        select unit;

    static State<Unit> Popf_9D =>
        from _1 in SetLog("Popf_9D")
        from type in OperandType(true)
        from fl in Pop(type)
        from _2 in SetCpu(cpu => { PopFlags(cpu, type, fl.Value); return cpu; }) // CPL>0 の IOPL/IF の保護は Alu.PopFlags(IR と共有)
        select unit;
}
