using System.Linq.Expressions;
using static Emu86.Conversions;
using static Emu86.FastCore;
using static Emu86.Operands;
using static Emu86.Alu;
using static Emu86.Mmu;
using static Emu86.Ports;
using static Emu86.Decoder;

namespace Emu86;

// 命令の意味論を第一級のデータ(IR)で表す(docs/adr/0002)。
//
// State モナドの命令定義は C# のクロージャ(SetCpu(cpu => ...))で意味を閉じ込めるため、解析もコンパイルも
// できない。ここでは「値を生む式(Expr)」と「状態を変える文(Stmt)」を record で表し、LINQ クエリは
// Emit モナド(文を書き出す Writer)で IR を組み立てる(シンボリック評価)。組み立てた IR は
//   Interpret: 木を歩いて実行する(通常コアと同じ意味論の参照実装)
//   Compile  : System.Linq.Expressions で Action<EmuEnvironment, CPU> にコンパイルする(翻訳キャッシュが使う)
// の 2 つが消費する。対象(Lower が null を返さないもの): ALU の 00-3D と Group1、INC/DEC、PUSH/POP(レジスタ・即値・r/m)、
// MOV(88-8B、A0-A3、B0-BF、C6/C7)、TEST/CMP、LEA、MOVZX/MOVSX、XCHG、Group2 のシフト/ローテート、Group3 の TEST/NOT/NEG/MUL/IMUL、
// IMUL r,r/m(,imm)、SETcc、CBW/CWD 系、CLC/STC/CMC/CLD/STD/CLI/STI、NOP、Jcc/JMP rel、CALL/JMP(相対・近傍間接)、RET、LEAVE。
// docs/adr/0003 の段階 1 で高速コアから移した命令: 文字列命令と REP、DIV/IDIV、POP r/m、PUSHF/POPF、SAHF/LAHF、PUSHA/POPA、
// LOOP/LOOPE/LOOPNE/JCXZ、HLT、XLAT、BSWAP、CMOVcc、XADD、CMPXCHG、BT/BTS/BTR/BTC、BSF/BSR、SHLD/SHRD、AAA/AAS/DAA/DAS/AAM/AAD。
// これらは「共通の基本操作(Alu/StringOps/Mmu)を呼ぶ文」で、状態を変える前にページを検査して、フォルト時の CPU が命令の先頭の
// 状態のままになる規律(段階 3)を守る。
// メモリオペランドは実効アドレスの式で表し、読み書きは Environment の共通経路(ページフォルトは例外のまま)を呼ぶ。
// 状態を変える前に読んだ値を後で使う命令(XCHG、CALL 間接)は Let で一時変数に束縛する(TempExpr)。

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

public sealed record Block(IReadOnlyList<Stmt> Statements) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu)
    {
        foreach (var statement in Statements) statement.Run(env, cpu);
    }
}

// IR を組み立てる Writer モナド。値はシンボリック(Expr)で、文は sink へ追記される。
public delegate T Emit<T>(List<Stmt> sink);

public static class Semantics
{
    public static Emit<B> Select<A, B>(this Emit<A> emit, Func<A, B> selector) => sink => selector(emit(sink));

    public static Emit<C> SelectMany<A, B, C>(this Emit<A> emit, Func<A, Emit<B>> selector, Func<A, B, C> projector) => sink =>
    {
        var a = emit(sink);
        return projector(a, selector(a)(sink));
    };

    static Emit<Expr> Read(Place place, int type) => _ => place.Address == null ? new RegExpr(place.Reg, type) : new LoadExpr(place.Address, type);
    static Emit<Expr> Const(uint value, int type) => _ => new ImmExpr(value, type);
    static Emit<Expr> Alu(int kind, Expr left, Expr right, int type) => _ => new AluExpr(kind, left, right, type);
    static Emit<Expr> IncDec(Expr value, int delta, int type) => _ => new IncDecExpr(value, delta, type);
    static Emit<Expr> Pop(int type) => _ => new PopExpr(type);
    static Emit<Expr> Extend(Expr value, int fromType, bool signed, int toType) => _ => new ExtendExpr(value, fromType, signed, toType);
    static Emit<Expr> Cond(int condition) => _ => new ConditionExpr(condition);
    static Emit<Expr> ReturnAddress(int length) => _ => new ReturnAddressExpr(length);
    static Emit<Expr> NearTarget(Expr value, int length, bool operand32) => _ => new NearTargetExpr(value, length, operand32);
    // 値を一時変数に束縛して、以後はその変数を参照する(状態を変えた後でも元の値を使えるように)。
    static Emit<Expr> Let(Expr value) => sink =>
    {
        var slot = sink.Count(statement => statement is LetStmt);
        sink.Add(new LetStmt(slot, value));
        return new TempExpr(slot);
    };
    static readonly Emit<Unit> Nothing = _ => Unit.unit;
    static Emit<Unit> Statement(Stmt statement) => sink => { sink.Add(statement); return Unit.unit; };
    static Emit<Unit> SetEip(Expr value) => Statement(new SetEipStmt(value));
    static Emit<Unit> Write(Place place, int type, Expr value, bool readsFirst = false) => sink =>
    {
        sink.Add(place.Address == null ? new SetRegStmt(place.Reg, type, value) : new StoreStmt(place.Address, type, value, readsFirst));
        return Unit.unit;
    };
    static Emit<Unit> Push(int type, Expr value) => sink => { sink.Add(new PushStmt(type, value)); return Unit.unit; };
    static Emit<Unit> Evaluate(Expr value) => sink => { sink.Add(new EvalStmt(value)); return Unit.unit; };
    static Emit<Unit> Advance(int length) => sink => { sink.Add(new AdvanceStmt(length)); return Unit.unit; };
    static Emit<Unit> Branch(int condition, int length, uint displacement) => sink =>
    {
        sink.Add(condition < 0
            ? new SetEipStmt(new BranchTargetExpr(length, displacement))
            : new IfStmt(condition, new SetEipStmt(new BranchTargetExpr(length, displacement)), new AdvanceStmt(length)));
        return Unit.unit;
    };

    // dst <- dst op src。CMP(kind 7)は書き戻さない(高速コアと同じ)。
    static Emit<Unit> Binary(int kind, Place dst, Emit<Expr> src, int type, int length) =>
        from left in Read(dst, type)
        from right in src
        from result in Alu(kind, left, right, type)
        from _1 in kind == 7 ? Evaluate(result) : Write(dst, type, result, readsFirst: true)
        from _2 in Advance(length)
        select Unit.unit;

    // TEST: AND のフラグだけ。
    static Emit<Unit> Test(Place left, Emit<Expr> right, int type, int length) =>
        from a in Read(left, type)
        from b in right
        from result in Alu(4, a, b, type)
        from _1 in Evaluate(result)
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> Move(Place dst, Emit<Expr> src, int type, int length) =>
        from value in src
        from _1 in Write(dst, type, value)
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> MoveExtended(int reg, Place src, int fromType, bool signed, int toType, int length) =>
        from value in Read(src, fromType)
        from extended in Extend(value, fromType, signed, toType)
        from _1 in Write(Place.Register(reg), toType, extended)
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> IncrementDecrement(Place place, int delta, int type, int length) =>
        from value in Read(place, type)
        from result in IncDec(value, delta, type)
        from _1 in Write(place, type, result, readsFirst: true)
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> PushValue(int type, Emit<Expr> value, int length) =>
        from v in value
        from _1 in Push(type, v)
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> PopInto(Place place, int type, int length) =>
        from value in Pop(type)
        from _1 in Write(place, type, value)
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> Exchange(Place a, Place b, int type, int length) =>
        from x in Read(a, type)
        from y in Read(b, type)
        from tx in Let(x)
        from ty in Let(y)
        from _1 in Write(a, type, ty, readsFirst: true)
        from _2 in Write(b, type, tx)
        from _3 in Advance(length)
        select Unit.unit;

    static Emit<Unit> Shift(int kind, Place place, Emit<Expr> count, int type, int length) =>
        from value in Read(place, type)
        from n in count
        from _1 in Write(place, type, new ShiftExpr(kind, value, n, type), readsFirst: true)
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> Unary(int kind, Place place, int type, int length) =>
        from value in Read(place, type)
        from _1 in Write(place, type, new UnaryExpr(kind, value, type), readsFirst: true)
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> MultiplyInto(Place dst, Emit<Expr> left, Emit<Expr> right, int type, int length) =>
        from a in left
        from b in right
        from _1 in Write(dst, type, new MultiplyExpr(a, b, type))
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> MultiplyAccumulator(Place src, int type, bool signed, int length) =>
        from value in Read(src, type)
        from _1 in Statement(new MultiplyStmt(value, type, signed))
        from _2 in Advance(length)
        select Unit.unit;

    // CALL: 戻り番地を push してから分岐(間接なら分岐先を先に読んで束縛する: push が ESP やスタックを変えるため)。
    static Emit<Unit> CallRelative(int type, int length, uint displacement) =>
        from ret in ReturnAddress(length)
        from _1 in Push(type, ret)
        from _2 in SetEip(new BranchTargetExpr(length, displacement))
        select Unit.unit;

    static Emit<Unit> CallIndirect(Place place, int type, bool operand32, int length) =>
        from value in Read(place, type)
        from target in Let(value)
        from ret in ReturnAddress(length)
        from _1 in Push(type, ret)
        from near in NearTarget(target, length, operand32)
        from _2 in SetEip(near)
        select Unit.unit;

    static Emit<Unit> JumpIndirect(Place place, int type, bool operand32, int length) =>
        from value in Read(place, type)
        from near in NearTarget(value, length, operand32)
        from _1 in SetEip(near)
        select Unit.unit;

    // RET: pop してから EIP を変え、最後に SP を進める(RET imm16)。
    static Emit<Unit> Return(int type, bool operand32, int length, int adjust) =>
        from value in Pop(type)
        from near in NearTarget(value, length, operand32)
        from _1 in SetEip(near)
        from _2 in adjust == 0 ? Nothing : Statement(new AdjustStackStmt(adjust))
        select Unit.unit;

    static Emit<Unit> DivideBy(Place divisor, int type, bool signed, int length) =>
        from value in Read(divisor, type)
        from _1 in Statement(new DivideStmt(value, type, signed))
        from _2 in Advance(length)
        select Unit.unit;

    // CMOVcc: 条件に関わらず r/m を読む(メモリならフォルトしうる)。読んだ値を束縛して条件付きで書く。
    static Emit<Unit> ConditionalMove(int condition, int reg, Place src, int type, int length) =>
        from value in Read(src, type)
        from bound in Let(value)
        from _1 in Statement(new IfStmt(condition, new SetRegStmt(reg, type, bound), new Block([])))
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> Simple(Stmt statement, int length) =>
        from _1 in Statement(statement)
        from _2 in Advance(length)
        select Unit.unit;

    // デコード済み命令を IR に下ろす。対象外なら null(呼び出し側は既存の経路へ)。
    public static Block Lower(Instruction instruction)
    {
        var op = instruction.Opcode;
        // REP は文字列命令にだけ意味を持つ。他の命令に付いた REP は無視して、その命令として下ろす(F3 90 = PAUSE は NOP)。
        var repeat = instruction.Repeat switch { 0xF3 => 1, 0xF2 => 2, _ => 0 };
        var prefixes = instruction.Prefixes;
        var length = instruction.Length;
        var operand32 = instruction.OperandSize32;
        var typeW = operand32 ? 2 : 1;
        var type = (op & 1) != 0 ? typeW : 0;
        Place Rm() => instruction.ModRm is { Mod: 3 } register
            ? Place.Register(register.Rm)
            : Place.Memory(new AddressExpr(instruction.ModRm.Value, instruction.AddressSize32, prefixes, true));
        Place Reg() => Place.Register(instruction.ModRm.Value.Reg);
        Place Acc() => Place.Register(0);
        Emit<Expr> Imm(int immediateType) => Const(instruction.Immediate, immediateType);
        Emit<Expr> Imm8Extended(int toType) => Const((uint)(int)(sbyte)instruction.Immediate, toType);
        var moffs = new MoffsExpr(instruction.Immediate, prefixes);
        var reg = instruction.ModRm?.Reg ?? 0;

        Emit<Unit> program = op switch
        {
            <= 0x3D when (op & 7) <= 3 => (op & 2) != 0 ? Binary((op >> 3) & 7, Reg(), Read(Rm(), type), type, length) : Binary((op >> 3) & 7, Rm(), Read(Reg(), type), type, length),
            <= 0x3D when (op & 7) is 4 or 5 => Binary((op >> 3) & 7, Acc(), Imm(type), type, length),
            >= 0x40 and <= 0x47 => IncrementDecrement(Place.Register(op & 7), 1, typeW, length),
            >= 0x48 and <= 0x4F => IncrementDecrement(Place.Register(op & 7), -1, typeW, length),
            >= 0x50 and <= 0x57 => PushValue(typeW, Read(Place.Register(op & 7), typeW), length),
            >= 0x58 and <= 0x5F => PopInto(Place.Register(op & 7), typeW, length),
            0x68 => PushValue(typeW, Imm(typeW), length),
            0x6A => PushValue(typeW, Imm8Extended(typeW), length),
            >= 0x70 and <= 0x7F => _ => Branch(op & 0xF, length, instruction.Immediate)(_),
            0x80 or 0x81 => Binary(reg, Rm(), Imm(type), type, length),
            0x83 => Binary(reg, Rm(), Imm8Extended(type), type, length),
            0x69 => MultiplyInto(Reg(), Read(Rm(), typeW), Imm(typeW), typeW, length),
            0x6B => MultiplyInto(Reg(), Read(Rm(), typeW), Imm8Extended(typeW), typeW, length),
            0x84 or 0x85 => Test(Rm(), Read(Reg(), type), type, length),
            0x86 or 0x87 => Exchange(Rm(), Reg(), type, length),
            >= 0x88 and <= 0x8B => (op & 2) != 0 ? Move(Reg(), Read(Rm(), type), type, length) : Move(Rm(), Read(Reg(), type), type, length),
            0x8D when instruction.ModRm is { Mod: not 3 } => Move(Reg(), _ => new AddressExpr(instruction.ModRm.Value, instruction.AddressSize32, prefixes, false), typeW, length),
            0x90 => Advance(length),
            >= 0x91 and <= 0x97 => Exchange(Acc(), Place.Register(op & 7), typeW, length),
            0x98 => operand32 ? MoveExtended(0, Acc(), 1, true, 2, length) : MoveExtended(0, Acc(), 0, true, 1, length),
            0x99 => Move(Place.Register(2), Read(Acc(), typeW).Select(value => (Expr)new SignFillExpr(value, typeW)), typeW, length),
            0xA0 => Move(Acc(), Read(Place.Memory(moffs), 0), 0, length),
            0xA1 => Move(Acc(), Read(Place.Memory(moffs), typeW), typeW, length),
            0xA2 => Move(Place.Memory(moffs), Read(Acc(), 0), 0, length),
            0xA3 => Move(Place.Memory(moffs), Read(Acc(), typeW), typeW, length),
            0xA8 or 0xA9 => Test(Acc(), Imm(type), type, length),
            >= 0xB0 and <= 0xB7 => Move(Place.Register(op & 7), Imm(0), 0, length),
            >= 0xB8 and <= 0xBF => Move(Place.Register(op & 7), Imm(typeW), typeW, length),
            0xC0 or 0xC1 => Shift(reg, Rm(), Imm(0), type, length),
            0xC2 => Return(typeW, operand32, length, (int)instruction.Immediate),
            0xC3 => Return(typeW, operand32, length, 0),
            0xC6 or 0xC7 => Move(Rm(), Imm(type), type, length),
            0xC9 => Simple(new LeaveStmt(typeW), length),
            0xD0 or 0xD1 => Shift(reg, Rm(), Const(1, 0), type, length),
            0xD2 or 0xD3 => Shift(reg, Rm(), Read(Place.Register(1), 0), type, length),
            0xE8 => CallRelative(typeW, length, instruction.Immediate),
            0xE9 or 0xEB => _ => Branch(-1, length, instruction.Immediate)(_),
            0xF5 => Simple(new FlagStmt(2), length),
            0xF6 or 0xF7 when reg is 0 or 1 => Test(Rm(), Imm(type), type, length),
            0xF6 or 0xF7 when reg is 2 or 3 => Unary(reg - 2, Rm(), type, length),
            0xF6 or 0xF7 when reg is 4 or 5 => MultiplyAccumulator(Rm(), type, reg == 5, length),
            0xF8 => Simple(new FlagStmt(0), length),
            0xF9 => Simple(new FlagStmt(1), length),
            0xFA => Simple(new FlagStmt(5), length),
            0xFB => Simple(new FlagStmt(6), length),
            0xFC => Simple(new FlagStmt(3), length),
            0xFD => Simple(new FlagStmt(4), length),
            0xFE when reg is 0 or 1 => IncrementDecrement(Rm(), reg == 0 ? 1 : -1, 0, length),
            0xFF when reg is 0 or 1 => IncrementDecrement(Rm(), reg == 0 ? 1 : -1, typeW, length),
            0xFF when reg == 2 => CallIndirect(Rm(), typeW, operand32, length),
            0xFF when reg == 4 => JumpIndirect(Rm(), typeW, operand32, length),
            0xFF when reg == 6 => PushValue(typeW, Read(Rm(), typeW), length),
            >= 0x0F80 and <= 0x0F8F => _ => Branch(op & 0xF, length, instruction.Immediate)(_),
            >= 0x0F90 and <= 0x0F9F => Move(Rm(), Cond(op & 0xF), 0, length),
            0x0FAF => MultiplyInto(Reg(), Read(Reg(), typeW), Read(Rm(), typeW), typeW, length),
            0x0FB6 or 0x0FB7 => MoveExtended(reg, Rm(), op == 0x0FB6 ? 0 : 1, false, typeW, length),
            0x0FBE or 0x0FBF => MoveExtended(reg, Rm(), op == 0x0FBE ? 0 : 1, true, typeW, length),
            // ---- docs/adr/0003 段階 1: 高速コアから移した命令 ----
            0x27 => Simple(new AdjustStmt(2, 0), length),
            0x2F => Simple(new AdjustStmt(3, 0), length),
            0x37 => Simple(new AdjustStmt(0, 0), length),
            0x3F => Simple(new AdjustStmt(1, 0), length),
            0xD4 when instruction.Immediate != 0 => Simple(new AdjustStmt(4, instruction.Immediate), length), // AAM 0 は通常コアで #DE
            0xD5 => Simple(new AdjustStmt(5, instruction.Immediate), length),
            0x60 => Simple(new PushAllStmt(typeW), length),
            0x61 => Simple(new PopAllStmt(typeW), length),
            0x6C or 0x6D or 0x6E or 0x6F or (>= 0xA4 and <= 0xA7) or (>= 0xAA and <= 0xAF) =>
                Simple(new StringStmt(op, (op & 1) != 0 ? Bytes(typeW) : 1, instruction.AddressSize32, prefixes, repeat), length),
            0x8F when reg == 0 => instruction.ModRm is { Mod: 3 } ? PopInto(Rm(), typeW, length) : Simple(new PopIntoMemoryStmt(Rm().Address, typeW), length),
            0x9C => PushValue(typeW, _ => new FlagsExpr(typeW), length),
            0x9D => Simple(new PopFlagsStmt(typeW), length),
            0x9E => Simple(new FlagStmt(7), length),
            0x9F => Move(Place.Register(4), _ => new FlagsExpr(0), 0, length),
            0xD7 => Move(Acc(), _ => new LoadExpr(new XlatAddressExpr(), 0), 0, length),
            >= 0xE0 and <= 0xE3 => Statement(new LoopStmt(op & 3, length, instruction.Immediate)),
            0xF4 => Simple(new HaltStmt(), length),
            0xF6 or 0xF7 when reg is 6 or 7 => DivideBy(Rm(), type, reg == 7, length),
            >= 0x0F40 and <= 0x0F4F => ConditionalMove(op & 0xF, reg, Rm(), typeW, length),
            0x0FA3 or 0x0FAB or 0x0FB3 or 0x0FBB => Simple(new BitTestStmt(Rm(), new RegExpr(reg, typeW), true, (op >> 3) & 3, typeW), length),
            0x0FA4 or 0x0FAC => Simple(new DoubleShiftStmt(Rm(), reg, new ImmExpr(instruction.Immediate, 0), op == 0x0FA4, typeW), length),
            0x0FA5 or 0x0FAD => Simple(new DoubleShiftStmt(Rm(), reg, new RegExpr(1, 0), op == 0x0FA5, typeW), length),
            0x0FB0 or 0x0FB1 => Simple(new CompareExchangeStmt(Rm(), reg, type), length),
            0x0FBA when reg >= 4 => Simple(new BitTestStmt(Rm(), new ImmExpr(instruction.Immediate, 0), false, reg & 3, typeW), length),
            0x0FBC or 0x0FBD => from source in Read(Rm(), typeW) from _1 in Statement(new BitScanStmt(reg, source, op == 0x0FBC, typeW)) from _2 in Advance(length) select Unit.unit,
            0x0FC0 or 0x0FC1 => Simple(new ExchangeAddStmt(Rm(), reg, type), length),
            >= 0x0FC8 and <= 0x0FCF => Move(Place.Register(op & 7), _ => new BswapExpr(new RegExpr(op & 7, 2)), 2, length),
            _ => null,
        };
        if (program == null) return null;
        var sink = new List<Stmt>();
        program(sink);
        return new Block(sink);
    }

    // ブロックの終端になる命令: 制御転送と STI。STI は保留中の割り込みを配送できるようにするので、
    // 1 命令ずつの実行と同じく直後の Step 境界で配送されるよう、ブロックをそこで切る(CLI は逆向きなので切らなくてよい:
    // ブロックの途中で新たに保留になる割り込みはタイマ周期(予算の境界)でしか生じない)。
    // HLT・POPF・IN/OUT 系(INS/OUTS)も終端で、かつ連結しない(StopsChain): HLT はランナーが待機を見る必要があり、
    // POPF は IF を立てうる(STI と同じ配送点の理由)、ポート入出力はデバイスが割り込み要求を立てうる。
    public static bool EndsBlock(Instruction instruction) => instruction.Opcode switch
    {
        (>= 0x70 and <= 0x7F) or (>= 0xE0 and <= 0xE3) or 0xE8 or 0xE9 or 0xEB or 0xC2 or 0xC3 or (>= 0x0F80 and <= 0x0F8F) => true,
        0xFF => instruction.ModRm is { Reg: 2 or 4 },
        _ => StopsChain(instruction),
    };

    public static bool StopsChain(Instruction instruction) => instruction.Opcode is 0xFB or 0xF4 or 0x9D or (>= 0x6C and <= 0x6F);

    /// 解釈器(各ノードの Evaluate/Run) ///////////////////////////////
    public static void Interpret(Stmt statement, EmuEnvironment env, CPU cpu) => statement.Run(env, cpu);

    // ブロック翻訳向け: 1 命令の IR を、与えられたパラメータ式に対する式木にする(一時変数は命令ごとに宣言する)。
    public static Expression CompileInto(Stmt statement, ParameterExpression env, ParameterExpression cpu)
    {
        var scope = new Scope(env, cpu);
        var body = CompileStatement(statement, scope);
        return scope.Temps.Count == 0 ? body : Expression.Block(scope.Temps, body);
    }

    /// コンパイラ(System.Linq.Expressions) ///////////////////////////////
    public static Action<EmuEnvironment, CPU> Compile(Block block)
    {
        var env = Expression.Parameter(typeof(EmuEnvironment), "env");
        var cpu = Expression.Parameter(typeof(CPU), "cpu");
        return Expression.Lambda<Action<EmuEnvironment, CPU>>(CompileInto(block, env, cpu), env, cpu).Compile();
    }

    // コンパイル中の文脈: パラメータと、Let が使う一時変数。
    sealed class Scope(ParameterExpression env, ParameterExpression cpu)
    {
        public readonly ParameterExpression Env = env, Cpu = cpu;
        public readonly List<ParameterExpression> Temps = [];
        public ParameterExpression Temp(int slot)
        {
            while (Temps.Count <= slot) Temps.Add(Expression.Variable(typeof(uint), $"t{Temps.Count}"));
            return Temps[slot];
        }
    }

    static Expression Call(string method, params Expression[] arguments) =>
        Expression.Call(typeof(Semantics).GetMethod(method), arguments);

    static Expression Const<T>(T value) => Expression.Constant(value);

    // コンパイル済みコードは CPU のプロパティを直接読み書きする(解釈器は番号で引くヘルパを使う)。
    static readonly string[] Registers32 = [nameof(CPU.eax), nameof(CPU.ecx), nameof(CPU.edx), nameof(CPU.ebx), nameof(CPU.esp), nameof(CPU.ebp), nameof(CPU.esi), nameof(CPU.edi)];
    static readonly string[] Registers16 = [nameof(CPU.ax), nameof(CPU.cx), nameof(CPU.dx), nameof(CPU.bx), nameof(CPU.sp), nameof(CPU.bp), nameof(CPU.si), nameof(CPU.di)];
    static readonly string[] Registers8 = [nameof(CPU.al), nameof(CPU.cl), nameof(CPU.dl), nameof(CPU.bl), nameof(CPU.ah), nameof(CPU.ch), nameof(CPU.dh), nameof(CPU.bh)];
    static MemberExpression Register(Scope s, int reg, int type) =>
        Expression.Property(s.Cpu, (type == 0 ? Registers8 : type == 1 ? Registers16 : Registers32)[reg]);
    static Expression ReadRegisterOf(Scope s, int reg, int type) =>
        type == 2 ? Register(s, reg, type) : Expression.Convert(Register(s, reg, type), typeof(uint));
    static Expression WriteRegisterOf(Scope s, int reg, int type, Expression value) =>
        Expression.Assign(Register(s, reg, type), type == 2 ? value : Expression.Convert(value, type == 0 ? typeof(byte) : typeof(ushort)));
    static Expression Eflags(Scope s) => Expression.Property(s.Cpu, nameof(CPU.eflags));
    static Expression Bit(Expression flags, uint mask) => Expression.NotEqual(Expression.And(flags, Const(mask)), Const(0u));

    // EvaluateCondition と同じ条件を EFLAGS のビット演算で(0x40 ZF、0x80 SF、0x800 OF、0x1 CF、0x4 PF)。
    static Expression CompileCondition(Scope s, int condition)
    {
        var flags = Expression.Variable(typeof(uint), "flags");
        var signNotOverflow = Expression.NotEqual(Expression.And(Expression.ExclusiveOr(Expression.RightShift(flags, Const(7)), Expression.RightShift(flags, Const(11))), Const(1u)), Const(0u));
        Expression test = (condition >> 1) switch
        {
            0 => Bit(flags, 0x800),
            1 => Bit(flags, 0x1),
            2 => Bit(flags, 0x40),
            3 => Bit(flags, 0x41),
            4 => Bit(flags, 0x80),
            5 => Bit(flags, 0x4),
            6 => signNotOverflow,
            _ => Expression.OrElse(Bit(flags, 0x40), signNotOverflow),
        };
        if ((condition & 1) != 0) test = Expression.Not(test);
        return Expression.Block(typeof(bool), [flags], Expression.Assign(flags, Eflags(s)), test);
    }

    // 32 ビットアドレッシングの実効アドレスを式で組み立てる(EffectiveAddress と同じ規則)。16 ビットは LinearOf に任せる。
    static Expression CompileAddress(AddressExpr e, Scope s)
    {
        var m = e.ModRm;
        if (!e.Address32)
            return Call(nameof(LinearOf), s.Cpu,
                Const((int)m.Mod), Const((int)m.Rm), Const(m.HasSib), Const((int)m.Scale), Const((int)m.Index), Const((int)m.Base),
                Const(m.Disp), Const(e.Address32), Const((int)e.Prefixes), Const(e.WithSegment));
        Expression offset;
        bool stackSegment;
        if (m.HasSib)
        {
            Expression scaled = m.Index == 4 ? Const(0u) : Expression.Multiply(Register(s, m.Index, 2), Const(1u << m.Scale));
            if (m.Mod == 0 && m.Base == 5) { offset = Expression.Add(scaled, Const(m.Disp)); stackSegment = false; }
            else { offset = Expression.Add(Expression.Add(scaled, Register(s, m.Base, 2)), Const(m.Disp)); stackSegment = m.Base is 4 or 5; }
        }
        else if (m.Mod == 0 && m.Rm == 5) { offset = Const(m.Disp); stackSegment = false; }
        else { offset = Expression.Add(Register(s, m.Rm, 2), Const(m.Disp)); stackSegment = m.Rm == 5; }
        if (!e.WithSegment) return offset;
        var segment = SegmentOf(e.Prefixes) ?? (stackSegment ? nameof(CPU.ss_base) : nameof(CPU.ds_base));
        return Expression.Add(offset, Expression.Field(s.Cpu, segment));
    }

    static string SegmentOf(Prefix prefixes) =>
        (prefixes & Prefix.Es) != 0 ? nameof(CPU.es_base) :
        (prefixes & Prefix.Cs) != 0 ? nameof(CPU.cs_base) :
        (prefixes & Prefix.Ss) != 0 ? nameof(CPU.ss_base) :
        (prefixes & Prefix.Ds) != 0 ? nameof(CPU.ds_base) :
        (prefixes & Prefix.Fs) != 0 ? nameof(CPU.fs_base) :
        (prefixes & Prefix.Gs) != 0 ? nameof(CPU.gs_base) : null;

    static Expression CompileLoad(Scope s, Expression address, int type) =>
        type == 2 ? Call(nameof(Load32), s.Env, address) : Expression.Convert(Call(type == 0 ? nameof(Load8) : nameof(Load16), s.Env, address), typeof(uint));

    static Expression CompileStatement(Stmt statement, Scope s) => statement switch
    {
        SetRegStmt set => WriteRegisterOf(s, set.Reg, set.Type, CompileExpression(set.Value, s)),
        StoreStmt store => CompileStore(store, s),
        PushStmt push => Call(nameof(Push), s.Env, s.Cpu, Const(push.Type), CompileExpression(push.Value, s)),
        EvalStmt eval => CompileExpression(eval.Value, s),
        AdvanceStmt advance => Expression.AddAssign(Expression.Property(s.Cpu, nameof(CPU.eip)), Const((uint)advance.Length)),
        SetEipStmt set => Expression.Assign(Expression.Property(s.Cpu, nameof(CPU.eip)), CompileExpression(set.Value, s)),
        IfStmt branch => Expression.IfThenElse(CompileCondition(s, branch.Condition),
            CompileStatement(branch.Then, s), CompileStatement(branch.Else, s)),
        LetStmt let => Expression.Assign(s.Temp(let.Slot), CompileExpression(let.Value, s)),
        AdjustStackStmt adjust => Call(nameof(AdjustStack), s.Cpu, Const(adjust.Delta)),
        LeaveStmt leave => Call(nameof(Leave), s.Env, s.Cpu, Const(leave.Type)),
        MultiplyStmt multiply => Call(nameof(Multiply), s.Cpu, Const(multiply.Type), CompileExpression(multiply.Value, s), Const(multiply.Signed)),
        FlagStmt flag => Call(nameof(Flag), s.Cpu, Const(flag.Kind)),
        PopFlagsStmt popFlags => Call(nameof(PopFlags), s.Env, s.Cpu, Const(popFlags.Type)),
        PushAllStmt pushAll => Call(nameof(PushAll), s.Env, s.Cpu, Const(pushAll.Type)),
        PopAllStmt popAll => Call(nameof(PopAll), s.Env, s.Cpu, Const(popAll.Type)),
        PopIntoMemoryStmt popInto => Call(nameof(PopIntoMemory), s.Env, s.Cpu, CompileExpression(popInto.Address, s), Const(popInto.Type)),
        LoopStmt loop => Call(nameof(Loop), s.Cpu, Const(loop.Kind), Const(loop.Length), Const(loop.Displacement)),
        HaltStmt => Expression.Assign(Expression.Field(s.Cpu, nameof(CPU.Halted)), Const(true)),
        CompareExchangeStmt cmpxchg => Call(nameof(CompareExchange), s.Env, s.Cpu, Const(cmpxchg.Dst.IsMemory), CompilePlaceAddress(cmpxchg.Dst, s), Const(cmpxchg.Dst.Reg), Const(cmpxchg.Reg), Const(cmpxchg.Type)),
        ExchangeAddStmt xadd => Call(nameof(ExchangeAdd), s.Env, s.Cpu, Const(xadd.Dst.IsMemory), CompilePlaceAddress(xadd.Dst, s), Const(xadd.Dst.Reg), Const(xadd.Reg), Const(xadd.Type)),
        BitTestStmt bit => Call(nameof(BitTest), s.Env, s.Cpu, Const(bit.Dst.IsMemory), CompilePlaceAddress(bit.Dst, s), Const(bit.Dst.Reg), CompileExpression(bit.Bit, s), Const(bit.BitString), Const(bit.Op), Const(bit.Type)),
        BitScanStmt scan => Call(nameof(BitScan), s.Cpu, Const(scan.Reg), CompileExpression(scan.Source, s), Const(scan.Forward), Const(scan.Type)),
        DoubleShiftStmt shift => Call(nameof(DoubleShift), s.Env, s.Cpu, Const(shift.Dst.IsMemory), CompilePlaceAddress(shift.Dst, s), Const(shift.Dst.Reg), Const(shift.Reg), CompileExpression(shift.Count, s), Const(shift.Left), Const(shift.Type)),
        DivideStmt divide => Call(nameof(Divide), s.Cpu, Const(divide.Type), CompileExpression(divide.Divisor, s), Const(divide.Signed)),
        AdjustStmt adjust => Call(nameof(AdjustDecimal), s.Cpu, Const(adjust.Kind), Const(adjust.Immediate)),
        StringStmt text => Call(nameof(StringOp), s.Env, s.Cpu, Const(text.Op), Const(text.Size), Const(text.Address32), Const((int)text.Prefixes), Const(text.Repeat)),
        Block { Statements.Count: 0 } => Expression.Empty(),
        Block block => Expression.Block(block.Statements.Select(inner => CompileStatement(inner, s))),
        _ => throw new ArgumentOutOfRangeException(nameof(statement)),
    };

    // メモリの置き場所ならアドレスの式、レジスタなら 0(ヘルパは IsMemory で見分ける)。
    static Expression CompilePlaceAddress(Place place, Scope s) => place.IsMemory ? CompileExpression(place.Address, s) : Const(0u);

    // アドレスを評価 → 書き込み先を検査 → 値を評価 → 書く(解釈器と同じ順序)。
    static Expression CompileStore(StoreStmt store, Scope s)
    {
        var address = Expression.Variable(typeof(uint), "address");
        return Expression.Block([address],
            Expression.Assign(address, CompileExpression(store.Address, s)),
            Call(nameof(Probe), s.Env, address, Const(store.Type), Const(store.ReadsFirst)),
            Call(store.Type == 0 ? nameof(Store8) : store.Type == 1 ? nameof(Store16) : nameof(Store32), s.Env, address, CompileExpression(store.Value, s)));
    }

    static Expression CompileExpression(Expr expr, Scope s) => expr switch
    {
        RegExpr r => ReadRegisterOf(s, r.Reg, r.Type),
        ImmExpr i => Const(i.Value & Mask(i.Type)),
        AluExpr a => Call(nameof(AluWithFlags), s.Cpu, Const(a.Type), CompileExpression(a.Left, s), CompileExpression(a.Right, s), Const(a.Kind)),
        IncDecExpr d => Call(nameof(IncrementWithFlags), s.Cpu, Const(d.Type), CompileExpression(d.Value, s), Const(d.Delta)),
        AddressExpr e => CompileAddress(e, s),
        MoffsExpr m => Expression.Add(Expression.Field(s.Cpu, SegmentOf(m.Prefixes) ?? nameof(CPU.ds_base)), Const(m.Offset)),
        LoadExpr l => CompileLoad(s, CompileExpression(l.Address, s), l.Type),
        PopExpr p => Call(nameof(Pop), s.Env, s.Cpu, Const(p.Type)),
        ExtendExpr x => Call(nameof(Extend), CompileExpression(x.Value, s), Const(x.FromType), Const(x.Signed), Const(x.ToType)),
        BranchTargetExpr b => Expression.Add(Expression.Property(s.Cpu, nameof(CPU.eip)), Const((uint)b.Length + b.Displacement)),
        ShiftExpr sh => Call(nameof(ShiftWithFlags), s.Cpu, Const(sh.Type), CompileExpression(sh.Value, s), CompileExpression(sh.Count, s), Const(sh.Kind)),
        UnaryExpr u => Call(nameof(Unary), s.Cpu, Const(u.Type), CompileExpression(u.Value, s), Const(u.Kind)),
        MultiplyExpr m => Call(nameof(MultiplySigned), s.Cpu, Const(m.Type), CompileExpression(m.Left, s), CompileExpression(m.Right, s)),
        ConditionExpr c => Expression.Condition(CompileCondition(s, c.Condition), Const(1u), Const(0u)),
        NearTargetExpr n => Call(nameof(NearTarget), s.Cpu, CompileExpression(n.Value, s), Const(n.Length), Const(n.Operand32)),
        ReturnAddressExpr r => Expression.Add(Expression.Property(s.Cpu, nameof(CPU.eip)), Const((uint)r.Length)),
        SignFillExpr f => Call(nameof(SignFill), CompileExpression(f.Value, s), Const(f.Type)),
        TempExpr t => s.Temp(t.Slot),
        FlagsExpr f => Expression.And(Eflags(s), Const(Mask(f.Type))),
        BswapExpr b => Call(nameof(Bswap), CompileExpression(b.Value, s)),
        XlatAddressExpr => Call(nameof(XlatAddress), s.Cpu),
        _ => throw new ArgumentOutOfRangeException(nameof(expr)),
    };

    /// 両バックエンドが共有する基本操作(高速コアと同じ演算規則とメモリ経路) ///////////////////////////////
    public static uint ReadRegister(CPU cpu, int reg, int type) => type switch
    {
        0 => FReg8(cpu, reg),
        1 => FReg16(cpu, reg),
        _ => ReadRegister32(cpu, reg),
    };

    public static void WriteRegister(CPU cpu, int reg, int type, uint value)
    {
        if (type == 0) FReg8Set(cpu, reg, (byte)value);
        else if (type == 1) FReg16Set(cpu, reg, (ushort)value);
        else FReg32Set(cpu, reg, value);
    }

    public static uint AluWithFlags(CPU cpu, int type, uint left, uint right, int kind) => CalculateAlu(cpu, type, left, right, kind);

    public static uint IncrementWithFlags(CPU cpu, int type, uint value, int delta)
    {
        SetIncDecFlags(cpu, type, value, delta);
        return (uint)(value + delta) & Mask(type);
    }

    // ModRM の実効アドレス。withSegment なら既定/オーバーライドのセグメント基底を足した線形アドレス、LEA はオフセットのみ。
    public static uint Linear(CPU cpu, ModRm modrm, bool address32, Prefix prefixes, bool withSegment)
    {
        var ovr = OverrideBase(prefixes, cpu);
        var (_, offset, segBase) = EffectiveAddress(cpu, modrm, address32, ovr ?? cpu.ds_base, ovr ?? cpu.ss_base);
        return withSegment ? offset + segBase : offset;
    }

    // コンパイル済みコード向け(ModRm のフィールドを個別に受ける)。
    public static uint LinearOf(CPU cpu, int mod, int rm, bool hasSib, int scale, int index, int baseRegister, uint disp, bool address32, int prefixes, bool withSegment) =>
        Linear(cpu, new ModRm((byte)mod, 0, (byte)rm, hasSib, (byte)scale, (byte)index, (byte)baseRegister, disp, 0), address32, (Prefix)prefixes, withSegment);

    public static uint MoffsAddress(CPU cpu, uint offset, Prefix prefixes) => (OverrideBase(prefixes, cpu) ?? cpu.ds_base) + offset;

    public static uint Load(EmuEnvironment env, uint address, int type) => EnvReadN(env, address, Bytes(type));

    public static void Store(EmuEnvironment env, uint address, int type, uint value) => EnvWriteN(env, address, Bytes(type), value);

    // コンパイル済みコード向けの幅別の入口(幅の分岐を省く)。
    public static byte Load8(EmuEnvironment env, uint address) => EnvGetMemoryData8(env, address);
    public static ushort Load16(EmuEnvironment env, uint address) => EnvGetMemoryData16(env, address);
    public static uint Load32(EmuEnvironment env, uint address) => EnvGetMemoryData32(env, address);
    public static void Store8(EmuEnvironment env, uint address, uint value) => EnvWriteN(env, address, 1, value);
    public static void Store16(EmuEnvironment env, uint address, uint value) => EnvWriteN(env, address, 2, value);
    public static void Store32(EmuEnvironment env, uint address, uint value) => EnvWriteN(env, address, 4, value);

    // 書き込み先のページを(ページ境界をまたぐなら両方)変換し、不在・保護違反ならここで #PF にする。
    // readsFirst(読み書き命令)なら、実機と同じく読み取りの変換を先に行う: 不在ページでは読み取りフォルト(err=0)、
    // 存在するが書き込み禁止なら書き込みフォルト(err=3)になる。変換結果は TLB に残るので、その後の Store はフォルトしない。
    public static void Probe(EmuEnvironment env, uint address, int type, bool readsFirst)
    {
        if (!env.Paging.Enabled) return;
        var size = Bytes(type);
        var crosses = (address & 0xFFF) > 0x1000 - (uint)size;
        if (readsFirst)
        {
            EnvTranslate(env, address, false);
            if (crosses) EnvTranslate(env, address + (uint)size - 1, false);
        }
        EnvTranslate(env, address, true);
        if (crosses) EnvTranslate(env, address + (uint)size - 1, true);
    }

    // PUSH: 書き込み先を検査して書いてから SP/ESP を減らす(高速コアは先に減らす。フォルト時に SP を変えないための順序)。
    public static void Push(EmuEnvironment env, CPU cpu, int type, uint value)
    {
        var size = Bytes(type);
        var pointer = cpu.stack32 ? (uint)(cpu.esp - size) : (ushort)(cpu.sp - size);
        var address = cpu.ss_base + pointer;
        Probe(env, address, type, readsFirst: false);
        EnvWriteN(env, address, size, value);
        if (cpu.stack32) cpu.esp = pointer; else cpu.sp = (ushort)pointer;
    }

    public static uint Pop(EmuEnvironment env, CPU cpu, int type) => FPop(env, cpu, type);

    public static uint Extend(uint value, int fromType, bool signed, int toType)
    {
        var extended = !signed ? value & Mask(fromType) : fromType == 0 ? (uint)(int)(sbyte)value : (uint)(int)(short)value;
        return extended & Mask(toType);
    }

    public static bool Condition(CPU cpu, int condition) => EvaluateCondition(cpu, condition);

    public static uint ConditionValue(CPU cpu, int condition) => EvaluateCondition(cpu, condition) ? 1u : 0u;

    // Group2: 80386 はシフト量を 5 ビットにマスクし、0 ならフラグを変えない。シフト系(kind>=4)は ZF/SF/PF も更新する。
    public static uint ShiftWithFlags(CPU cpu, int type, uint value, uint count, int kind)
    {
        var n = (int)(count & 0x1F);
        var (result, cf, of) = ComputeShift(value, n, kind, Bits(type), cpu.cf);
        if (n != 0)
        {
            var flags = cpu.eflags;
            if (kind >= 4)
                flags = (flags & ~0x8C5u) | (result == 0 ? 0x40u : 0) | ((result & Msb(type)) != 0 ? 0x80u : 0) | (Par(result) ? 0x4u : 0);
            cpu.eflags = (flags & ~0x801u) | (cf ? 0x1u : 0) | (of ? 0x800u : 0);
        }
        return result;
    }

    public static uint Unary(CPU cpu, int type, uint value, int kind)
    {
        if (kind == 0) return ~value & Mask(type);
        SetSubtractionFlags(cpu, type, 0, value);
        return (0u - value) & Mask(type);
    }

    // 2 オペランド IMUL(16/32 ビット): 下位の積を返し、CF=OF=上位への溢れ。
    public static uint MultiplySigned(CPU cpu, int type, uint left, uint right)
    {
        var product = type == 1 ? (long)(short)(ushort)left * (short)(ushort)right : (long)(int)left * (int)right;
        var overflow = type == 1 ? product < short.MinValue || product > short.MaxValue : product < int.MinValue || product > int.MaxValue;
        cpu.cf = overflow; cpu.of = overflow;
        return (uint)product & Mask(type);
    }

    // 1 オペランド MUL/IMUL: AL*v -> AX、AX*v -> DX:AX、EAX*v -> EDX:EAX。CF=OF=上位が有効か。
    public static void Multiply(CPU cpu, int type, uint value, bool signed)
    {
        if (!signed)
        {
            uint upper;
            if (type == 0) { var p = (uint)(cpu.al * value); upper = (p >> 8) & 0xFF; cpu.ax = (ushort)p; }
            else if (type == 1) { var p = cpu.ax * value; upper = (p >> 16) & 0xFFFF; cpu.ax = (ushort)p; cpu.dx = (ushort)(p >> 16); }
            else { var p = (ulong)cpu.eax * value; upper = (uint)(p >> 32); cpu.eax = (uint)p; cpu.edx = (uint)(p >> 32); }
            cpu.cf = upper != 0; cpu.of = upper != 0;
            return;
        }
        var product = type == 0 ? (sbyte)cpu.al * (sbyte)(byte)value
                    : type == 1 ? (short)cpu.ax * (short)(ushort)value
                    : (long)(int)cpu.eax * (int)value;
        var overflow = type == 0 ? product < sbyte.MinValue || product > sbyte.MaxValue
                     : type == 1 ? product < short.MinValue || product > short.MaxValue
                     : product < int.MinValue || product > int.MaxValue;
        if (type == 0) cpu.ax = (ushort)(short)product;
        else if (type == 1) { cpu.ax = (ushort)product; cpu.dx = (ushort)(product >> 16); }
        else { cpu.eax = (uint)product; cpu.edx = (uint)(product >> 32); }
        cpu.cf = overflow; cpu.of = overflow;
    }

    // 近傍の分岐先: 32 ビットはそのまま、16 ビットは EIP の上位 16 ビットを保つ(cpu.eip は命令の先頭)。
    public static uint NearTarget(CPU cpu, uint value, int length, bool operand32) =>
        operand32 ? value : ((cpu.eip + (uint)length) & 0xFFFF0000) | (value & 0xFFFF);

    public static uint SignFill(uint value, int type) => (value & Msb(type)) != 0 ? Mask(type) : 0;

    public static void AdjustStack(CPU cpu, int delta)
    {
        if (cpu.stack32) cpu.esp = (uint)(cpu.esp + delta); else cpu.sp = (ushort)(cpu.sp + delta);
    }

    // LEAVE: (E)SP <- (E)BP; (E)BP <- pop。読み取りを先に行い、フォルト時には何も変えない。
    public static void Leave(EmuEnvironment env, CPU cpu, int type)
    {
        var size = type == 1 ? 2 : 4;
        var pointer = cpu.stack32 ? cpu.ebp : cpu.bp;
        var value = EnvReadN(env, cpu.ss_base + pointer, size);
        if (cpu.stack32) cpu.esp = pointer + (uint)size; else cpu.sp = (ushort)(pointer + size);
        if (type == 2) cpu.ebp = value; else cpu.bp = (ushort)value;
    }

    // 0 CLC、1 STC、2 CMC、3 CLD、4 STD、5 CLI、6 STI、7 SAHF
    public static void Flag(CPU cpu, int kind)
    {
        switch (kind)
        {
            case 0: cpu.cf = false; break;
            case 1: cpu.cf = true; break;
            case 2: cpu.cf = !cpu.cf; break;
            case 3: cpu.df = false; break;
            case 4: cpu.df = true; break;
            case 5: cpu.jf = false; break;
            case 6: cpu.jf = true; break;
            default: Sahf(cpu); break;
        }
    }

    /// docs/adr/0003 段階 1 で加えた基本操作(通常コアと同じ規則。精密性のため、状態を変える前にページを検査する) ///////////////////////////////
    public static uint Flags(CPU cpu, int type) => cpu.eflags & Mask(type);

    public static uint Bswap(uint value) => ((value & 0xFF) << 24) | ((value & 0xFF00) << 8) | ((value >> 8) & 0xFF00) | (value >> 24);

    // XLAT: 通常コアと同じく DS:BX(16 ビット)+ AL(セグメントオーバーライドとアドレスサイズは見ない)。
    public static uint XlatAddress(CPU cpu) => cpu.ds_base + (ushort)(cpu.bx + cpu.al);

    public static void PopFlags(EmuEnvironment env, CPU cpu, int type) => Emu86.Alu.PopFlags(cpu, type, Pop(env, cpu, type));

    // PUSHA: AX,CX,DX,BX,(開始時の SP),BP,SI,DI。8 個分の書き込み先を先に検査してから書く(途中のフォルトで一部だけ push された状態を残さない)。
    public static void PushAll(EmuEnvironment env, CPU cpu, int type)
    {
        var size = Bytes(type);
        var original = ReadRegister(cpu, 4, type);
        var start = cpu.stack32 ? cpu.esp : cpu.sp;
        Span<uint> pointers = stackalloc uint[8];
        for (var index = 0; index < 8; index++)
            pointers[index] = cpu.stack32 ? (uint)(start - size * (index + 1)) : (ushort)(start - size * (index + 1));
        foreach (var pointer in pointers) Probe(env, cpu.ss_base + pointer, type, readsFirst: false);
        for (var index = 0; index < 8; index++)
            EnvWriteN(env, cpu.ss_base + pointers[index], size, index == 4 ? original : ReadRegister(cpu, index, type));
        if (cpu.stack32) cpu.esp = pointers[7]; else cpu.sp = (ushort)pointers[7];
    }

    // POPA: DI,SI,BP,(読み飛ばし),BX,DX,CX,AX。8 個を読んでから書く(読み取りは状態を変えないので、途中のフォルトでも精密)。
    public static void PopAll(EmuEnvironment env, CPU cpu, int type)
    {
        var size = Bytes(type);
        var start = cpu.stack32 ? cpu.esp : cpu.sp;
        Span<uint> values = stackalloc uint[8];
        for (var index = 0; index < 8; index++)
        {
            var pointer = cpu.stack32 ? (uint)(start + size * index) : (ushort)(start + size * index);
            values[index] = EnvReadN(env, cpu.ss_base + pointer, size);
        }
        for (var index = 0; index < 8; index++)
            if (index != 3) WriteRegister(cpu, 7 - index, type, values[index]);
        if (cpu.stack32) cpu.esp = (uint)(start + 8 * size); else cpu.sp = (ushort)(start + 8 * size);
    }

    // POP m: 書き込み先を検査してから pop する(pop が SP を進めた後に書き込みでフォルトしないため)。
    public static void PopIntoMemory(EmuEnvironment env, CPU cpu, uint address, int type)
    {
        Probe(env, address, type, readsFirst: false);
        Store(env, address, type, Pop(env, cpu, type));
    }

    // 0 LOOPNE、1 LOOPE、2 LOOP(CX を減らしてから判定)、3 JCXZ(CX は変えない)。通常コアと同じく CX(16 ビット)で数える。
    public static void Loop(CPU cpu, int kind, int length, uint displacement)
    {
        bool taken;
        if (kind == 3) taken = cpu.cx == 0;
        else
        {
            var count = (ushort)(cpu.cx - 1);
            cpu.cx = count;
            taken = kind switch { 0 => count != 0 && !cpu.zf, 1 => count != 0 && cpu.zf, _ => count != 0 };
        }
        cpu.eip = cpu.eip + (uint)length + (taken ? displacement : 0);
    }

    // CMPXCHG: acc と r/m を比べて(CMP のフラグ)、等しければ r/m <- reg、違えば acc <- r/m。メモリは検査してから読む。
    public static void CompareExchange(EmuEnvironment env, CPU cpu, bool memory, uint address, int dstReg, int reg, int type)
    {
        uint destination;
        if (memory) { Probe(env, address, type, readsFirst: true); destination = Load(env, address, type); }
        else destination = ReadRegister(cpu, dstReg, type);
        var accumulator = ReadRegister(cpu, 0, type);
        CalculateAlu(cpu, type, accumulator, destination, 7);
        if (accumulator == destination)
        {
            var source = ReadRegister(cpu, reg, type);
            if (memory) Store(env, address, type, source); else WriteRegister(cpu, dstReg, type, source);
        }
        else WriteRegister(cpu, 0, type, destination);
    }

    // XADD: reg <- r/m の旧値、r/m <- r/m + reg(ADD のフラグ)。レジスタ形式は通常コアと同じ順(reg を先に書く: 同じレジスタなら和が残る)。
    public static void ExchangeAdd(EmuEnvironment env, CPU cpu, bool memory, uint address, int dstReg, int reg, int type)
    {
        var source = ReadRegister(cpu, reg, type);
        if (memory)
        {
            Probe(env, address, type, readsFirst: true);
            var destination = Load(env, address, type);
            Store(env, address, type, CalculateAlu(cpu, type, destination, source, 0));
            WriteRegister(cpu, reg, type, destination);
        }
        else
        {
            var destination = ReadRegister(cpu, dstReg, type);
            var sum = CalculateAlu(cpu, type, destination, source, 0);
            WriteRegister(cpu, reg, type, destination);
            WriteRegister(cpu, dstReg, type, sum);
        }
    }

    // BT 系: r/m の bit 番目を CF へ。BTS/BTR/BTC は書き戻す(BT は書かない)。bitString ならビット番号(符号付き)でワードを選ぶ。
    public static void BitTest(EmuEnvironment env, CPU cpu, bool memory, uint address, int dstReg, uint bit, bool bitString, int op, int type)
    {
        var bits = Bits(type);
        if (memory && bitString) address += (uint)(((int)bit >> (type == 1 ? 4 : 5)) * Bytes(type));
        var mask = 1u << ((int)bit & (bits - 1));
        uint value;
        if (memory)
        {
            if (op != 0) Probe(env, address, type, readsFirst: true);
            value = Load(env, address, type);
        }
        else value = ReadRegister(cpu, dstReg, type);
        cpu.cf = (value & mask) != 0;
        if (op == 0) return;
        value = op switch { 1 => value | mask, 2 => value & ~mask, _ => value ^ mask };
        if (memory) Store(env, address, type, value); else WriteRegister(cpu, dstReg, type, value);
    }

    // BSF/BSR: 源が 0 なら ZF=1 で書かない。そうでなければ ZF=0 でビット位置を書く。
    public static void BitScan(CPU cpu, int reg, uint source, bool forward, int type)
    {
        if (source == 0) { cpu.zf = true; return; }
        cpu.zf = false;
        WriteRegister(cpu, reg, type, (uint)BitOps.BitScanIndex(source, forward, Bits(type)));
    }

    // SHLD/SHRD: count は 5 ビット。0 なら結果もフラグも変えない(書き戻しもしない)。
    public static void DoubleShift(EmuEnvironment env, CPU cpu, bool memory, uint address, int dstReg, int srcReg, uint count, bool left, int type)
    {
        var n = (int)(count & 0x1F);
        if (n == 0) return;
        uint destination;
        if (memory) { Probe(env, address, type, readsFirst: true); destination = Load(env, address, type); }
        else destination = ReadRegister(cpu, dstReg, type);
        var (result, cf, of) = Emu86.Alu.DoubleShift(destination, ReadRegister(cpu, srcReg, type), n, Bits(type), left);
        if (memory) Store(env, address, type, result); else WriteRegister(cpu, dstReg, type, result);
        cpu.cf = cf; cpu.of = of; cpu.zf = (result & Mask(type)) == 0; cpu.sf = (result & Msb(type)) != 0;
    }

    public static void Divide(CPU cpu, int type, uint divisor, bool signed) => Emu86.Alu.Divide(cpu, type, divisor, signed);

    public static void AdjustDecimal(CPU cpu, int kind, uint immediate) => Emu86.Alu.AdjustDecimal(cpu, kind, immediate);

    // 文字列命令。repeat: 0 単発、1 REP/REPE、2 REPNE。CMPS/SCAS だけ ZF で打ち切る。
    public static void StringOp(EmuEnvironment env, CPU cpu, int op, int size, bool address32, int prefixes, int repeat)
    {
        var source = OverrideBase((Prefix)prefixes, cpu) ?? cpu.ds_base;
        if (repeat == 0) { StringOps.Element(env, cpu, op, size, address32, source); return; }
        var repeatZf = repeat == 1;
        var checkZf = op is 0xA6 or 0xA7 or 0xAE or 0xAF;
        while ((address32 ? cpu.ecx : cpu.cx) != 0)
        {
            StringOps.Element(env, cpu, op, size, address32, source);
            if (address32) cpu.ecx -= 1; else cpu.cx = (ushort)(cpu.cx - 1);
            cpu.Decode.CommitRepeat(cpu);
            if (checkZf && cpu.zf != repeatZf) break;
        }
    }
}
