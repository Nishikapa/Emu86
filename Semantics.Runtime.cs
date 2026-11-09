using static Emu86.Conversions;
using static Emu86.Operands;
using static Emu86.Alu;
using static Emu86.Mmu;
using static Emu86.Ports;
using static Emu86.Decoder;

namespace Emu86;

// IR の解釈器(各ノードの Evaluate/Run)とコンパイル済みコードの両方が呼ぶ基本操作(通常コアと同じ演算規則とメモリ経路)。
public static partial class Semantics
{
    public static uint ReadRegister(CPU cpu, int reg, int type) => type switch
    {
        0 => ReadRegister8(cpu, reg),
        1 => ReadRegister16(cpu, reg),
        _ => ReadRegister32(cpu, reg),
    };

    public static void WriteRegister(CPU cpu, int reg, int type, uint value)
    {
        if (type == 0) WriteRegister8(cpu, reg, (byte)value);
        else if (type == 1) WriteRegister16(cpu, reg, (ushort)value);
        else WriteRegister32(cpu, reg, value);
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
        // ページをまたぐときは次のページの先頭を検査する(フォルトの線形アドレスは、1 バイトずつ書く通常コアと同じく、そのページの先頭)。
        var next = (address & ~0xFFFu) + 0x1000;
        var crosses = (address & 0xFFF) > 0x1000 - (uint)size;
        if (readsFirst)
        {
            EnvTranslate(env, address, false);
            if (crosses) EnvTranslate(env, next, false);
        }
        EnvTranslate(env, address, true);
        if (crosses) EnvTranslate(env, next, true);
    }

    // PUSH: 書き込み先を検査して書いてから SP/ESP を減らす(通常コアの Push は先に減らして実行器が戻す。フォルト時に SP を変えないための順序)。
    public static void Push(EmuEnvironment env, CPU cpu, int type, uint value)
    {
        var size = Bytes(type);
        var pointer = cpu.stack32 ? (uint)(cpu.esp - size) : (ushort)(cpu.sp - size);
        var address = cpu.ss_base + pointer;
        Probe(env, address, type, readsFirst: false);
        EnvWriteN(env, address, size, value);
        if (cpu.stack32) cpu.esp = pointer; else cpu.sp = (ushort)pointer;
    }

    // POP: スタックトップから読んでから SP/ESP を増やす(読み取りのフォルトでは何も変えない)。
    public static uint Pop(EmuEnvironment env, CPU cpu, int type)
    {
        var size = Bytes(type);
        var value = EnvReadN(env, cpu.ss_base + (cpu.stack32 ? cpu.esp : cpu.sp), size);
        if (cpu.stack32) cpu.esp = (uint)(cpu.esp + size); else cpu.sp = (ushort)(cpu.sp + size);
        return value;
    }

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

    // 2 オペランド IMUL(16/32 ビット): 下位の積を返し、CF=OF=上位への溢れ(規則は Alu.SignedMultiply。通常コアと共有)。
    public static uint MultiplySigned(CPU cpu, int type, uint left, uint right)
    {
        var (product, overflow) = SignedMultiply(type, SignExtend(left, type), SignExtend(right, type));
        cpu.cf = overflow; cpu.of = overflow;
        return (uint)product & Mask(type);
    }

    // 1 オペランド MUL/IMUL: AL*v -> AX、AX*v -> DX:AX、EAX*v -> EDX:EAX。CF=OF=上位が有効か(規則は Alu と共有)。
    public static void Multiply(CPU cpu, int type, uint value, bool signed)
    {
        var accumulator = ReadRegister(cpu, 0, type);
        ulong product;
        bool overflow;
        if (signed)
        {
            (var signedProduct, overflow) = SignedMultiply(type, SignExtend(accumulator, type), SignExtend(value, type));
            product = (ulong)signedProduct;
        }
        else
        {
            product = (ulong)accumulator * value;
            overflow = product >> Bits(type) != 0;
        }
        WriteWide(cpu, type, product);
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
        var size = Bytes(type);
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

    // XLAT: 通常コアと同じ規則(seg:(E)BX + AL。アドレスサイズとセグメントオーバーライドは命令のもの)。コンパイル済みコードは prefixes を int で渡す。
    public static uint XlatAddress(CPU cpu, bool address32, int prefixes) => Instructions.XlatAddress(cpu, address32, (Prefix)prefixes);

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

    // 読んで書き戻す r/m オペランド(memory なら address のメモリ、そうでなければレジスタ reg)。CMPXCHG/XADD/BT 系/SHLD/SHRD。
    // メモリは書き戻す場合、読む前に書き込みまで検査する(Probe。読んだ後の Store がフォルトしないように)。
    static uint ReadPlace(EmuEnvironment env, CPU cpu, bool memory, uint address, int reg, int type, bool writesBack = true)
    {
        if (!memory) return ReadRegister(cpu, reg, type);
        if (writesBack) Probe(env, address, type, readsFirst: true);
        return Load(env, address, type);
    }

    static void WritePlace(EmuEnvironment env, CPU cpu, bool memory, uint address, int reg, int type, uint value)
    {
        if (memory) Store(env, address, type, value);
        else WriteRegister(cpu, reg, type, value);
    }

    // CMPXCHG: acc と r/m を比べて(CMP のフラグ)、等しければ r/m <- reg、違えば acc <- r/m。
    public static void CompareExchange(EmuEnvironment env, CPU cpu, bool memory, uint address, int dstReg, int reg, int type)
    {
        var destination = ReadPlace(env, cpu, memory, address, dstReg, type);
        var accumulator = ReadRegister(cpu, 0, type);
        CalculateAlu(cpu, type, accumulator, destination, 7);
        if (accumulator == destination) WritePlace(env, cpu, memory, address, dstReg, type, ReadRegister(cpu, reg, type));
        else WriteRegister(cpu, 0, type, destination);
    }

    // XADD: reg <- r/m の旧値、r/m <- r/m + reg(ADD のフラグ)。reg を先に書く(通常コアと同じ順: 同じレジスタなら和が残る)。
    public static void ExchangeAdd(EmuEnvironment env, CPU cpu, bool memory, uint address, int dstReg, int reg, int type)
    {
        var source = ReadRegister(cpu, reg, type);
        var destination = ReadPlace(env, cpu, memory, address, dstReg, type);
        var sum = CalculateAlu(cpu, type, destination, source, 0);
        WriteRegister(cpu, reg, type, destination);
        WritePlace(env, cpu, memory, address, dstReg, type, sum);
    }

    // BT 系: r/m の bit 番目を CF へ。BTS/BTR/BTC は書き戻す(BT は書かない)。bitString ならビット番号(符号付き)でワードを選ぶ。
    public static void BitTest(EmuEnvironment env, CPU cpu, bool memory, uint address, int dstReg, uint bit, bool bitString, int op, int type)
    {
        var bits = Bits(type);
        if (memory && bitString) address += (uint)(((int)bit >> (type == 1 ? 4 : 5)) * Bytes(type));
        var mask = 1u << ((int)bit & (bits - 1));
        var value = ReadPlace(env, cpu, memory, address, dstReg, type, writesBack: op != 0);
        cpu.cf = (value & mask) != 0;
        if (op == 0) return;
        WritePlace(env, cpu, memory, address, dstReg, type, op switch { 1 => value | mask, 2 => value & ~mask, _ => value ^ mask });
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
        var destination = ReadPlace(env, cpu, memory, address, dstReg, type);
        var (result, cf, of) = Emu86.Alu.DoubleShift(destination, ReadRegister(cpu, srcReg, type), n, Bits(type), left);
        WritePlace(env, cpu, memory, address, dstReg, type, result);
        cpu.cf = cf; cpu.of = of; cpu.zf = (result & Mask(type)) == 0; cpu.sf = (result & Msb(type)) != 0;
    }

    public static void Divide(CPU cpu, int type, uint divisor, bool signed) => Emu86.Alu.Divide(cpu, type, divisor, signed);

    public static void AdjustDecimal(CPU cpu, int kind, uint immediate) => Emu86.Alu.AdjustDecimal(cpu, kind, immediate);

    /// docs/adr/0003 段階 2 ///////////////////////////////
    public static uint ReadSegment(CPU cpu, int sreg) => DataAccess.ArraySreg[sreg].getter(cpu);

    // 通常コアの State を 1 つ実行する。失敗しない(where を含まない)State だけを渡す。
    // フォルトは CPU を文の前の状態へ戻してから投げ直す(通常コアの実行器の回復と同じ。メモリへの書き込みは戻さないのも同じ)。
    public static void RunState(EmuEnvironment env, CPU cpu, State<Unit> state)
    {
        var checkpoint = env.Translations.Checkpoint;
        cpu.CopyTo(checkpoint);
        try
        {
            var result = state(env, cpu, null);
            if (!ReferenceEquals(result.cpu, cpu)) result.cpu.CopyTo(cpu);
            if (!result.IsSuccess) throw new InvalidOperationException("a State used by the IR failed");
        }
        catch (CpuFaultException)
        {
            checkpoint.CopyTo(cpu);
            throw;
        }
    }

    public static void PortIn(EmuEnvironment env, CPU cpu, int type, uint port) => WriteRegister(cpu, 0, type, EnvInPortN(env, (int)port, Bytes(type)));

    public static void PortOut(EmuEnvironment env, int type, uint port, uint value) => EnvOutPortN(env, (int)port, Bytes(type), value);

    public static void Rdtsc(EmuEnvironment env, CPU cpu) => WriteWide(cpu, 2, env.Tsc);

    public static void Cpuid(CPU cpu) => (cpu.eax, cpu.ebx, cpu.ecx, cpu.edx) = Instructions.CpuidLeaf(cpu.eax);

    // CMPXCHG8B: EDX:EAX と m64 を比べ、等しければ m64 <- ECX:EBX(ZF=1)、違えば EDX:EAX <- m64(ZF=0)。
    // 通常コアと同じく、等しいときだけ書く(実機は常に書き戻す)。書く前に両方の dword の書き込み先を検査する。
    public static void CompareExchange8(EmuEnvironment env, CPU cpu, uint address)
    {
        var low = Load(env, address, 2);
        var high = Load(env, address + 4, 2);
        if (cpu.eax == low && cpu.edx == high)
        {
            Probe(env, address, 2, readsFirst: true);
            Probe(env, address + 4, 2, readsFirst: true);
            Store(env, address, 2, cpu.ebx);
            Store(env, address + 4, 2, cpu.ecx);
            cpu.zf = true;
        }
        else
        {
            cpu.eax = low;
            cpu.edx = high;
            cpu.zf = false;
        }
    }

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
