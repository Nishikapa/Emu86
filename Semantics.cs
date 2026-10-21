using System.Linq.Expressions;
using static Emu86.Ext;

namespace Emu86;

// 命令の意味論を第一級のデータ(IR)で表す概念実証(docs/adr/0002)。実行経路では使っていない。
//
// State モナドの命令定義は C# のクロージャ(SetCpu(cpu => ...))で意味を閉じ込めるため、解析もコンパイルも
// できない。ここでは「値を生む式(Expr)」と「状態を変える文(Stmt)」を record で表し、LINQ クエリは
// Emit モナド(文を書き出す Writer)で IR を組み立てる(シンボリック評価)。組み立てた IR は
//   Interpreter: 木を歩いて実行する(通常コアと同じ意味論の参照実装)
//   Compiler   : System.Linq.Expressions で Action<CPU> にコンパイルする(高速コアの自動導出)
// の 2 つが消費する。対象は PoC として ALU(00-3D、80/81/83 のレジスタ形式)、MOV(88-8B のレジスタ形式、B0-BF)、
// INC/DEC(40-4F)に限る。

public abstract record Expr;
public sealed record RegExpr(int Reg, int Type) : Expr;                                 // 汎用レジスタの読み出し
public sealed record ImmExpr(uint Value, int Type) : Expr;                              // 定数
public sealed record AluExpr(int Kind, Expr Left, Expr Right, int Type) : Expr;         // CalculateAlu(フラグ更新を伴う)
public sealed record IncDecExpr(Expr Value, int Delta, int Type) : Expr;                // INC/DEC(CF 以外のフラグ更新)

public abstract record Stmt;
public sealed record SetRegStmt(int Reg, int Type, Expr Value) : Stmt;
public sealed record AdvanceStmt(int Length) : Stmt;                                    // EIP += Length
public sealed record Block(IReadOnlyList<Stmt> Statements) : Stmt;

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

    static Emit<Expr> Read(int reg, int type) => _ => new RegExpr(reg, type);
    static Emit<Expr> Const(uint value, int type) => _ => new ImmExpr(value, type);
    static Emit<Expr> Alu(int kind, Expr left, Expr right, int type) => _ => new AluExpr(kind, left, right, type);
    static Emit<Expr> IncDec(Expr value, int delta, int type) => _ => new IncDecExpr(value, delta, type);
    static Emit<Unit> Write(int reg, int type, Expr value) => sink => { sink.Add(new SetRegStmt(reg, type, value)); return Unit.unit; };
    static Emit<Unit> Advance(int length) => sink => { sink.Add(new AdvanceStmt(length)); return Unit.unit; };

    // dst <- dst op src(ALU)。
    static Emit<Unit> Binary(int kind, int dst, int src, int type, int length) =>
        from left in Read(dst, type)
        from right in Read(src, type)
        from result in Alu(kind, left, right, type)
        from _1 in Write(dst, type, result)
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> BinaryImmediate(int kind, int dst, uint immediate, int type, int length) =>
        from left in Read(dst, type)
        from right in Const(immediate, type)
        from result in Alu(kind, left, right, type)
        from _1 in Write(dst, type, result)
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> MoveRegister(int dst, int src, int type, int length) =>
        from value in Read(src, type)
        from _1 in Write(dst, type, value)
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> MoveImmediate(int dst, uint immediate, int type, int length) =>
        from value in Const(immediate, type)
        from _1 in Write(dst, type, value)
        from _2 in Advance(length)
        select Unit.unit;

    static Emit<Unit> IncrementDecrement(int reg, int delta, int type, int length) =>
        from value in Read(reg, type)
        from result in IncDec(value, delta, type)
        from _1 in Write(reg, type, result)
        from _2 in Advance(length)
        select Unit.unit;

    // デコード済み命令を IR に下ろす。対象外なら null(呼び出し側は既存の経路へ)。
    public static Block Lower(Instruction instruction)
    {
        var op = instruction.Opcode;
        var modrm = instruction.ModRm;
        var w = (op & 1) != 0;
        var type = w ? (instruction.OperandSize32 ? 2 : 1) : 0;
        var length = instruction.Length;
        if ((instruction.Prefixes & ~(Prefix.OperandSize)) != 0 || instruction.Repeat != null) return null;
        Emit<Unit> program = op switch
        {
            <= 0x3D when (op & 7) <= 3 && modrm is { Mod: 3 } m =>
                (op & 2) != 0 ? Binary((op >> 3) & 7, m.Reg, m.Rm, type, length) : Binary((op >> 3) & 7, m.Rm, m.Reg, type, length),
            <= 0x3D when (op & 7) is 4 or 5 =>
                BinaryImmediate((op >> 3) & 7, 0, instruction.Immediate, type, length),
            >= 0x40 and <= 0x4F =>
                IncrementDecrement(op & 7, op < 0x48 ? 1 : -1, instruction.OperandSize32 ? 2 : 1, length),
            0x80 or 0x81 when modrm is { Mod: 3 } m =>
                BinaryImmediate(m.Reg, m.Rm, instruction.Immediate, type, length),
            0x83 when modrm is { Mod: 3 } m =>
                BinaryImmediate(m.Reg, m.Rm, (uint)(int)(sbyte)instruction.Immediate, type, length),
            >= 0x88 and <= 0x8B when modrm is { Mod: 3 } m =>
                (op & 2) != 0 ? MoveRegister(m.Reg, m.Rm, type, length) : MoveRegister(m.Rm, m.Reg, type, length),
            >= 0xB0 and <= 0xBF =>
                MoveImmediate(op & 7, instruction.Immediate, op < 0xB8 ? 0 : instruction.OperandSize32 ? 2 : 1, length),
            _ => null,
        };
        if (program == null) return null;
        var sink = new List<Stmt>();
        program(sink);
        return new Block(sink);
    }

    /// 解釈器 ///////////////////////////////
    public static uint Evaluate(Expr expr, CPU cpu) => expr switch
    {
        RegExpr r => ReadRegister(cpu, r.Reg, r.Type),
        ImmExpr i => i.Value & Mask(i.Type),
        AluExpr a => CalculateAlu(cpu, a.Type, Evaluate(a.Left, cpu), Evaluate(a.Right, cpu), a.Kind),
        IncDecExpr d => IncrementWithFlags(cpu, d.Type, Evaluate(d.Value, cpu), d.Delta),
        _ => throw new ArgumentOutOfRangeException(nameof(expr)),
    };

    public static void Interpret(Stmt statement, CPU cpu)
    {
        switch (statement)
        {
            case SetRegStmt set: WriteRegister(cpu, set.Reg, set.Type, Evaluate(set.Value, cpu)); break;
            case AdvanceStmt advance: cpu.eip += (uint)advance.Length; break;
            case Block block: foreach (var inner in block.Statements) Interpret(inner, cpu); break;
            default: throw new ArgumentOutOfRangeException(nameof(statement));
        }
    }

    /// コンパイラ(System.Linq.Expressions) ///////////////////////////////
    public static Action<CPU> Compile(Block block)
    {
        var cpu = Expression.Parameter(typeof(CPU), "cpu");
        var body = Expression.Block(block.Statements.Select(statement => CompileStatement(statement, cpu)));
        return Expression.Lambda<Action<CPU>>(body, cpu).Compile();
    }

    static Expression CompileStatement(Stmt statement, ParameterExpression cpu) => statement switch
    {
        SetRegStmt set => Expression.Call(typeof(Semantics).GetMethod(nameof(WriteRegister)), cpu,
            Expression.Constant(set.Reg), Expression.Constant(set.Type), CompileExpression(set.Value, cpu)),
        AdvanceStmt advance => Expression.AddAssign(Expression.Property(cpu, nameof(CPU.eip)), Expression.Constant((uint)advance.Length)),
        Block block => Expression.Block(block.Statements.Select(inner => CompileStatement(inner, cpu))),
        _ => throw new ArgumentOutOfRangeException(nameof(statement)),
    };

    static Expression CompileExpression(Expr expr, ParameterExpression cpu) => expr switch
    {
        RegExpr r => Expression.Call(typeof(Semantics).GetMethod(nameof(ReadRegister)), cpu, Expression.Constant(r.Reg), Expression.Constant(r.Type)),
        ImmExpr i => Expression.Constant(i.Value & Mask(i.Type)),
        AluExpr a => Expression.Call(typeof(Semantics).GetMethod(nameof(AluWithFlags)), cpu,
            Expression.Constant(a.Type), CompileExpression(a.Left, cpu), CompileExpression(a.Right, cpu), Expression.Constant(a.Kind)),
        IncDecExpr d => Expression.Call(typeof(Semantics).GetMethod(nameof(IncrementWithFlags)), cpu,
            Expression.Constant(d.Type), CompileExpression(d.Value, cpu), Expression.Constant(d.Delta)),
        _ => throw new ArgumentOutOfRangeException(nameof(expr)),
    };

    /// 両バックエンドが共有する基本操作(高速コアと同じ演算規則) ///////////////////////////////
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
}
