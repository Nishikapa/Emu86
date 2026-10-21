namespace Emu86;

// CPU の例外(フォルト)。命令開始時の状態から IDT 経由で配送する(Raise)。
//   Vector    : IDT のベクタ(6=#UD、13=#GP、14=#PF)
//   ErrorCode : エラーコードを push する例外ではその値(null なら push しない)
//   Linear    : #PF でフォルトした線形アドレス(配送時に CR2 へ入れる)
public readonly record struct Fault(int Vector, uint? ErrorCode = null, uint? Linear = null)
{
    public static readonly Fault UndefinedOpcode = new(6);
    public static Fault GeneralProtection(uint errorCode) => new(13, errorCode);
    public static Fault PageFault(uint linear, uint errorCode) => new(14, errorCode, linear);

    public string Name => Vector switch { 6 => "#UD", 13 => "#GP", 14 => "#PF", _ => $"#{Vector}" };

    // ログ用の短い表記(lin=... err=...)。
    public string Describe() => Vector switch
    {
        14 => $"lin={Linear ?? 0:x8} err={ErrorCode ?? 0:x}",
        13 => $"err={ErrorCode ?? 0:x}",
        _ => "",
    };

    public string Message => Vector switch
    {
        14 => $"PAGE FAULT lin={Linear ?? 0:x8} err={ErrorCode ?? 0:x} " +
              $"({((ErrorCode & 1) != 0 ? "protection" : "not-present")}, {((ErrorCode & 2) != 0 ? "write" : "read")})",
        13 => $"GENERAL PROTECTION err={ErrorCode ?? 0:x}",
        6 => "UNDEFINED OPCODE",
        _ => $"EXCEPTION vector {Vector}",
    };

    // 配送できるか: プロテクトモードで、IDT のゲートが limit の内側にあること。
    public bool IdtReady(CPU cpu) => cpu.pe && cpu.idt_limit >= (Vector + 1) * 8 - 1;
}

// 命令の途中で起きたフォルト。InstructionExecutor が命令開始時の CPU へ戻してから呼び出し側へ伝え、
// EmulationRunner が Raise で配送する(メモリや I/O の既発生の副作用は取り消さない)。
public class CpuFaultException(Fault fault) : Exception
{
    public Fault Fault => fault;
    public override string Message => fault.Message;
}

// ページフォルト。errorCode: bit0=保護違反(1)/不在(0)、bit1=書き込み、bit2=ユーザ。
public sealed class PageFaultException(uint linear, uint errorCode) : CpuFaultException(Fault.PageFault(linear, errorCode))
{
    public uint Linear => linear;
    public uint ErrorCode => errorCode;
}

// 一般保護例外。V86 モードの INT n(IOPL とゲートの DPL)や IRET の復帰先 CS の検査で起きる。
public sealed class GeneralProtectionException(uint errorCode) : CpuFaultException(Fault.GeneralProtection(errorCode))
{
    public uint ErrorCode => errorCode;
}
