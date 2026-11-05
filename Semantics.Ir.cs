using static Emu86.Conversions;

namespace Emu86;

// IR のノード: 値を生む式(Expr)と状態を変える文(Stmt)。IR の全体像は Semantics.cs の先頭を参照。
// 各ノードの Evaluate/Run が解釈器(仮想呼び出しで木を歩く)。コンパイラは Semantics.Compile が型で分岐する。
public abstract record Expr
{
    public abstract uint Evaluate(EmuEnvironment env, CPU cpu);
}

// 汎用レジスタ
public sealed record RegExpr(int Reg, int Type) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.ReadRegister(cpu, Reg, Type);
}

// 定数
public sealed record ImmExpr(uint Value, int Type) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Value & Mask(Type);
}

// CalculateAlu(フラグ更新を伴う)
public sealed record AluExpr(int Kind, Expr Left, Expr Right, int Type) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.AluWithFlags(cpu, Type, Left.Evaluate(env, cpu), Right.Evaluate(env, cpu), Kind);
}

// INC/DEC(CF 以外のフラグ更新)
public sealed record IncDecExpr(Expr Value, int Delta, int Type) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.IncrementWithFlags(cpu, Type, Value.Evaluate(env, cpu), Delta);
}

// ModRM の実効アドレス(線形。LEA はオフセットのみ)
public sealed record AddressExpr(ModRm ModRm, bool Address32, Prefix Prefixes, bool WithSegment) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.Linear(cpu, ModRm, Address32, Prefixes, WithSegment);
}

// DS(またはオーバーライド):オフセット
public sealed record MoffsExpr(uint Offset, Prefix Prefixes) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.MoffsAddress(cpu, Offset, Prefixes);
}

// メモリ読み出し
public sealed record LoadExpr(Expr Address, int Type) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.Load(env, Address.Evaluate(env, cpu), Type);
}

// スタックから pop
public sealed record PopExpr(int Type) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.Pop(env, cpu, Type);
}

// ゼロ/符号拡張
public sealed record ExtendExpr(Expr Value, int FromType, bool Signed, int ToType) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.Extend(Value.Evaluate(env, cpu), FromType, Signed, ToType);
}

// 次命令 + 変位
public sealed record BranchTargetExpr(int Length, uint Displacement) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => cpu.eip + (uint)Length + Displacement;
}

// Group2 シフト/ローテート(count が 0 ならフラグは変えない)
public sealed record ShiftExpr(int Kind, Expr Value, Expr Count, int Type) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.ShiftWithFlags(cpu, Type, Value.Evaluate(env, cpu), Count.Evaluate(env, cpu), Kind);
}

// NOT(kind 0、フラグ不変)/ NEG(kind 1、0 - v のフラグ)
public sealed record UnaryExpr(int Kind, Expr Value, int Type) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.Unary(cpu, Type, Value.Evaluate(env, cpu), Kind);
}

// 2 オペランドの IMUL(下位の積。CF/OF は上位への溢れ)
public sealed record MultiplyExpr(Expr Left, Expr Right, int Type) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.MultiplySigned(cpu, Type, Left.Evaluate(env, cpu), Right.Evaluate(env, cpu));
}

// 条件が成り立てば 1(SETcc)
public sealed record ConditionExpr(int Condition) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.ConditionValue(cpu, Condition);
}

// 近傍の分岐先(16 ビットオペランドでは EIP の上位 16 ビットを保つ)
public sealed record NearTargetExpr(Expr Value, int Length, bool Operand32) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.NearTarget(cpu, Value.Evaluate(env, cpu), Length, Operand32);
}

// CALL の戻り番地(次命令)
public sealed record ReturnAddressExpr(int Length) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => cpu.eip + (uint)Length;
}

// 符号ビットで埋めた値(CWD/CDQ の上位)
public sealed record SignFillExpr(Expr Value, int Type) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.SignFill(Value.Evaluate(env, cpu), Type);
}

// Let で束縛した一時変数
public sealed record TempExpr(int Slot) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => env.Translations.Temps[Slot];
}

// EFLAGS(type の幅: LAHF は下位 8 ビット、PUSHF はオペランドサイズ)
public sealed record FlagsExpr(int Type) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.Flags(cpu, Type);
}

// BSWAP
public sealed record BswapExpr(Expr Value) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.Bswap(Value.Evaluate(env, cpu));
}

// XLAT の表引きアドレス
public sealed record XlatAddressExpr : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.XlatAddress(cpu);
}

// セグメントレジスタのセレクタ(ES=0 CS=1 SS=2 DS=3 FS=4 GS=5)
public sealed record SegmentExpr(int Sreg) : Expr
{
    public override uint Evaluate(EmuEnvironment env, CPU cpu) => Semantics.ReadSegment(cpu, Sreg);
}

// オペランドの置き場所: レジスタ番号か、アドレス式。
public readonly record struct Place(int Reg, Expr Address)
{
    public static Place Register(int reg) => new(reg, null);
    public static Place Memory(Expr address) => new(-1, address);
    public bool IsMemory => Address != null;
}

public abstract record Stmt
{
    public abstract void Run(EmuEnvironment env, CPU cpu);
}

public sealed record SetRegStmt(int Reg, int Type, Expr Value) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.WriteRegister(cpu, Reg, Type, Value.Evaluate(env, cpu));
}

// ReadsFirst は読み書き(RMW: ADD [m], r や INC [m])で、実機と同じく読み取りの検査を先に行う(不在ページでは読み取りフォルトになる)。
public sealed record StoreStmt(Expr Address, int Type, Expr Value, bool ReadsFirst) : Stmt
{
    // 書き込み先を先に検査(Probe)してから値を評価する。値の評価がフラグを変えるので、
    // ページフォルトは状態を変える前に出す(ブロック翻訳ではこの順序がフォルトの精密性を担う)。
    public override void Run(EmuEnvironment env, CPU cpu)
    {
        var address = Address.Evaluate(env, cpu);
        Semantics.Probe(env, address, Type, ReadsFirst);
        Semantics.Store(env, address, Type, Value.Evaluate(env, cpu));
    }
}

public sealed record PushStmt(int Type, Expr Value) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.Push(env, cpu, Type, Value.Evaluate(env, cpu));
}

// フラグのためだけに評価(CMP/TEST)
public sealed record EvalStmt(Expr Value) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Value.Evaluate(env, cpu);
}

// EIP += Length
public sealed record AdvanceStmt(int Length) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => cpu.eip += (uint)Length;
}

public sealed record SetEipStmt(Expr Value) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => cpu.eip = Value.Evaluate(env, cpu);
}

// EvaluateCondition の条件番号で分岐
public sealed record IfStmt(int Condition, Stmt Then, Stmt Else) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => (Semantics.Condition(cpu, Condition) ? Then : Else).Run(env, cpu);
}

// 一時変数への束縛(命令内で有効)
public sealed record LetStmt(int Slot, Expr Value) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => env.Translations.Temps[Slot] = Value.Evaluate(env, cpu);
}

// SP/ESP += Delta(RET imm16)
public sealed record AdjustStackStmt(int Delta) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.AdjustStack(cpu, Delta);
}

public sealed record LeaveStmt(int Type) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.Leave(env, cpu, Type);
}

// 1 オペランドの MUL/IMUL(AX、DX:AX、EDX:EAX へ)
public sealed record MultiplyStmt(Expr Value, int Type, bool Signed) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.Multiply(cpu, Type, Value.Evaluate(env, cpu), Signed);
}

// CLC/STC/CMC/CLD/STD/CLI/STI(kind 0-6)
public sealed record FlagStmt(int Kind) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.Flag(cpu, Kind);
}

// POPF(CPL>0 の IOPL/IF の保護は Alu.PopFlags)
public sealed record PopFlagsStmt(int Type) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.PopFlags(env, cpu, Type);
}

// PUSHA / POPA
public sealed record PushAllStmt(int Type) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.PushAll(env, cpu, Type);
}

public sealed record PopAllStmt(int Type) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.PopAll(env, cpu, Type);
}

// POP m(書き込み先を検査してから pop する)
public sealed record PopIntoMemoryStmt(Expr Address, int Type) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.PopIntoMemory(env, cpu, Address.Evaluate(env, cpu), Type);
}

// LOOPNE(0)/LOOPE(1)/LOOP(2)/JCXZ(3): CX を数えて rel8 分岐
public sealed record LoopStmt(int Kind, int Length, uint Displacement) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.Loop(cpu, Kind, Length, Displacement);
}

// HLT: ランナーが待機に使う印を立てる(ブロックの終端)
public sealed record HaltStmt : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => cpu.Halted = true;
}

// CMPXCHG r/m, r
public sealed record CompareExchangeStmt(Place Dst, int Reg, int Type) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) =>
        Semantics.CompareExchange(env, cpu, Dst.IsMemory, Dst.IsMemory ? Dst.Address.Evaluate(env, cpu) : 0, Dst.Reg, Reg, Type);
}

// XADD r/m, r
public sealed record ExchangeAddStmt(Place Dst, int Reg, int Type) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) =>
        Semantics.ExchangeAdd(env, cpu, Dst.IsMemory, Dst.IsMemory ? Dst.Address.Evaluate(env, cpu) : 0, Dst.Reg, Reg, Type);
}

// BT(0)/BTS(1)/BTR(2)/BTC(3) r/m, bit。BitString はレジスタのビット番号でメモリのワードを選ぶ形(0F A3 系)。
public sealed record BitTestStmt(Place Dst, Expr Bit, bool BitString, int Op, int Type) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) =>
        Semantics.BitTest(env, cpu, Dst.IsMemory, Dst.IsMemory ? Dst.Address.Evaluate(env, cpu) : 0, Dst.Reg, Bit.Evaluate(env, cpu), BitString, Op, Type);
}

// BSF/BSR r, r/m
public sealed record BitScanStmt(int Reg, Expr Source, bool Forward, int Type) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.BitScan(cpu, Reg, Source.Evaluate(env, cpu), Forward, Type);
}

// SHLD/SHRD r/m, r, count
public sealed record DoubleShiftStmt(Place Dst, int Reg, Expr Count, bool Left, int Type) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) =>
        Semantics.DoubleShift(env, cpu, Dst.IsMemory, Dst.IsMemory ? Dst.Address.Evaluate(env, cpu) : 0, Dst.Reg, Reg, Count.Evaluate(env, cpu), Left, Type);
}

// DIV/IDIV r/m
public sealed record DivideStmt(Expr Divisor, int Type, bool Signed) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.Divide(cpu, Type, Divisor.Evaluate(env, cpu), Signed);
}

// BCD 調整: 0 AAA、1 AAS、2 DAA、3 DAS、4 AAM、5 AAD
public sealed record AdjustStmt(int Kind, uint Immediate) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.AdjustDecimal(cpu, Kind, Immediate);
}

// 文字列命令(Repeat: 0 単発、1 REP/REPE、2 REPNE)。REP は反復ごとに ECX/ESI/EDI/EFLAGS を更新するので、
// 途中のフォルトからはそのまま再開できる(EIP は命令の先頭のまま)。
public sealed record StringStmt(int Op, int Size, bool Address32, Prefix Prefixes, int Repeat) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.StringOp(env, cpu, Op, Size, Address32, (int)Prefixes, Repeat);
}

// 通常コアの State を 1 つ実行する(セグメントのロード、far 転送、割り込みの配送、ENTER)。State は EIP の前進を自分で行う。
public sealed record StateStmt(State<Unit> State, string Name) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.RunState(env, cpu, State);
}

// 引数(アドレスやセレクタ)を評価してから、その値で組み立てた State を実行する。
public sealed record ArgumentStateStmt(Expr Argument, Func<uint, State<Unit>> Build, string Name) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.RunState(env, cpu, Build(Argument.Evaluate(env, cpu)));
}

// IN acc, port / OUT port, acc(ブロックの終端、連結しない)
public sealed record PortInStmt(int Type, Expr Port) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.PortIn(env, cpu, Type, Port.Evaluate(env, cpu));
}

public sealed record PortOutStmt(int Type, Expr Port, Expr Value) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.PortOut(env, Type, Port.Evaluate(env, cpu), Value.Evaluate(env, cpu));
}

public sealed record RdtscStmt : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.Rdtsc(env, cpu);
}

public sealed record CpuidStmt : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.Cpuid(cpu);
}

// CMPXCHG8B m64
public sealed record CompareExchange8Stmt(Expr Address) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.CompareExchange8(env, cpu, Address.Evaluate(env, cpu));
}

public sealed record Block(IReadOnlyList<Stmt> Statements) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu)
    {
        // インターフェース経由の foreach は列挙子を割り当てるので添字で回す(解釈器の 1 命令ごとに通る)。
        for (var index = 0; index < Statements.Count; index++) Statements[index].Run(env, cpu);
    }
}
