using static Emu86.CPU;
using static Emu86.Conversions;
using static Emu86.Unit;
using static Emu86.Operands;
using static Emu86.States;
using static Emu86.DataAccess;
using static Emu86.Segments;
using static Emu86.CpuStates;
using static Emu86.Fetch;

namespace Emu86;

// システム命令: 制御/デバッグレジスタの MOV、CPUID、RDTSC、RDMSR/WRMSR、記述子テーブル(SGDT/LGDT/LIDT/SIDT/LLDT/LTR)、VERR/VERW/LAR/LSL、INVLPG、HLT、NOP、FWAIT、キャッシュ命令。
// 保存と読み込みが対になる命令(MOV CR/DR の両方向、RDMSR/WRMSR、SGDT/LGDT、SLDT/LLDT)は、レジスタを Lens にして Move の向きだけを変える。
static partial class Instructions
{
    // 制御レジスタ CRn(書くとページング状態も同期する)、デバッグレジスタ DRn(ハードウェアブレークポイントは未実装で、
    // 値を保持するだけ)、32 ビット汎用レジスタの Lens。
    static Lens<uint> ControlRegister(int reg) => new(GetCrReg(reg), value => SetCrReg(reg, value));

    static Lens<uint> DebugRegister(int reg) => new(GetDataFromEnvCpu((env, _) => env.Dr[reg]), value => Effect((env, _) => env.Dr[reg] = value));

    static Lens<uint> GeneralRegister32(int reg) => new(GetRegData32(reg), value => SetRegData32(reg, value));

    // MOV r32, CRn/DRn (0F 20/21) と MOV CRn/DRn, r32 (0F 22/23): 汎用レジスタと制御/デバッグレジスタの間の Move(レジスタ形式のみ)。
    // 2 バイト目の bit0 が DR、bit1 が向き(1 = 制御/デバッグレジスタへ)。
    static State<Unit> MovSpecial_0F20_23 =>
        from _1 in SetLog("MovSpecial_0F20_23")
        from opecode in Opecodes
        from m in ModRegRm()
        where 3 == m.mod
        let special = (opecode[1] & 0x01) != 0 ? DebugRegister(m.reg) : ControlRegister(m.reg)
        let general = GeneralRegister32(m.rm)
        from _2 in (opecode[1] & 0x02) != 0 ? Move(special, general) : Move(general, special)
        select unit;

    // CPUID (0F A2): 入力 EAX=リーフ。最小限の機能のみ報告し、APIC 等の高度な
    // (未エミュレートの MMIO を要する)経路に SeaBIOS が入らないようにする。
    static State<Unit> Cpuid_0FA2 =>
        from _1 in SetLog("Cpuid_0FA2")
        from leaf in GetRegData32(0) // EAX
        let result = CpuidLeaf(leaf)
        from _2 in Effect(cpu => (cpu.eax, cpu.ebx, cpu.ecx, cpu.edx) = result)
        select unit;

    // CPUID の応答 (EAX, EBX, ECX, EDX)。
    //   リーフ 0: 最大リーフ + ベンダ文字列 "GenuineIntel"(EBX="Genu" EDX="ineI" ECX="ntel")。
    //   リーフ 1: family 6、拡張機能なし。EDX はエミュレートできる機能のみ:
    //             FPU(0x1) PSE(0x8) TSC(0x10) MSR(0x20) PAE(0x40) CX8(0x100) CMOV(0x8000)。
    //             686-pae カーネルはここで PAE を確認して CR4.PAE を設定する。APIC/SEP/FXSR/MMX 等は未実装のため伏せる。
    internal static (uint eax, uint ebx, uint ecx, uint edx) CpuidLeaf(uint leaf) => leaf switch
    {
        0 => (1, 0x756e6547, 0x6c65746e, 0x49656e69),
        1 => (0x00000600, 0, 0, 0x00008179),
        _ => (0, 0, 0, 0),
    };

    // ECX が選ぶ MSR の Lens(実際の機能は持たない汎用ストア。未書き込みの MSR は 0)。
    static Lens<ulong> SelectedMsr =>
        new(GetDataFromEnvCpu((env, cpu) => env.Msrs.GetValueOrDefault(cpu.ecx)), value => Effect((env, cpu) => env.Msrs[cpu.ecx] = value));

    // RDTSC (0F 31): EDX:EAX <- タイムスタンプカウンタ。実行済み命令数を代用する
    // (Runner が毎命令 env.Tsc を更新する)。
    static State<Unit> Rdtsc_0F31 =>
        from _1 in SetLog("Rdtsc_0F31")
        from tsc in GetDataFromEnvCpu((env, _) => env.Tsc)
        from _2 in EdxEax.Write(tsc)
        select unit;

    // RDMSR (0F 32) / WRMSR (0F 30): EDX:EAX と MSR の間の Move。
    static State<Unit> Rdmsr_0F32 =>
        from _1 in SetLog("Rdmsr_0F32")
        from _2 in Move(EdxEax, SelectedMsr)
        select unit;

    static State<Unit> Wrmsr_0F30 =>
        from _1 in SetLog("Wrmsr_0F30")
        from _2 in Move(SelectedMsr, EdxEax)
        select unit;

    // WAIT/FWAIT (0x9B): FPU 例外を同期する。例外は配送しないため NOP。
    static State<Unit> Fwait_9B =>
        from _ in SetLog("Fwait_9B")
        select unit;

    // INVD (0F 08) / WBINVD (0F 09): キャッシュ無効化/書き戻し。エミュレータでは NOP。
    static State<Unit> CacheInvd_0F08_09 =>
        from _ in SetLog("CacheInvd")
        select unit;

    static State<Unit> Nop_90 =>
        from _ in SetLog("Nop_90")
        select unit;

    // 0F 18 (PREFETCHh) と 0F 19-0F 1F (予約 NOP。0F 1F /0 = NOP r/m16/32: アセンブラが整列に使う多バイト NOP
    // 「66 2E 0F 1F 84 00 00 00 00 00」)。ModRM・SIB・変位を読み進めるだけで、メモリには触れずフラグも変えない。
    // Debian の librsvg(lightdm-gtk-greeter から)がこれを実行し、#UD だと SIGILL で greeter が落ちていた。
    static State<Unit> HintNop_0F18_1F =>
        from _1 in SetLog("HintNop_0F18_1F")
        from m in ModRegRm()
        from _2 in GetMemOrRegOffset(m.mod, m.rm)
        select unit;

    // HLT: 印を立てるだけ。待機(次の割り込みまで仮想時間を進める)はランナーが行う。
    static State<Unit> Hlt_F4 =>
        from _1 in SetLog("Hlt_F4")
        from _2 in Effect(cpu => cpu.Halted = true)
        select unit;

    // 番号で引けない CPU のレジスタ(GDTR/IDTR の limit と base、TR/LDTR のセレクタ)をオペランドに。
    static Operand Word(Func<CPU, ushort> get, Action<CPU, ushort> set) =>
        new(GetDataFromCpu(cpu => get(cpu).ToTypeData()), data => Effect(cpu => set(cpu, data.dw)));

    static Operand Dword(Func<CPU, uint> get, Action<CPU, uint> set) =>
        new(GetDataFromCpu(cpu => get(cpu).ToTypeData()), data => Effect(cpu => set(cpu, data.dd)));

    // 記述子テーブルレジスタ(GDTR か IDTR)の limit(16 ビット)と base(32 ビット)。
    static (Operand limit, Operand @base) TableRegister(bool idt) => idt
        ? (Word(cpu => cpu.idt_limit, (cpu, value) => cpu.idt_limit = value), Dword(cpu => cpu.idt_base, (cpu, value) => cpu.idt_base = value))
        : (Word(cpu => cpu.gdt_limit, (cpu, value) => cpu.gdt_limit = value), Dword(cpu => cpu.gdt_base, (cpu, value) => cpu.gdt_base = value));

    // SGDT/SIDT (0F 01 /0, /1) と LGDT/LIDT (0F 01 /2, /3): 記述子テーブルレジスタとメモリ(limit の 2 バイト後ろに base)の間の Move。
    // load なら レジスタ <- メモリ、そうでなければ メモリ <- レジスタ。
    static State<Unit> DescriptorTable(int mod, int rm, bool idt, bool load) =>
        from addr in GetMemOrRegAddr(mod, rm)
        let memory = (limit: Rm(addr, 1), @base: Rm((addr.isMem, addr.addr + 2), 2))
        let register = TableRegister(idt)
        from _1 in load ? Move(register.limit, memory.limit) : Move(memory.limit, register.limit)
        from _2 in load ? Move(register.@base, memory.@base) : Move(memory.@base, register.@base)
        select unit;

    // Group6 (0F 00): SLDT/STR/LLDT/LTR/VERR/VERW。LDT・タスクスイッチは未使用のため、
    // LLDT/LTR はセレクタを保持するだけ。VERR/VERW は記述子を照合して ZF に入れる(Linux は MDS 緩和の `verw` を頻繁に実行する)。
    static State<Unit> Group6_0F00 =>
        from _1 in SetLog("Group6_0F00")
        from m in ModRegRm()
        from _ in Group(
            m.reg,
            (0, StoreSelector(m)),        // SLDT
            (1, StoreSelector(m)),        // STR
            (2, LoadSelector(m)),         // LLDT
            (3, LoadSelector(m)),         // LTR
            (4, Verify(m, write: false)), // VERR
            (5, Verify(m, write: true))   // VERW
        )
        select unit;

    // VERR/VERW: セグメントが CPL/RPL から読める(書ける)なら ZF=1、そうでなければ ZF=0。例外は出さない。
    static State<Unit> Verify((int mod, int reg, int rm) m, bool write) =>
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from sel in GetMemOrRegData16(addr)
        from accessible in GetDataFromEnvCpu((env, cpu) => EnvSegmentAccessible(env, cpu, sel, write))
        from _ in _zf.Set(accessible)
        select unit;

    // LAR/LSL (0F 02/03): r <- アクセス権 / リミット、ZF=1。照合に通らなければ ZF=0 で r は変えない。
    static State<Unit> Lar_0F02 => LarLsl("Lar_0F02", limit: false);

    static State<Unit> Lsl_0F03 => LarLsl("Lsl_0F03", limit: true);

    static State<Unit> LarLsl(string name, bool limit) =>
        from _1 in SetLog(name)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from sel in GetMemOrRegData16(addr)
        from type in OperandType(true)
        from value in GetDataFromEnvCpu((env, cpu) => EnvDescriptorQuery(env, cpu, sel, limit))
        from _2 in _zf.Set(value != null)
        from _3 in When(value != null, SetRegData(m.reg, (value ?? 0).ToTypeData(type)))
        select unit;

    // Group6 の reg が選ぶセレクタのレジスタ: bit0 が立っていれば TR(STR/LTR)、そうでなければ LDTR(SLDT/LLDT)。
    static Operand SystemSelector(int reg) => (reg & 1) != 0
        ? Word(cpu => cpu.tr, (cpu, value) => cpu.tr = value)
        : Word(cpu => cpu.ldtr, (cpu, value) => cpu.ldtr = value);

    // SLDT/STR: 保持しているセレクタを r/m16 へ(レジスタ形式は 32 ビットへゼロ拡張して書く)。
    static State<Unit> StoreSelector((int mod, int reg, int rm) m) =>
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from selector in SystemSelector(m.reg).Read
        from _ in addr.isMem ? SetMemOrRegData(addr, selector) : SetRegData(m.rm, ((uint)selector.dw).ToTypeData())
        select unit;

    // LLDT/LTR: r/m16 のセレクタを保持するだけ(記述子の検証・ビジー化は行わない)。
    static State<Unit> LoadSelector((int mod, int reg, int rm) m) =>
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from _ in Move(SystemSelector(m.reg), Rm(addr, 1))
        select unit;

    static State<Unit> Group7_0F01 =>
        from _1 in SetLog("Group7_0F01")
        from m in ModRegRm()
        from _ in Group(
            m.reg,
            (0, DescriptorTable(m.mod, m.rm, idt: false, load: false)), // SGDT
            (1, DescriptorTable(m.mod, m.rm, idt: true, load: false)),  // SIDT
            (2, DescriptorTable(m.mod, m.rm, idt: false, load: true)),  // LGDT
            (3, DescriptorTable(m.mod, m.rm, idt: true, load: true)),   // LIDT
            (7, Invlpg(m.mod, m.rm))
        )
        select unit;

    // INVLPG (0F 01 /7): 指定ページの TLB エントリを無効化する(簡便のため全フラッシュ)。
    // アドレスはデコードして消費するだけで、変換はしない。
    static State<Unit> Invlpg(int mod, int rm) =>
        from addr in GetMemOrRegOffset(mod, rm)
        from _ in Effect((env, _) => env.Paging.Flush())
        select unit;
}
