using static Emu86.Conversions;

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
// 段階 2 で通常コアから移した命令: セグメントレジスタのロード(MOV Sreg/POP Sreg/LES 系)、MOV r/m,Sreg、PUSH Sreg、IN/OUT、far JMP/CALL/RET、
// INT n/INT3/INTO/IRET、ENTER、RDTSC、CPUID、FWAIT。記述子の検査や割り込みの配送は通常コアの State(Segments/Interrupts)を
// そのまま 1 つの文(StateStmt)として実行し、フォルト時は文の前の CPU へ戻す(通常コアの実行器と同じ回復)。
// メモリオペランドは実効アドレスの式で表し、読み書きは Mmu の共通経路(ページフォルトは例外のまま)を呼ぶ。
// 状態を変える前に読んだ値を後で使う命令(XCHG、CALL 間接)は Let で一時変数に束縛する(TempExpr)。
//
// ファイルの分け方: Semantics.Ir.cs = ノード(Expr/Stmt)、このファイル = Emit モナドと IR への下ろし(Lower)、
// Semantics.Compiler.cs = コンパイラ、Semantics.Runtime.cs = 解釈器とコンパイル済みコードが共有する基本操作。

// IR を組み立てる Writer モナド。値はシンボリック(Expr)で、文は sink へ追記される。
public delegate T Emit<T>(List<Stmt> sink);

public static partial class Semantics
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

    // dst <- dst op src。CMP(kind 7)は書き戻さない(通常コアと同じ: 書き戻すとページの Dirty ビットが立つ)。
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

    // POP m: 書き込み先のアドレスに ESP を使う形(pop [esp+8])は、ESP を進めた後の値で実効アドレスを求める(通常コアの Pop_8F と同じ)。
    // ESP を進めてアドレスを一時変数に束縛し、ESP を戻してから「書き込み先の検査 → pop → 書く」を行うので、フォルト時の CPU は命令の先頭のまま。
    static Emit<Unit> PopIntoMemory(Expr address, int type, int length) =>
        from _1 in Statement(new AdjustStackStmt(Bytes(type)))
        from bound in Let(address)
        from _2 in Statement(new AdjustStackStmt(-Bytes(type)))
        from _3 in Simple(new PopIntoMemoryStmt(bound, type), length)
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
            >= 0x0F18 and <= 0x0F1F => Advance(length), // PREFETCHh と予約/多バイト NOP: ModRM を持つが何もしない
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
            0x8F when reg == 0 => instruction.ModRm is { Mod: 3 } ? PopInto(Rm(), typeW, length) : PopIntoMemory(Rm().Address, typeW, length),
            0x9C => PushValue(typeW, _ => new FlagsExpr(typeW), length),
            0x9D => Simple(new PopFlagsStmt(typeW), length),
            0x9E => Simple(new FlagStmt(7), length),
            0x9F => Move(Place.Register(4), _ => new FlagsExpr(0), 0, length),
            0xD7 => Move(Acc(), _ => new LoadExpr(new XlatAddressExpr(instruction.AddressSize32, prefixes), 0), 0, length),
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
            // ---- docs/adr/0003 段階 2: 通常コアの State を呼ぶ文 ----
            0x06 or 0x0E or 0x16 or 0x1E => PushValue(typeW, _ => new SegmentExpr(op >> 3), length),
            0x07 or 0x17 or 0x1F => Statement(new StateStmt(Instructions.PopSregInto(op >> 3, typeW).Then(CpuStates.IpInc(length)), "POP Sreg")),
            0x8C when reg <= 5 => Move(Rm(), _ => new SegmentExpr(reg), 1, length),
            0x8E when reg <= 5 && reg != 1 => // MOV CS は通常コアに残す(ブロックの途中で CS を変えない)
                from selector in Read(Rm(), typeW)
                from _ in Statement(new ArgumentStateStmt(selector, value => Segments.LoadSReg(reg, (ushort)value).Then(CpuStates.IpInc(length)), "MOV Sreg"))
                select Unit.unit,
            0x9A => Statement(new StateStmt(CpuStates.IpInc(length).Then(Instructions.PushReturnFar(typeW)).Then(Instructions.FarTransfer((instruction.Immediate, (ushort)instruction.Immediate2))), "CALL far")),
            0x9B => Statement(new StateStmt(Fpu.WaitAvailable.Then(CpuStates.IpInc(length)), "FWAIT")),
            // x87(D8-DF): 表にあるエンコーディングを、通常コアの State のまま 1 つの文にする(#NM の検査も State の中)。
            // メモリ形式は実効アドレスを式で求めてから、その番地の命令を組み立てる。
            >= 0xD8 and <= 0xDF when instruction.ModRm is { Mod: 3 } st =>
                Fpu.RegisterInstruction(op, st.Reg, st.Rm) is { } x87 ? Statement(new StateStmt(x87.Then(CpuStates.IpInc(length)), "x87")) : null,
            >= 0xD8 and <= 0xDF =>
                Fpu.MemoryInstruction(op, reg) is { } x87 ? Statement(new ArgumentStateStmt(Rm().Address, address => x87(address).Then(CpuStates.IpInc(length)), "x87 m")) : null,
            0xC4 or 0xC5 when instruction.ModRm is { Mod: not 3 } =>
                Statement(new ArgumentStateStmt(Rm().Address, address => Instructions.LoadFarPointer(reg, op == 0xC4 ? 0 : 3, (true, address), typeW).Then(CpuStates.IpInc(length)), "LES/LDS")),
            0xC8 => Statement(new StateStmt(DataAccess.Enter((ushort)instruction.Immediate, (int)instruction.Immediate2, typeW).Then(CpuStates.IpInc(length)), "ENTER")),
            0xCA => Statement(new StateStmt(Instructions.FarReturnOf(typeW, (ushort)instruction.Immediate), "RETF imm16")),
            0xCB => Statement(new StateStmt(Instructions.FarReturnOf(typeW, 0), "RETF")),
            0xCC => Statement(new StateStmt(CpuStates.IpInc(length).Then(Interrupts.Interrupt(3)), "INT3")),
            0xCD => Statement(new StateStmt(CpuStates.IpInc(length).Then(Instructions.SoftwareInterrupt((int)instruction.Immediate)), "INT")),
            0xCE => Statement(new IfStmt(0, new StateStmt(CpuStates.IpInc(length).Then(Interrupts.Interrupt(4)), "INTO"), new AdvanceStmt(length))),
            0xCF => Statement(new StateStmt(Interrupts.Iret, "IRET")),
            0xE4 or 0xE5 => Simple(new PortInStmt(op & 1, new ImmExpr(instruction.Immediate, 0)), length),
            0xE6 or 0xE7 => Simple(new PortOutStmt(op & 1, new ImmExpr(instruction.Immediate, 0), new RegExpr(0, op & 1)), length),
            0xEA => Statement(new StateStmt(Instructions.FarTransfer((instruction.Immediate, (ushort)instruction.Immediate2)), "JMP far")),
            0xEC or 0xED => Simple(new PortInStmt(type, new RegExpr(2, 1)), length),
            0xEE or 0xEF => Simple(new PortOutStmt(type, new RegExpr(2, 1), new RegExpr(0, type)), length),
            0xFF when reg == 3 && instruction.ModRm is { Mod: not 3 } =>
                Statement(new ArgumentStateStmt(Rm().Address, address => CpuStates.IpInc(length).Then(Instructions.CallFar_rm((true, address), typeW)), "CALL far m")),
            0xFF when reg == 5 && instruction.ModRm is { Mod: not 3 } =>
                Statement(new ArgumentStateStmt(Rm().Address, address => Instructions.JmpFar_rm((true, address), typeW), "JMP far m")),
            0x0F31 => Simple(new RdtscStmt(), length),
            0x0FA0 or 0x0FA8 => PushValue(typeW, _ => new SegmentExpr(op == 0x0FA0 ? 4 : 5), length),
            0x0FA1 or 0x0FA9 => Statement(new StateStmt(Instructions.PopSregInto(op == 0x0FA1 ? 4 : 5, typeW).Then(CpuStates.IpInc(length)), "POP FS/GS")),
            0x0FA2 => Simple(new CpuidStmt(), length),
            0x0FC7 when reg == 1 && instruction.ModRm is { Mod: not 3 } => Simple(new CompareExchange8Stmt(Rm().Address), length),
            0x0FB2 or 0x0FB4 or 0x0FB5 when instruction.ModRm is { Mod: not 3 } =>
                Statement(new ArgumentStateStmt(Rm().Address, address => Instructions.LoadFarPointer(reg, op == 0x0FB2 ? 2 : op == 0x0FB4 ? 4 : 5, (true, address), typeW).Then(CpuStates.IpInc(length)), "LSS/LFS/LGS")),
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
        0x9A or 0xEA or 0xCA or 0xCB or 0xCC or 0xCD or 0xCE => true, // far 転送、INT
        0x0F31 => true, // RDTSC: env.Tsc を読むのでブロックの最後に置く(UsesClock)
        0xFF => instruction.ModRm is { Reg: 2 or 3 or 4 or 5 },
        _ => StopsChain(instruction),
    };

    // IRET は IF を立てうる。IN/OUT/INS/OUTS はデバイスが割り込み要求を立てうる。
    public static bool StopsChain(Instruction instruction) =>
        instruction.Opcode is 0xFB or 0xF4 or 0x9D or 0xCF or (>= 0x6C and <= 0x6F) or (>= 0xE4 and <= 0xE7) or (>= 0xEC and <= 0xEF);

    // env.Tsc(命令数の時計)を読む命令: RDTSC と、PIT/RTC/PM タイマなど時刻を返しうるポート入出力。
    public static bool UsesClock(Instruction instruction) =>
        instruction.Opcode is 0x0F31 or (>= 0x6C and <= 0x6F) or (>= 0xE4 and <= 0xE7) or (>= 0xEC and <= 0xEF);

    /// 解釈器(各ノードの Evaluate/Run) ///////////////////////////////
    public static void Interpret(Stmt statement, EmuEnvironment env, CPU cpu) => statement.Run(env, cpu);
}
