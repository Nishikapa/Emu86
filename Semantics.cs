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
// の 2 つが消費する。対象(Lower が null を返さないもの): ALU の 00-3D と Group1、INC/DEC、PUSH/POP(レジスタ・即値)、
// MOV(88-8B、A0-A3、B0-BF、C6/C7)、TEST/CMP、LEA、MOVZX/MOVSX、Jcc/JMP rel。メモリオペランドは実効アドレスの式で表し、
// 読み書きは Environment の共通経路(ページフォルトは例外のまま)を呼ぶ。

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

public abstract record Stmt
{
    public abstract void Run(EmuEnvironment env, CPU cpu);
}

public sealed record SetRegStmt(int Reg, int Type, Expr Value) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu) => Semantics.WriteRegister(cpu, Reg, Type, Value.Evaluate(env, cpu));
}

public sealed record StoreStmt(Expr Address, int Type, Expr Value) : Stmt
{
    public override void Run(EmuEnvironment env, CPU cpu)
    {
        var address = Address.Evaluate(env, cpu);
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
    static Emit<Unit> Write(Place place, int type, Expr value) => sink =>
    {
        sink.Add(place.Address == null ? new SetRegStmt(place.Reg, type, value) : new StoreStmt(place.Address, type, value));
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
        from _1 in kind == 7 ? Evaluate(result) : Write(dst, type, result)
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
        from _1 in Write(place, type, result)
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
            0x84 or 0x85 => Test(Rm(), Read(Reg(), type), type, length),
            >= 0x88 and <= 0x8B => (op & 2) != 0 ? Move(Reg(), Read(Rm(), type), type, length) : Move(Rm(), Read(Reg(), type), type, length),
            0x8D when instruction.ModRm is { Mod: not 3 } => Move(Reg(), _ => new AddressExpr(instruction.ModRm.Value, instruction.AddressSize32, prefixes, false), typeW, length),
            0xA0 => Move(Acc(), Read(Place.Memory(moffs), 0), 0, length),
            0xA1 => Move(Acc(), Read(Place.Memory(moffs), typeW), typeW, length),
            0xA2 => Move(Place.Memory(moffs), Read(Acc(), 0), 0, length),
            0xA3 => Move(Place.Memory(moffs), Read(Acc(), typeW), typeW, length),
            0xA8 or 0xA9 => Test(Acc(), Imm(type), type, length),
            >= 0xB0 and <= 0xB7 => Move(Place.Register(op & 7), Imm(0), 0, length),
            >= 0xB8 and <= 0xBF => Move(Place.Register(op & 7), Imm(typeW), typeW, length),
            0xC6 or 0xC7 => Move(Rm(), Imm(type), type, length),
            0xE9 or 0xEB => _ => Branch(-1, length, instruction.Immediate)(_),
            0xFE when reg is 0 or 1 => IncrementDecrement(Rm(), reg == 0 ? 1 : -1, 0, length),
            0xFF when reg is 0 or 1 => IncrementDecrement(Rm(), reg == 0 ? 1 : -1, typeW, length),
            >= 0x0F80 and <= 0x0F8F => _ => Branch(op & 0xF, length, instruction.Immediate)(_),
            0x0FB6 or 0x0FB7 => MoveExtended(reg, Rm(), op == 0x0FB6 ? 0 : 1, false, typeW, length),
            0x0FBE or 0x0FBF => MoveExtended(reg, Rm(), op == 0x0FBE ? 0 : 1, true, typeW, length),
            _ => null,
        };
        if (program == null) return null;
        var sink = new List<Stmt>();
        program(sink);
        return new Block(sink);
    }

    /// 解釈器(各ノードの Evaluate/Run) ///////////////////////////////
    public static void Interpret(Stmt statement, EmuEnvironment env, CPU cpu) => statement.Run(env, cpu);

    /// コンパイラ(System.Linq.Expressions) ///////////////////////////////
    public static Action<EmuEnvironment, CPU> Compile(Block block)
    {
        var env = Expression.Parameter(typeof(EmuEnvironment), "env");
        var cpu = Expression.Parameter(typeof(CPU), "cpu");
        var body = Expression.Block(block.Statements.Select(statement => CompileStatement(statement, env, cpu)));
        return Expression.Lambda<Action<EmuEnvironment, CPU>>(body, env, cpu).Compile();
    }

    static Expression Call(string method, params Expression[] arguments) =>
        Expression.Call(typeof(Semantics).GetMethod(method), arguments);

    static Expression CompileStatement(Stmt statement, ParameterExpression env, ParameterExpression cpu) => statement switch
    {
        SetRegStmt set => Call(nameof(WriteRegister), cpu, Expression.Constant(set.Reg), Expression.Constant(set.Type), CompileExpression(set.Value, env, cpu)),
        StoreStmt store => Call(nameof(Store), env, CompileExpression(store.Address, env, cpu), Expression.Constant(store.Type), CompileExpression(store.Value, env, cpu)),
        PushStmt push => Call(nameof(Push), env, cpu, Expression.Constant(push.Type), CompileExpression(push.Value, env, cpu)),
        EvalStmt eval => CompileExpression(eval.Value, env, cpu),
        AdvanceStmt advance => Expression.AddAssign(Expression.Property(cpu, nameof(CPU.eip)), Expression.Constant((uint)advance.Length)),
        SetEipStmt set => Expression.Assign(Expression.Property(cpu, nameof(CPU.eip)), CompileExpression(set.Value, env, cpu)),
        IfStmt branch => Expression.IfThenElse(Call(nameof(Condition), cpu, Expression.Constant(branch.Condition)),
            CompileStatement(branch.Then, env, cpu), CompileStatement(branch.Else, env, cpu)),
        Block block => Expression.Block(block.Statements.Select(inner => CompileStatement(inner, env, cpu))),
        _ => throw new ArgumentOutOfRangeException(nameof(statement)),
    };

    static Expression CompileExpression(Expr expr, ParameterExpression env, ParameterExpression cpu) => expr switch
    {
        RegExpr r => Call(nameof(ReadRegister), cpu, Expression.Constant(r.Reg), Expression.Constant(r.Type)),
        ImmExpr i => Expression.Constant(i.Value & Mask(i.Type)),
        AluExpr a => Call(nameof(AluWithFlags), cpu, Expression.Constant(a.Type), CompileExpression(a.Left, env, cpu), CompileExpression(a.Right, env, cpu), Expression.Constant(a.Kind)),
        IncDecExpr d => Call(nameof(IncrementWithFlags), cpu, Expression.Constant(d.Type), CompileExpression(d.Value, env, cpu), Expression.Constant(d.Delta)),
        // ModRm を構造体定数にするとクロージャ経由のアンボックスが毎回起きるので、フィールドを整数定数で渡す。
        AddressExpr e => Call(nameof(LinearOf), cpu,
            Expression.Constant((int)e.ModRm.Mod), Expression.Constant((int)e.ModRm.Rm), Expression.Constant(e.ModRm.HasSib),
            Expression.Constant((int)e.ModRm.Scale), Expression.Constant((int)e.ModRm.Index), Expression.Constant((int)e.ModRm.Base),
            Expression.Constant(e.ModRm.Disp), Expression.Constant(e.Address32), Expression.Constant((int)e.Prefixes), Expression.Constant(e.WithSegment)),
        MoffsExpr m => Call(nameof(MoffsAddress), cpu, Expression.Constant(m.Offset), Expression.Constant(m.Prefixes)),
        LoadExpr l => Call(nameof(Load), env, CompileExpression(l.Address, env, cpu), Expression.Constant(l.Type)),
        PopExpr p => Call(nameof(Pop), env, cpu, Expression.Constant(p.Type)),
        ExtendExpr x => Call(nameof(Extend), CompileExpression(x.Value, env, cpu), Expression.Constant(x.FromType), Expression.Constant(x.Signed), Expression.Constant(x.ToType)),
        BranchTargetExpr b => Expression.Add(Expression.Property(cpu, nameof(CPU.eip)), Expression.Constant((uint)b.Length + b.Displacement)),
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

    public static void Push(EmuEnvironment env, CPU cpu, int type, uint value) => FPush(env, cpu, type, value);

    public static uint Pop(EmuEnvironment env, CPU cpu, int type) => FPop(env, cpu, type);

    public static uint Extend(uint value, int fromType, bool signed, int toType)
    {
        var extended = !signed ? value & Mask(fromType) : fromType == 0 ? (uint)(int)(sbyte)value : (uint)(int)(short)value;
        return extended & Mask(toType);
    }

    public static bool Condition(CPU cpu, int condition) => EvaluateCondition(cpu, condition);
}
