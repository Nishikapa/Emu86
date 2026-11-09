using System.Linq.Expressions;
using static Emu86.Conversions;
using static Emu86.Alu;
using static Emu86.Decoder;

namespace Emu86;

// IR のコンパイラ(System.Linq.Expressions): 文と式を式木にし、デリゲートにコンパイルする(翻訳キャッシュのブロックが使う)。
public static partial class Semantics
{
    // ブロック翻訳向け: 1 命令の IR を、与えられたパラメータ式に対する式木にする(一時変数は命令ごとに宣言する)。
    public static Expression CompileInto(Stmt statement, ParameterExpression env, ParameterExpression cpu)
    {
        var scope = new Scope(env, cpu);
        var body = CompileStatement(statement, scope);
        return scope.Temps.Count == 0 ? body : Expression.Block(scope.Temps, body);
    }

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
        Expression.Property(s.Cpu, (type switch { 0 => Registers8, 1 => Registers16, _ => Registers32 })[reg]);
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

    // セグメントオーバーライドの基底フィールド名(ES CS SS DS FS GS の順)。無ければ null。
    static readonly string[] SegmentBaseFields = [nameof(CPU.es_base), nameof(CPU.cs_base), nameof(CPU.ss_base), nameof(CPU.ds_base), nameof(CPU.fs_base), nameof(CPU.gs_base)];
    static string SegmentOf(Prefix prefixes) => OverrideSegment(prefixes) is int sreg ? SegmentBaseFields[sreg] : null;

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
        StateStmt state => Call(nameof(RunState), s.Env, s.Cpu, Expression.Constant(state.State)),
        ArgumentStateStmt state => Call(nameof(RunState), s.Env, s.Cpu, Expression.Invoke(Expression.Constant(state.Build), CompileExpression(state.Argument, s))),
        PortInStmt input => Call(nameof(PortIn), s.Env, s.Cpu, Const(input.Type), CompileExpression(input.Port, s)),
        PortOutStmt output => Call(nameof(PortOut), s.Env, Const(output.Type), CompileExpression(output.Port, s), CompileExpression(output.Value, s)),
        RdtscStmt => Call(nameof(Rdtsc), s.Env, s.Cpu),
        CpuidStmt => Call(nameof(Cpuid), s.Cpu),
        CompareExchange8Stmt cmpxchg8 => Call(nameof(CompareExchange8), s.Env, s.Cpu, CompileExpression(cmpxchg8.Address, s)),
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
        XlatAddressExpr x => Call(nameof(XlatAddress), s.Cpu, Const(x.Address32), Const((int)x.Prefixes)),
        SegmentExpr segment => Call(nameof(ReadSegment), s.Cpu, Const(segment.Sreg)),
        _ => throw new ArgumentOutOfRangeException(nameof(expr)),
    };
}
