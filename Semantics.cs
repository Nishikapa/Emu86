using System.Linq.Expressions;
using static Emu86.Ext;

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

    // オペランドの置き場所: レジスタ番号か、アドレス式。
    readonly record struct Place(int Reg, Expr Address)
    {
        public static Place Register(int reg) => new(reg, null);
        public static Place Memory(Expr address) => new(-1, address);
    }

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

    static Emit<Unit> Simple(Stmt statement, int length) =>
        from _1 in Statement(statement)
        from _2 in Advance(length)
        select Unit.unit;

    // デコード済み命令を IR に下ろす。対象外なら null(呼び出し側は既存の経路へ)。
    public static Block Lower(Instruction instruction)
    {
        if (instruction.Repeat != null) return null;
        var op = instruction.Opcode;
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
    public static bool EndsBlock(Instruction instruction) => instruction.Opcode switch
    {
        (>= 0x70 and <= 0x7F) or 0xE8 or 0xE9 or 0xEB or 0xC2 or 0xC3 or 0xFB or (>= 0x0F80 and <= 0x0F8F) => true,
        0xFF => instruction.ModRm is { Reg: 2 or 4 },
        _ => false,
    };

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

    static Expression CompileStatement(Stmt statement, Scope s) => statement switch
    {
        SetRegStmt set => Call(nameof(WriteRegister), s.Cpu, Const(set.Reg), Const(set.Type), CompileExpression(set.Value, s)),
        StoreStmt store => CompileStore(store, s),
        PushStmt push => Call(nameof(Push), s.Env, s.Cpu, Const(push.Type), CompileExpression(push.Value, s)),
        EvalStmt eval => CompileExpression(eval.Value, s),
        AdvanceStmt advance => Expression.AddAssign(Expression.Property(s.Cpu, nameof(CPU.eip)), Const((uint)advance.Length)),
        SetEipStmt set => Expression.Assign(Expression.Property(s.Cpu, nameof(CPU.eip)), CompileExpression(set.Value, s)),
        IfStmt branch => Expression.IfThenElse(Call(nameof(Condition), s.Cpu, Const(branch.Condition)),
            CompileStatement(branch.Then, s), CompileStatement(branch.Else, s)),
        LetStmt let => Expression.Assign(s.Temp(let.Slot), CompileExpression(let.Value, s)),
        AdjustStackStmt adjust => Call(nameof(AdjustStack), s.Cpu, Const(adjust.Delta)),
        LeaveStmt leave => Call(nameof(Leave), s.Env, s.Cpu, Const(leave.Type)),
        MultiplyStmt multiply => Call(nameof(Multiply), s.Cpu, Const(multiply.Type), CompileExpression(multiply.Value, s), Const(multiply.Signed)),
        FlagStmt flag => Call(nameof(Flag), s.Cpu, Const(flag.Kind)),
        Block block => Expression.Block(block.Statements.Select(inner => CompileStatement(inner, s))),
        _ => throw new ArgumentOutOfRangeException(nameof(statement)),
    };

    // アドレスを評価 → 書き込み先を検査 → 値を評価 → 書く(解釈器と同じ順序)。
    static Expression CompileStore(StoreStmt store, Scope s)
    {
        var address = Expression.Variable(typeof(uint), "address");
        return Expression.Block([address],
            Expression.Assign(address, CompileExpression(store.Address, s)),
            Call(nameof(Probe), s.Env, address, Const(store.Type), Const(store.ReadsFirst)),
            Call(nameof(Store), s.Env, address, Const(store.Type), CompileExpression(store.Value, s)));
    }

    static Expression CompileExpression(Expr expr, Scope s) => expr switch
    {
        RegExpr r => Call(nameof(ReadRegister), s.Cpu, Const(r.Reg), Const(r.Type)),
        ImmExpr i => Const(i.Value & Mask(i.Type)),
        AluExpr a => Call(nameof(AluWithFlags), s.Cpu, Const(a.Type), CompileExpression(a.Left, s), CompileExpression(a.Right, s), Const(a.Kind)),
        IncDecExpr d => Call(nameof(IncrementWithFlags), s.Cpu, Const(d.Type), CompileExpression(d.Value, s), Const(d.Delta)),
        // ModRm を構造体定数にするとクロージャ経由のアンボックスが毎回起きるので、フィールドを整数定数で渡す。
        AddressExpr e => Call(nameof(LinearOf), s.Cpu,
            Const((int)e.ModRm.Mod), Const((int)e.ModRm.Rm), Const(e.ModRm.HasSib),
            Const((int)e.ModRm.Scale), Const((int)e.ModRm.Index), Const((int)e.ModRm.Base),
            Const(e.ModRm.Disp), Const(e.Address32), Const((int)e.Prefixes), Const(e.WithSegment)),
        MoffsExpr m => Call(nameof(MoffsAddress), s.Cpu, Const(m.Offset), Const(m.Prefixes)),
        LoadExpr l => Call(nameof(Load), s.Env, CompileExpression(l.Address, s), Const(l.Type)),
        PopExpr p => Call(nameof(Pop), s.Env, s.Cpu, Const(p.Type)),
        ExtendExpr x => Call(nameof(Extend), CompileExpression(x.Value, s), Const(x.FromType), Const(x.Signed), Const(x.ToType)),
        BranchTargetExpr b => Expression.Add(Expression.Property(s.Cpu, nameof(CPU.eip)), Const((uint)b.Length + b.Displacement)),
        ShiftExpr sh => Call(nameof(ShiftWithFlags), s.Cpu, Const(sh.Type), CompileExpression(sh.Value, s), CompileExpression(sh.Count, s), Const(sh.Kind)),
        UnaryExpr u => Call(nameof(Unary), s.Cpu, Const(u.Type), CompileExpression(u.Value, s), Const(u.Kind)),
        MultiplyExpr m => Call(nameof(MultiplySigned), s.Cpu, Const(m.Type), CompileExpression(m.Left, s), CompileExpression(m.Right, s)),
        ConditionExpr c => Call(nameof(ConditionValue), s.Cpu, Const(c.Condition)),
        NearTargetExpr n => Call(nameof(NearTarget), s.Cpu, CompileExpression(n.Value, s), Const(n.Length), Const(n.Operand32)),
        ReturnAddressExpr r => Expression.Add(Expression.Property(s.Cpu, nameof(CPU.eip)), Const((uint)r.Length)),
        SignFillExpr f => Call(nameof(SignFill), CompileExpression(f.Value, s), Const(f.Type)),
        TempExpr t => s.Temp(t.Slot),
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

    public static uint Load(EmuEnvironment env, uint address, int type) => EnvReadN(env, address, type == 0 ? 1 : type == 1 ? 2 : 4);

    public static void Store(EmuEnvironment env, uint address, int type, uint value) => EnvWriteN(env, address, type == 0 ? 1 : type == 1 ? 2 : 4, value);

    // 書き込み先のページを(ページ境界をまたぐなら両方)変換し、不在・保護違反ならここで #PF にする。
    // readsFirst(読み書き命令)なら、実機と同じく読み取りの変換を先に行う: 不在ページでは読み取りフォルト(err=0)、
    // 存在するが書き込み禁止なら書き込みフォルト(err=3)になる。変換結果は TLB に残るので、その後の Store はフォルトしない。
    public static void Probe(EmuEnvironment env, uint address, int type, bool readsFirst)
    {
        if (!env.PagingOn) return;
        var size = type == 0 ? 1 : type == 1 ? 2 : 4;
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
        var size = type == 0 ? 1 : type == 1 ? 2 : 4;
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
            cpu.cf = cf;
            cpu.of = of;
            if (kind >= 4) { cpu.zf = result == 0; cpu.sf = (result & Msb(type)) != 0; cpu.pf = Par(result); }
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

    // 0 CLC、1 STC、2 CMC、3 CLD、4 STD、5 CLI、6 STI
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
            default: cpu.jf = true; break;
        }
    }
}
