using static Emu86.CPU;
using static Emu86.Conversions;
using static Emu86.Unit;
using static Emu86.States;
using static Emu86.DataAccess;
using static Emu86.Segments;
using static Emu86.Interrupts;
using static Emu86.CpuStates;
using static Emu86.Fetch;
using static Emu86.Decoder;
using static Emu86.AluStates;

namespace Emu86;

// 制御転送命令: 相対/絶対/far のジャンプとコール、リターン、LOOP/JCXZ、ソフトウェア割り込みと IRET。
static partial class Instructions
{
    // 命令ストリームの far ポインタ ptr16:16 / ptr16:32(offset が先、segment が後)。
    // 実効オペランドサイズが32ビット(66プレフィックスまたは32ビットコード)なら offset は 32 ビット。
    static State<(uint offset, ushort segment)> FarPointerIp(int type) =>
        from offset in type == 2 ? GetMemoryDataIp32 : GetMemoryDataIp16.Select(dw => (uint)dw)
        from segment in GetMemoryDataIp16
        select (offset, segment);

    // メモリの far ポインタ m16:16 / m16:32(offset の直後に selector)。
    static State<(uint offset, ushort segment)> FarPointerMem(MemAddr addr, int type) =>
        from offset in type == 2 ? GetMemOrRegData32(addr) : GetMemOrRegData16(addr).Select(dw => (uint)dw)
        from segment in GetMemOrRegData16((addr.isMem, addr.addr + (uint)(type == 2 ? 4 : 2)))
        select (offset, segment);

    // CS:EIP を target へ切り替える。CS のロードは LoadSReg 経由(プロテクトモードでは GDT 記述子から基底/D ビットを反映)。
    static State<Unit> FarTransfer((uint offset, ushort segment) target) =>
        from _1 in _eip.Set(target.offset)
        from _2 in LoadSReg(1, target.segment)
        select unit;

    // 戻り番地(現在の IP/EIP = 次命令)をオペランドサイズ幅で push する。
    static State<Unit> PushReturnNear(int type) =>
        from eip in _eip.Get()
        from _ in Push(eip.ToTypeData(type))
        select unit;

    // CS と戻り番地をオペランドサイズ幅で push する(far CALL のリターンフレーム)。
    static State<Unit> PushReturnFar(int type) =>
        from cs in GetSRegData(1)
        from _1 in Push(((uint)cs).ToTypeData(type))
        from _2 in PushReturnNear(type)
        select unit;

    // 近傍ジャンプ先の設定(type に応じて IP / EIP)。
    static State<Unit> JmpTo(Data target) =>
        2 == target.type ? _eip.Set(target.dd) : _ip.Set(target.dw);

    // 相対分岐 JMP rel8 (0xEB) / rel16・rel32 (0xE9)。
    static State<Unit> JumpRelative(string name, State<int> rel) =>
        from _1 in SetLog(name)
        from offset in rel
        from _2 in IpInc(offset)
        select unit;

    // 条件分岐 Jcc rel8 (70-7F) / rel16・rel32 (0F 80-8F)。条件はオペコード末尾バイトの下位 4 ビット。
    static State<Unit> ConditionalJump(string name, State<int> rel) =>
        from _1 in SetLog(name)
        from opecode in Opecodes
        from taken in Jcc(opecode[^1] & 0xF)
        from offset in rel
        from _2 in When(taken, IpInc(offset))
        select unit;

    static State<Unit> Jmp_EB => JumpRelative("Jmp_EB", Rel8);

    static State<Unit> Jump_E9 => JumpRelative("Jump_E9", RelNear);

    static State<Unit> Jcc_70_7F => ConditionalJump("Jcc_70_7F", Rel8);

    static State<Unit> Jcc_0F80_0F8F => ConditionalJump("Jcc_0F80_0F8F", RelNear);

    // JMP far ptr16:16 / ptr16:32 (0xEA)。
    static State<Unit> FarJump_EA =>
        from _1 in SetLog("FarJump_EA")
        from type in OperandType(true)
        from target in FarPointerIp(type)
        from _2 in FarTransfer(target)
        select unit;

    // CALL far ptr16:16 / ptr16:32 (0x9A): CS, (E)IP を push してから offset:segment へ far ジャンプ。
    // オペランドサイズに従う(32ビットコードではオフセットは 32 ビット、リターンフレームも 32 ビット幅)。
    static State<Unit> CallFar_9A =>
        from _1 in SetLog("CallFar_9A")
        from type in OperandType(true)
        from target in FarPointerIp(type)
        from _2 in PushReturnFar(type)
        from _3 in FarTransfer(target)
        select unit;

    // RET (0xC3) / RET imm16 (0xC2): 戻り番地を pop し、release バイトの引数を捨てる。
    static State<Unit> NearReturn(string name, State<ushort> release) =>
        from _1 in SetLog(name)
        from n in release
        from type in OperandType(true)
        from ret in Pop(type)
        from _2 in JmpTo(ret)
        from _3 in StackAdjust(n)
        select unit;

    // RETF (0xCB) / RETF imm16 (0xCA): (E)IP, CS を pop して far 復帰する(オペランドサイズに従う)。
    // 引数の解放は特権レベルが下がるときの外側スタックへの切り替えより前(内側スタック上)で行う。
    static State<Unit> FarReturn(string name, State<ushort> release) =>
        from _1 in SetLog(name)
        from n in release
        from type in OperandType(true)
        from ret in Pop(type)
        from _2 in JmpTo(ret)
        from cs in Pop(type)
        from _3 in StackAdjust(n)
        from _4 in When(2 == type, ReturnToOuterStack((ushort)cs.Value))
        from _5 in LoadSReg(1, (ushort)cs.Value)
        select unit;

    // 引数を捨てない RET/RETF(表の初期化順に依存しないよう、フィールドではなくその場で作る)。
    static State<ushort> NoRelease => ((ushort)0).ToState();

    static State<Unit> Ret_C3 => NearReturn("Ret_C3", NoRelease);

    static State<Unit> Ret_C2 => NearReturn("Ret_C2", GetMemoryDataIp16);

    static State<Unit> Retf_CB => FarReturn("Retf_CB", NoRelease);

    static State<Unit> Retf_CA => FarReturn("Retf_CA", GetMemoryDataIp16);

    // far 間接 JMP: メモリから offset(16/32bit) と selector(16bit) を読み、CS:EIP を切り替える。
    static State<Unit> JmpFar_rm(MemAddr addr, int type) =>
        from target in FarPointerMem(addr, type)
        from _ in FarTransfer(target)
        select unit;

    // far 間接 CALL: CS と IP/EIP をオペランドサイズ幅で push してから far ジャンプする。
    static State<Unit> CallFar_rm(MemAddr addr, int type) =>
        from _1 in PushReturnFar(type)
        from _2 in JmpFar_rm(addr, type)
        select unit;

    // 近傍間接 CALL: 戻り番地(現在のIP/EIP)を push してから IP/EIP を target に設定する。
    static State<Unit> Call_rm(Data target) =>
        from _1 in PushReturnNear(target.type)
        from _2 in JmpTo(target)
        select unit;

    // INT imm8 (0xCD): 指定ベクタへソフトウェア割り込み。
    static State<Unit> Int_CD =>
        from _1 in SetLog("Int_CD")
        from vector in GetMemoryDataIp8
        from cpu in GetCpu
        from _2 in cpu.vm ? InterruptFromV86(vector) : Interrupt(vector)
        select unit;

    // INT3 (0xCC): ベクタ 3 へのブレークポイント割り込み。
    static State<Unit> Int3_CC =>
        from _1 in SetLog("Int3_CC")
        from _2 in Interrupt(3)
        select unit;

    // INTO (0xCE): OF=1 のときのみベクタ 4 へ割り込む。
    static State<Unit> Into_CE =>
        from _1 in SetLog("Into_CE")
        from of in GetDataFromCpu(cpu => cpu.of)
        from _2 in When(of, Interrupt(4))
        select unit;

    // IRET (0xCF): IP, CS, FLAGS を pop して割り込みから復帰する。
    static State<Unit> Iret_CF =>
        from _1 in SetLog("Iret_CF")
        from _2 in Iret
        select unit;

    // CALL rel16/rel32 (0xE8): rel 読み取り後の IP/EIP（＝次命令アドレス）を戻り番地として push する。
    static State<Unit> Call_E8 =>
        from _1 in SetLog("Call_E8")
        from type in OperandType(true)
        from offset in RelNear
        from _2 in PushReturnNear(type)
        from _3 in IpInc(offset)
        select unit;

    // LOOP系 (0xE0-0xE2): CX-- してから、条件を満たせば rel8 分岐する。
    //   0xE2 LOOP   : CX!=0
    //   0xE1 LOOPE  : CX!=0 && ZF==1
    //   0xE0 LOOPNE : CX!=0 && ZF==0
    static State<Unit> Loop_E0_E2 =>
        from _1 in SetLog("Loop_E0_E2")
        from opecode in Opecodes
        from offset in Rel8
        from cx in GetRegData16(1)
        let newCx = (ushort)(cx - 1)
        from _2 in SetRegData16(1, newCx)
        from zf in _zf.Get()
        let taken = (opecode[0] & 0x03) switch
        {
            0 => newCx != 0 && !zf,   // LOOPNE
            1 => newCx != 0 && zf,    // LOOPE
            _ => newCx != 0,          // LOOP
        }
        from _3 in When(taken, IpInc(offset))
        select unit;

    // JCXZ (0xE3): CX==0 なら rel8 分岐（CX は変更しない）。
    static State<Unit> Jcxz_E3 =>
        from _1 in SetLog("Jcxz_E3")
        from offset in Rel8
        from cx in GetRegData16(1)
        from _2 in When(cx == 0, IpInc(offset))
        select unit;
}
