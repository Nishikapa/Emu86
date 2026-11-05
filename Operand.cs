using static Emu86.CPU;
using static Emu86.Conversions;
using static Emu86.DataAccess;
using static Emu86.AluStates;
using static Emu86.CpuStates;
using static Emu86.Ports;

namespace Emu86;

// オペランド(Lens): 読み出す State と書き戻す関数の組。整数命令は Data(Operand)、FPU は double(Real)で同じ形を使う
// (GlobalUsings の別名)。汎用レジスタ、ModRM で決まる r/m(メモリまたはレジスタ)、読み出した即値、スタックトップ、
// I/O ポート、セグメントレジスタ、EFLAGS、FPU の ST(i) とメモリ形式を同じ形で扱う。d ビットで向きが決まる命令
// (ALU の 00-3B、MOV の 88-8B)や、保存と読み込みが対になる命令(PUSH/POP、IN/OUT、SGDT/LGDT、FNSAVE/FRSTOR)は、
// オペランドを入れ替えるだけで書ける。
public readonly record struct Lens<T>(State<T> Read, Func<T, State<Unit>> Write);

static public partial class Operands
{
    // 汎用レジスタ reg(type: 0=8 ビット、1=16 ビット、2=32 ビット)。
    static public Operand Reg(int reg, int type) =>
        new(GetRegData(reg, type), data => SetRegData(reg, data));

    // ModRM の r/m(実効アドレスはデコード済み)。
    static public Operand Rm(MemAddr addr, int type) =>
        new(Choice(type, GetMemOrRegData8(addr), GetMemOrRegData16(addr), GetMemOrRegData32(addr)), data => SetMemOrRegData(addr, data));

    // 読み出し済みの値(即値など)。書き戻しはできない。
    static public Operand Const(Data data) =>
        new(data.ToState(), _ => throw new InvalidOperationException("constant operand is read-only"));

    // スタックトップ: 読むと POP、書くと PUSH。PUSH r は Move(Stack(type), Reg(...))、POP r は Move(Reg(...), Stack(type))。
    static public Operand Stack(int type) =>
        new(Pop(type), Push);

    // I/O ポート: 読むと IN、書くと OUT(幅は type)。ポート番号は State で与える(即値か DX)。
    // IN acc, port は Move(Reg(0, type), Port(port, type))、OUT port, acc は Move(Port(port, type), Reg(0, type))。
    static public Operand Port(State<int> port, int type) =>
        new(from address in port
            from value in GetDataFromEnvCpu((env, _) => EnvInPortN(env, address, Bytes(type)))
            select value.ToTypeData(type),
            data => from address in port
                    from _ in Effect((env, _) => EnvOutPortN(env, address, Bytes(type), data.Value))
                    select Unit.unit);

    // DX が指すポート(IN/OUT の DX 形式と INS/OUTS)。
    static public State<int> Dx => _dx.Get().Select(dx => (int)dx);

    // セグメントレジスタ: 読むとセレクタを type の幅へゼロ拡張し、書くと下位 16 ビットをロードする(プロテクトモードでは記述子も読み直す)。
    // PUSH Sreg は Move(Stack(type), Sreg(reg, type))、POP Sreg は Move(Sreg(reg, type), Stack(type))。
    static public Operand Sreg(int reg, int type) =>
        new(GetSRegData(reg).Select(selector => ((uint)selector).ToTypeData(type)), data => Segments.LoadSReg(reg, (ushort)data.Value));

    // EFLAGS: 読むと type の幅の下位、書くと POPF の規則(CPL > 0 では IOPL と IF を保護する。Alu.PopFlags。IR と共有)。
    // PUSHF は Move(Stack(type), Flags(type))、POPF は Move(Flags(type), Stack(type))。
    static public Operand Flags(int type) =>
        new(GetDataFromCpu(cpu => cpu.eflags.ToTypeData(type)), data => Effect(cpu => Alu.PopFlags(cpu, type, data.Value)));

    // dst <- src
    static public State<Unit> Move<T>(Lens<T> dst, Lens<T> src) =>
        from value in src.Read
        from _ in dst.Write(value)
        select Unit.unit;

    // dst <- dst op src。kind は Calc と同じ(0=ADD 1=OR 2=ADC 3=SBB 4=AND 5=SUB 6=XOR 7=CMP)。
    // CMP(kind 7)はフラグだけで、書き戻さない(メモリ先に書き戻すとページの Dirty ビットが立ってしまう。IR と同じ)。
    static public State<Unit> Binary(Operand dst, Operand src, int kind) =>
        from left in dst.Read
        from right in src.Read
        from result in Calc(left, right, kind)
        from _ in kind == 7 ? States.Pass : dst.Write(result)
        select Unit.unit;

    // left & right のフラグ(CF=OF=0、ZF/SF/PF)だけ更新し、結果は捨てる。TEST。
    static public State<Unit> Test(Operand left, Operand right) =>
        from a in left.Read
        from b in right.Read
        from _ in update_eflags((a.Value & b.Value).ToTypeData(a.type))
        select Unit.unit;

    // x <-> y
    static public State<Unit> Swap<T>(Lens<T> x, Lens<T> y) =>
        from a in x.Read
        from b in y.Read
        from _1 in x.Write(b)
        from _2 in y.Write(a)
        select Unit.unit;
}
