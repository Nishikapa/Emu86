using System.Text;
using static Emu86.Ext;

namespace Emu86;

// 即値の形。長さはオペランドサイズ(Operand/Rel/FarPointer)やアドレスサイズ(Moffs)で決まる。
public enum ImmediateKind
{
    None,
    Byte,       // imm8
    Word,       // imm16(RET imm16 など)
    Operand,    // imm16/32(オペランドサイズ)
    WordByte,   // imm16, imm8(ENTER)
    FarPointer, // offset16/32, selector16
    Moffs,      // アドレスサイズ幅の絶対オフセット(MOV AL,[moffs])
    Rel8,       // 相対分岐 8 ビット
    Rel,        // 相対分岐 16/32 ビット
}

// オペコード表の 1 項。Operands は Intel のオペコードマップ流の書式:
//   E=r/m G=reg(ModRM) I=即値 J=相対分岐先 O=moffs S=セグメントレジスタ(reg) C/D=CR/DR(reg) R=r/m(レジスタ形式)
//   M=メモリ r/m Z=オペコード下位 3 ビットのレジスタ A=far ポインタ即値、サイズ b=8 w=16 v=16/32 z=即値 16/32 d=32
//   固定: AL eAX DX CL 1 ES CS SS DS FS GS。ニーモニックの "A|B" はオペランドサイズ 16/32 で選ぶ。
//   Group はグループ命令で reg フィールドにより選ぶニーモニック(null は未定義)。
public readonly record struct OpcodeInfo(string Mnemonic, bool HasModRm, ImmediateKind Immediate, string Operands, string[] Group = null)
{
    public static readonly OpcodeInfo Undefined = new("(undefined)", false, ImmediateKind.None, "");
}

// デコード済みの 1 命令(プレフィックス、オペコード、ModRM、即値、長さ)。実行はしない。
public readonly record struct Instruction(uint Address, bool Code32, Prefix Prefixes, byte? Repeat, int Opcode, ModRm? ModRm, uint Immediate, uint Immediate2, int Length)
{
    public OpcodeInfo Info => OpcodeTable.Of(Opcode);
    public bool OperandSize32 => Code32 != ((Prefixes & Prefix.OperandSize) != 0);
    public bool AddressSize32 => Code32 != ((Prefixes & Prefix.AddressSize) != 0);
    public override string ToString() => Disassembler.Format(this);
}

static public partial class Ext
{
    // 命令全体のパーサ(冷たい経路向け: 逆アセンブル、診断)。プレフィックス、REP、オペコード(1 バイトまたは 0F xx)、
    // ModRM(表で決まる)、即値(表とオペランド/アドレスサイズで決まる)の順に読む。
    static public Parser<Instruction> InstructionParser(uint address, bool code32) =>
        from first in Prefixes
        from repeat in Fetch8.Where(value => value is 0xF2 or 0xF3).Select(value => (byte?)value).Or(((byte?)null).ToParser())
        from rest in Prefixes
        let prefixes = first | rest
        let operand32 = code32 != ((prefixes & Prefix.OperandSize) != 0)
        let address32 = code32 != ((prefixes & Prefix.AddressSize) != 0)
        from first1 in Fetch8
        from opcode in first1 == 0x0F ? Fetch8.Select(second => 0x0F00 | second) : ((int)first1).ToParser()
        let info = OpcodeTable.Of(opcode)
        from modrm in info.HasModRm ? ModRmParser(address32).Select(m => (ModRm?)m) : ((ModRm?)null).ToParser()
        from immediate in ImmediateParser(OpcodeTable.ImmediateOf(opcode, modrm), operand32, address32)
        select new Instruction(address, code32, prefixes, repeat, opcode, modrm, immediate.first, immediate.second, 0);

    static Parser<(uint first, uint second)> ImmediateParser(ImmediateKind kind, bool operand32, bool address32)
    {
        Parser<uint> operand = operand32 ? Fetch32 : Fetch16.Select(value => (uint)value);
        Parser<uint> relative = operand32 ? Fetch32 : Fetch16.Select(value => (uint)(int)(short)value);
        Parser<uint> moffs = address32 ? Fetch32 : Fetch16.Select(value => (uint)value);
        return kind switch
        {
            ImmediateKind.None => (0u, 0u).ToParser(),
            ImmediateKind.Byte => Fetch8.Select(value => ((uint)value, 0u)),
            ImmediateKind.Rel8 => Fetch8.Select(value => ((uint)(int)(sbyte)value, 0u)),
            ImmediateKind.Word => Fetch16.Select(value => ((uint)value, 0u)),
            ImmediateKind.Operand => operand.Select(value => (value, 0u)),
            ImmediateKind.Rel => relative.Select(value => (value, 0u)),
            ImmediateKind.Moffs => moffs.Select(value => (value, 0u)),
            ImmediateKind.WordByte => from word in Fetch16 from level in Fetch8 select ((uint)word, (uint)level),
            ImmediateKind.FarPointer => from offset in operand from selector in Fetch16 select (offset, (uint)selector),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    // CS:(E)IP の命令を 1 つデコードする(実行はしない。フェッチが #PF になればそのまま伝わる)。
    static public Instruction DecodeInstruction(EmuEnvironment env, CPU cpu)
    {
        var code = CodeSource.At(env, cpu);
        var (_, instruction, length) = InstructionParser(cpu.eip, cpu.code32)(code, 0);
        return instruction with { Length = length };
    }
}

// オペコードの属性表。1 バイト命令は 0x00-0xFF、2 バイト命令は 0x0F00 | 2 バイト目。
public static class OpcodeTable
{
    static readonly string[] Alu = ["ADD", "OR", "ADC", "SBB", "AND", "SUB", "XOR", "CMP"];
    static readonly string[] Shift = ["ROL", "ROR", "RCL", "RCR", "SHL", "SHR", "SAL", "SAR"];
    static readonly string[] Group3 = ["TEST", "TEST", "NOT", "NEG", "MUL", "IMUL", "DIV", "IDIV"];
    static readonly string[] Group4 = ["INC", "DEC", null, null, null, null, null, null];
    static readonly string[] Group5 = ["INC", "DEC", "CALL", "CALLF", "JMP", "JMPF", "PUSH", null];
    static readonly string[] Group6 = ["SLDT", "STR", "LLDT", "LTR", "VERR", "VERW", null, null];
    static readonly string[] Group7 = ["SGDT", "SIDT", "LGDT", "LIDT", "SMSW", null, "LMSW", "INVLPG"];
    static readonly string[] Group8 = [null, null, null, null, "BT", "BTS", "BTR", "BTC"];
    static readonly string[] Group9 = [null, "CMPXCHG8B", null, null, null, null, null, null];
    static readonly string[] Condition = ["O", "NO", "B", "AE", "E", "NE", "BE", "A", "S", "NS", "P", "NP", "L", "GE", "LE", "G"];
    static readonly string[] Segments = ["ES", "CS", "SS", "DS", "FS", "GS"];

    static readonly OpcodeInfo[] oneByte = BuildOneByte();
    static readonly OpcodeInfo[] twoByte = BuildTwoByte();

    public static OpcodeInfo Of(int opcode) =>
        opcode >= 0x0F00 ? twoByte[opcode & 0xFF] : oneByte[opcode];

    // 即値の形。Group3(F6/F7)は /0 と /1(TEST)だけが即値を持つ。
    public static ImmediateKind ImmediateOf(int opcode, ModRm? modrm) => opcode switch
    {
        0xF6 => modrm is { Reg: 0 or 1 } ? ImmediateKind.Byte : ImmediateKind.None,
        0xF7 => modrm is { Reg: 0 or 1 } ? ImmediateKind.Operand : ImmediateKind.None,
        _ => Of(opcode).Immediate,
    };

    // オペランド書式(同じく Group3 の TEST だけ即値が付く)。
    public static string OperandsOf(int opcode, ModRm? modrm) => opcode switch
    {
        0xF6 when modrm is { Reg: 0 or 1 } => "Eb,Ib",
        0xF7 when modrm is { Reg: 0 or 1 } => "Ev,Iz",
        _ => Of(opcode).Operands,
    };

    static OpcodeInfo[] BuildOneByte()
    {
        var table = Enumerable.Repeat(OpcodeInfo.Undefined, 256).ToArray();
        void Set(int opcode, string mnemonic, string operands = "", ImmediateKind immediate = ImmediateKind.None, bool modrm = false, string[] group = null) =>
            table[opcode] = new(mnemonic, modrm, immediate, operands, group);
        void ModRm(int opcode, string mnemonic, string operands, ImmediateKind immediate = ImmediateKind.None, string[] group = null) =>
            Set(opcode, mnemonic, operands, immediate, modrm: true, group);

        for (var kind = 0; kind < 8; kind++)
        {
            var b = kind * 8;
            ModRm(b + 0, Alu[kind], "Eb,Gb"); ModRm(b + 1, Alu[kind], "Ev,Gv");
            ModRm(b + 2, Alu[kind], "Gb,Eb"); ModRm(b + 3, Alu[kind], "Gv,Ev");
            Set(b + 4, Alu[kind], "AL,Ib", ImmediateKind.Byte); Set(b + 5, Alu[kind], "eAX,Iz", ImmediateKind.Operand);
        }
        Set(0x06, "PUSH", "ES"); Set(0x07, "POP", "ES"); Set(0x0E, "PUSH", "CS");
        Set(0x16, "PUSH", "SS"); Set(0x17, "POP", "SS"); Set(0x1E, "PUSH", "DS"); Set(0x1F, "POP", "DS");
        Set(0x27, "DAA"); Set(0x2F, "DAS"); Set(0x37, "AAA"); Set(0x3F, "AAS");
        for (var reg = 0; reg < 8; reg++)
        {
            Set(0x40 + reg, "INC", "Zv"); Set(0x48 + reg, "DEC", "Zv");
            Set(0x50 + reg, "PUSH", "Zv"); Set(0x58 + reg, "POP", "Zv");
            Set(0x91 + reg, "XCHG", "eAX,Zv");
            Set(0xB0 + reg, "MOV", "Zb,Ib", ImmediateKind.Byte); Set(0xB8 + reg, "MOV", "Zv,Iz", ImmediateKind.Operand);
        }
        Set(0x60, "PUSHA|PUSHAD"); Set(0x61, "POPA|POPAD");
        ModRm(0x62, "BOUND", "Gv,M"); ModRm(0x63, "ARPL", "Ew,Gw");
        Set(0x68, "PUSH", "Iz", ImmediateKind.Operand); ModRm(0x69, "IMUL", "Gv,Ev,Iz", ImmediateKind.Operand);
        Set(0x6A, "PUSH", "Ib", ImmediateKind.Byte); ModRm(0x6B, "IMUL", "Gv,Ev,Ib", ImmediateKind.Byte);
        Set(0x6C, "INSB"); Set(0x6D, "INSW|INSD"); Set(0x6E, "OUTSB"); Set(0x6F, "OUTSW|OUTSD");
        for (var condition = 0; condition < 16; condition++)
            Set(0x70 + condition, "J" + Condition[condition], "Jb", ImmediateKind.Rel8);
        ModRm(0x80, "Group1", "Eb,Ib", ImmediateKind.Byte, Alu); ModRm(0x81, "Group1", "Ev,Iz", ImmediateKind.Operand, Alu);
        ModRm(0x82, "Group1", "Eb,Ib", ImmediateKind.Byte, Alu); ModRm(0x83, "Group1", "Ev,Ib", ImmediateKind.Byte, Alu);
        ModRm(0x84, "TEST", "Eb,Gb"); ModRm(0x85, "TEST", "Ev,Gv"); ModRm(0x86, "XCHG", "Eb,Gb"); ModRm(0x87, "XCHG", "Ev,Gv");
        ModRm(0x88, "MOV", "Eb,Gb"); ModRm(0x89, "MOV", "Ev,Gv"); ModRm(0x8A, "MOV", "Gb,Eb"); ModRm(0x8B, "MOV", "Gv,Ev");
        ModRm(0x8C, "MOV", "Ew,Sw"); ModRm(0x8D, "LEA", "Gv,M"); ModRm(0x8E, "MOV", "Sw,Ew"); ModRm(0x8F, "POP", "Ev");
        Set(0x90, "NOP"); Set(0x98, "CBW|CWDE"); Set(0x99, "CWD|CDQ"); Set(0x9A, "CALLF", "Ap", ImmediateKind.FarPointer);
        Set(0x9B, "WAIT"); Set(0x9C, "PUSHF|PUSHFD"); Set(0x9D, "POPF|POPFD"); Set(0x9E, "SAHF"); Set(0x9F, "LAHF");
        Set(0xA0, "MOV", "AL,Ob", ImmediateKind.Moffs); Set(0xA1, "MOV", "eAX,Ov", ImmediateKind.Moffs);
        Set(0xA2, "MOV", "Ob,AL", ImmediateKind.Moffs); Set(0xA3, "MOV", "Ov,eAX", ImmediateKind.Moffs);
        Set(0xA4, "MOVSB"); Set(0xA5, "MOVSW|MOVSD"); Set(0xA6, "CMPSB"); Set(0xA7, "CMPSW|CMPSD");
        Set(0xA8, "TEST", "AL,Ib", ImmediateKind.Byte); Set(0xA9, "TEST", "eAX,Iz", ImmediateKind.Operand);
        Set(0xAA, "STOSB"); Set(0xAB, "STOSW|STOSD"); Set(0xAC, "LODSB"); Set(0xAD, "LODSW|LODSD");
        Set(0xAE, "SCASB"); Set(0xAF, "SCASW|SCASD");
        ModRm(0xC0, "Group2", "Eb,Ib", ImmediateKind.Byte, Shift); ModRm(0xC1, "Group2", "Ev,Ib", ImmediateKind.Byte, Shift);
        Set(0xC2, "RET", "Iw", ImmediateKind.Word); Set(0xC3, "RET");
        ModRm(0xC4, "LES", "Gv,Mp"); ModRm(0xC5, "LDS", "Gv,Mp");
        ModRm(0xC6, "MOV", "Eb,Ib", ImmediateKind.Byte); ModRm(0xC7, "MOV", "Ev,Iz", ImmediateKind.Operand);
        Set(0xC8, "ENTER", "Iw,Ib", ImmediateKind.WordByte); Set(0xC9, "LEAVE");
        Set(0xCA, "RETF", "Iw", ImmediateKind.Word); Set(0xCB, "RETF"); Set(0xCC, "INT3"); Set(0xCD, "INT", "Ib", ImmediateKind.Byte);
        Set(0xCE, "INTO"); Set(0xCF, "IRET|IRETD");
        ModRm(0xD0, "Group2", "Eb,1", group: Shift); ModRm(0xD1, "Group2", "Ev,1", group: Shift);
        ModRm(0xD2, "Group2", "Eb,CL", group: Shift); ModRm(0xD3, "Group2", "Ev,CL", group: Shift);
        Set(0xD4, "AAM", "Ib", ImmediateKind.Byte); Set(0xD5, "AAD", "Ib", ImmediateKind.Byte); Set(0xD7, "XLAT");
        for (var escape = 0xD8; escape <= 0xDF; escape++) ModRm(escape, "FPU", "");
        Set(0xE0, "LOOPNE", "Jb", ImmediateKind.Rel8); Set(0xE1, "LOOPE", "Jb", ImmediateKind.Rel8);
        Set(0xE2, "LOOP", "Jb", ImmediateKind.Rel8); Set(0xE3, "JCXZ|JECXZ", "Jb", ImmediateKind.Rel8);
        Set(0xE4, "IN", "AL,Ib", ImmediateKind.Byte); Set(0xE5, "IN", "eAX,Ib", ImmediateKind.Byte);
        Set(0xE6, "OUT", "Ib,AL", ImmediateKind.Byte); Set(0xE7, "OUT", "Ib,eAX", ImmediateKind.Byte);
        Set(0xE8, "CALL", "Jz", ImmediateKind.Rel); Set(0xE9, "JMP", "Jz", ImmediateKind.Rel);
        Set(0xEA, "JMPF", "Ap", ImmediateKind.FarPointer); Set(0xEB, "JMP", "Jb", ImmediateKind.Rel8);
        Set(0xEC, "IN", "AL,DX"); Set(0xED, "IN", "eAX,DX"); Set(0xEE, "OUT", "DX,AL"); Set(0xEF, "OUT", "DX,eAX");
        Set(0xF4, "HLT"); Set(0xF5, "CMC");
        ModRm(0xF6, "Group3", "Eb", group: Group3); ModRm(0xF7, "Group3", "Ev", group: Group3);
        Set(0xF8, "CLC"); Set(0xF9, "STC"); Set(0xFA, "CLI"); Set(0xFB, "STI"); Set(0xFC, "CLD"); Set(0xFD, "STD");
        ModRm(0xFE, "Group4", "Eb", group: Group4); ModRm(0xFF, "Group5", "Ev", group: Group5);
        return table;
    }

    static OpcodeInfo[] BuildTwoByte()
    {
        var table = Enumerable.Repeat(OpcodeInfo.Undefined, 256).ToArray();
        void Set(int opcode, string mnemonic, string operands = "", ImmediateKind immediate = ImmediateKind.None, bool modrm = false, string[] group = null) =>
            table[opcode] = new(mnemonic, modrm, immediate, operands, group);
        void ModRm(int opcode, string mnemonic, string operands, ImmediateKind immediate = ImmediateKind.None, string[] group = null) =>
            Set(opcode, mnemonic, operands, immediate, modrm: true, group);

        ModRm(0x00, "Group6", "Ew", group: Group6); ModRm(0x01, "Group7", "Ms", group: Group7);
        ModRm(0x02, "LAR", "Gv,Ew"); ModRm(0x03, "LSL", "Gv,Ew");
        Set(0x06, "CLTS"); Set(0x08, "INVD"); Set(0x09, "WBINVD"); Set(0x0B, "UD2");
        ModRm(0x20, "MOV", "Rd,Cd"); ModRm(0x21, "MOV", "Rd,Dd"); ModRm(0x22, "MOV", "Cd,Rd"); ModRm(0x23, "MOV", "Dd,Rd");
        Set(0x30, "WRMSR"); Set(0x31, "RDTSC"); Set(0x32, "RDMSR"); Set(0x33, "RDPMC");
        for (var condition = 0; condition < 16; condition++)
        {
            ModRm(0x40 + condition, "CMOV" + Condition[condition], "Gv,Ev");
            Set(0x80 + condition, "J" + Condition[condition], "Jz", ImmediateKind.Rel);
            ModRm(0x90 + condition, "SET" + Condition[condition], "Eb");
        }
        Set(0xA0, "PUSH", "FS"); Set(0xA1, "POP", "FS"); Set(0xA2, "CPUID"); ModRm(0xA3, "BT", "Ev,Gv");
        ModRm(0xA4, "SHLD", "Ev,Gv,Ib", ImmediateKind.Byte); ModRm(0xA5, "SHLD", "Ev,Gv,CL");
        Set(0xA8, "PUSH", "GS"); Set(0xA9, "POP", "GS"); ModRm(0xAB, "BTS", "Ev,Gv");
        ModRm(0xAC, "SHRD", "Ev,Gv,Ib", ImmediateKind.Byte); ModRm(0xAD, "SHRD", "Ev,Gv,CL"); ModRm(0xAF, "IMUL", "Gv,Ev");
        ModRm(0xB0, "CMPXCHG", "Eb,Gb"); ModRm(0xB1, "CMPXCHG", "Ev,Gv"); ModRm(0xB2, "LSS", "Gv,Mp"); ModRm(0xB3, "BTR", "Ev,Gv");
        ModRm(0xB4, "LFS", "Gv,Mp"); ModRm(0xB5, "LGS", "Gv,Mp"); ModRm(0xB6, "MOVZX", "Gv,Eb"); ModRm(0xB7, "MOVZX", "Gv,Ew");
        ModRm(0xBA, "Group8", "Ev,Ib", ImmediateKind.Byte, Group8); ModRm(0xBB, "BTC", "Ev,Gv");
        ModRm(0xBC, "BSF", "Gv,Ev"); ModRm(0xBD, "BSR", "Gv,Ev"); ModRm(0xBE, "MOVSX", "Gv,Eb"); ModRm(0xBF, "MOVSX", "Gv,Ew");
        ModRm(0xC0, "XADD", "Eb,Gb"); ModRm(0xC1, "XADD", "Ev,Gv"); ModRm(0xC7, "Group9", "Mq", group: Group9);
        for (var reg = 0; reg < 8; reg++) Set(0xC8 + reg, "BSWAP", "Zd");
        return table;
    }
}

// デコード済み命令の文字列化(診断用)。例: "66 0F B6 C8  MOVZX cx, al"、"0F C7 C8  CMPXCHG8B eax (register form)".
public static class Disassembler
{
    static readonly string[] Reg8 = ["al", "cl", "dl", "bl", "ah", "ch", "dh", "bh"];
    static readonly string[] Reg16 = ["ax", "cx", "dx", "bx", "sp", "bp", "si", "di"];
    static readonly string[] Reg32 = ["eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi"];
    static readonly string[] Seg = ["es", "cs", "ss", "ds", "fs", "gs"];
    static readonly string[] Base16 = ["bx+si", "bx+di", "bp+si", "bp+di", "si", "di", "bp", "bx"];

    public static string Format(Instruction instruction)
    {
        var info = instruction.Info;
        var mnemonic = info.Group != null && instruction.ModRm is { } group
            ? info.Group[group.Reg] ?? $"{info.Mnemonic} /{group.Reg} (undefined)"
            : Pick(info.Mnemonic, instruction.OperandSize32);
        var immediates = 0; // 2 つ目の即値(ENTER の level)は Immediate2
        var operands = string.Join(", ", OpcodeTable.OperandsOf(instruction.Opcode, instruction.ModRm).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(code => Operand(instruction, code, code[0] == 'I' ? immediates++ : 0)));
        var repeat = instruction.Repeat switch { 0xF3 => "REP ", 0xF2 => "REPNE ", _ => "" };
        var lockPrefix = (instruction.Prefixes & Prefix.Lock) != 0 ? "LOCK " : "";
        return operands.Length == 0 ? $"{lockPrefix}{repeat}{mnemonic}" : $"{lockPrefix}{repeat}{mnemonic} {operands}";
    }

    // 命令のバイト列(フェッチし直す)と逆アセンブルを並べる。
    public static string Describe(EmuEnvironment env, CPU cpu)
    {
        try
        {
            var instruction = DecodeInstruction(env, cpu);
            var code = CodeSource.At(env, cpu);
            var bytes = string.Join(" ", Enumerable.Range(0, instruction.Length).Select(offset => code.Byte(offset).ToString("x2")));
            return $"{bytes}  {Format(instruction)}";
        }
        catch (CpuFaultException)
        {
            return "(unmapped)";
        }
    }

    static string Pick(string mnemonic, bool operand32)
    {
        var bar = mnemonic.IndexOf('|');
        return bar < 0 ? mnemonic : operand32 ? mnemonic[(bar + 1)..] : mnemonic[..bar];
    }

    static string Operand(Instruction instruction, string code, int immediateIndex)
    {
        var size = code.Length > 1 ? code[1] : 'v';
        var modrm = instruction.ModRm ?? default;
        var immediate = immediateIndex == 0 ? instruction.Immediate : instruction.Immediate2;
        switch (code[0])
        {
            case 'E': case 'M': case 'R':
                return modrm.Mod == 3 ? Register(modrm.Rm, size, instruction.OperandSize32) : Memory(instruction, modrm);
            case 'G': return Register(modrm.Reg, size, instruction.OperandSize32);
            case 'S': return modrm.Reg < Seg.Length ? Seg[modrm.Reg] : $"seg{modrm.Reg}";
            case 'C': return $"cr{modrm.Reg}";
            case 'D': return $"dr{modrm.Reg}";
            case 'Z': return Register(instruction.Opcode & 7, size, instruction.OperandSize32);
            case 'I': return "0x" + immediate.ToString(size == 'b' ? "x2" : size == 'w' ? "x4" : instruction.OperandSize32 ? "x8" : "x4");
            case 'J':
            {
                var next = instruction.Address + (uint)instruction.Length;
                var target = next + instruction.Immediate;
                return "0x" + (instruction.Code32 ? target.ToString("x8") : ((ushort)target).ToString("x4"));
            }
            case 'O': return $"{SegmentPrefix(instruction, "ds")}[0x{instruction.Immediate:x}]";
            case 'A': return $"0x{instruction.Immediate2:x4}:0x{instruction.Immediate.ToString(instruction.OperandSize32 ? "x8" : "x4")}";
            default:
                return code switch
                {
                    "eAX" => instruction.OperandSize32 ? "eax" : "ax",
                    "AL" => "al", "DX" => "dx", "CL" => "cl", "1" => "1",
                    _ => code.ToLowerInvariant(),
                };
        }
    }

    static string Register(int reg, char size, bool operand32) => size switch
    {
        'b' => Reg8[reg],
        'w' => Reg16[reg],
        'd' => Reg32[reg],
        _ => operand32 ? Reg32[reg] : Reg16[reg],
    };

    static string SegmentPrefix(Instruction instruction, string defaultSegment)
    {
        var prefixes = instruction.Prefixes;
        var segment = (prefixes & Prefix.Es) != 0 ? "es" : (prefixes & Prefix.Cs) != 0 ? "cs" : (prefixes & Prefix.Ss) != 0 ? "ss"
            : (prefixes & Prefix.Ds) != 0 ? "ds" : (prefixes & Prefix.Fs) != 0 ? "fs" : (prefixes & Prefix.Gs) != 0 ? "gs" : null;
        return segment == null || segment == defaultSegment ? "" : segment + ":";
    }

    // メモリオペランド: 16 ビットは [bx+si+disp]、32 ビットは [base+index*scale+disp]。
    static string Memory(Instruction instruction, in ModRm modrm)
    {
        var text = new StringBuilder();
        if (!instruction.AddressSize32)
        {
            var usesBp = modrm.Rm is 2 or 3 or 6 && !(modrm.Mod == 0 && modrm.Rm == 6);
            text.Append(SegmentPrefix(instruction, usesBp ? "ss" : "ds")).Append('[');
            if (modrm.Mod == 0 && modrm.Rm == 6) text.Append("0x").Append(((ushort)modrm.Disp).ToString("x4"));
            else
            {
                text.Append(Base16[modrm.Rm]);
                if (modrm.Mod != 0) text.Append(Displacement(modrm.Disp, modrm.Mod == 2 ? 16 : 0));
            }
            return text.Append(']').ToString();
        }
        var parts = new List<string>();
        var stackBased = false;
        if (modrm.HasSib)
        {
            if (!(modrm.Mod == 0 && modrm.Base == 5)) { parts.Add(Reg32[modrm.Base]); stackBased = modrm.Base is 4 or 5; }
            if (modrm.Index != 4) parts.Add(modrm.Scale == 0 ? Reg32[modrm.Index] : $"{Reg32[modrm.Index]}*{1 << modrm.Scale}");
        }
        else if (!(modrm.Mod == 0 && modrm.Rm == 5)) { parts.Add(Reg32[modrm.Rm]); stackBased = modrm.Rm == 5; }
        text.Append(SegmentPrefix(instruction, stackBased ? "ss" : "ds")).Append('[').Append(string.Join("+", parts));
        var hasDisp = modrm.Mod != 0 || (modrm.HasSib ? modrm.Base == 5 : modrm.Rm == 5);
        if (hasDisp)
            text.Append(parts.Count == 0 ? "0x" + modrm.Disp.ToString("x8") : Displacement(modrm.Disp, modrm.Mod == 2 || modrm.Mod == 0 ? 32 : 0));
        return text.Append(']').ToString();
    }

    // 変位: 8 ビット(符号拡張済み)は ±0x.. で、幅指定があればその桁数で。
    static string Displacement(uint disp, int bits) =>
        bits == 0 ? ((int)disp < 0 ? "-0x" + ((uint)-(int)disp).ToString("x") : "+0x" + disp.ToString("x"))
        : bits == 16 ? "+0x" + ((ushort)disp).ToString("x4")
        : ((int)disp < 0 ? "-0x" + ((uint)-(int)disp).ToString("x") : "+0x" + disp.ToString("x"));
}
