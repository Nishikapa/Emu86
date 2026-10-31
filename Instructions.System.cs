using static Emu86.CPU;
using static Emu86.Conversions;
using static Emu86.Unit;
using static Emu86.States;
using static Emu86.DataAccess;
using static Emu86.Segments;
using static Emu86.CpuStates;
using static Emu86.Fetch;

namespace Emu86;

// システム命令: 制御/デバッグレジスタの MOV、CPUID、RDTSC、RDMSR/WRMSR、記述子テーブル(SGDT/LGDT/LIDT/SIDT/LLDT/LTR)、VERR/VERW/LAR/LSL、INVLPG、HLT、NOP、FWAIT、キャッシュ命令。
static partial class Instructions
{
    static State<Unit> Mov_0F20 =>
        from _1 in SetLog("Mov_0F20")
        from m in ModRegRm()
        where 3 == m.mod
        from data in GetCrReg(m.reg)
        from _3 in SetRegData32(m.rm, data)
        select unit;

    // MOV r32, DRx (0F 21) / MOV DRx, r32 (0F 23): デバッグレジスタ。
    // ハードウェアブレークポイントは未実装のため、値を保持するだけの汎用ストア。
    static State<Unit> Mov_0F21 =>
        from _1 in SetLog("Mov_0F21")
        from m in ModRegRm()
        where 3 == m.mod
        from data in GetDataFromEnvCpu((env, _) => env.Dr[m.reg])
        from _3 in SetRegData32(m.rm, data)
        select unit;

    static State<Unit> Mov_0F23 =>
        from _1 in SetLog("Mov_0F23")
        from m in ModRegRm()
        where 3 == m.mod
        from data in GetRegData32(m.rm)
        from _3 in Effect((env, _) => env.Dr[m.reg] = data)
        select unit;

    static State<Unit> Mov_0F22 =>
        from _1 in SetLog("Mov_0F22")
        from m in ModRegRm()
        where 3 == m.mod
        from data in GetRegData32(m.rm)
        from _3 in SetCrReg(m.reg, data)
        select unit;

    // CPUID (0F A2): 入力 EAX=リーフ。最小限の機能のみ報告し、APIC 等の高度な
    // (未エミュレートの MMIO を要する)経路に SeaBIOS が入らないようにする。
    static State<Unit> Cpuid_0FA2 =>
        from _1 in SetLog("Cpuid_0FA2")
        from leaf in GetRegData32(0) // EAX
        let result = CpuidLeaf(leaf)
        from _2 in SetCpu(cpu => { (cpu.eax, cpu.ebx, cpu.ecx, cpu.edx) = result; return cpu; })
        select unit;

    // CPUID の応答 (EAX, EBX, ECX, EDX)。
    //   リーフ 0: 最大リーフ + ベンダ文字列 "GenuineIntel"(EBX="Genu" EDX="ineI" ECX="ntel")。
    //   リーフ 1: family 6、拡張機能なし。EDX はエミュレートできる機能のみ:
    //             FPU(0x1) PSE(0x8) TSC(0x10) MSR(0x20) PAE(0x40) CX8(0x100) CMOV(0x8000)。
    //             686-pae カーネルはここで PAE を確認して CR4.PAE を設定する。APIC/SEP/FXSR/MMX 等は未実装のため伏せる。
    static (uint eax, uint ebx, uint ecx, uint edx) CpuidLeaf(uint leaf) => leaf switch
    {
        0 => (1, 0x756e6547, 0x6c65746e, 0x49656e69),
        1 => (0x00000600, 0, 0, 0x00008179),
        _ => (0, 0, 0, 0),
    };

    // RDTSC (0F 31): EDX:EAX <- タイムスタンプカウンタ。実行済み命令数を代用する
    // (Runner が毎命令 env.Tsc を更新する)。
    static State<Unit> Rdtsc_0F31 =>
        from _1 in SetLog("Rdtsc_0F31")
        from tsc in GetDataFromEnvCpu((env, _) => env.Tsc)
        from _2 in SetWide(2, tsc)
        select unit;

    // RDMSR (0F 32) / WRMSR (0F 30): ECX で選択した MSR を EDX:EAX で読み書きする。
    // 実際の機能は持たない汎用ストア(未書き込みの MSR は 0 を返す)。
    static State<Unit> Rdmsr_0F32 =>
        from _1 in SetLog("Rdmsr_0F32")
        from value in GetDataFromEnvCpu((env, cpu) => env.Msrs.GetValueOrDefault(cpu.ecx))
        from _2 in SetWide(2, value)
        select unit;

    static State<Unit> Wrmsr_0F30 =>
        from _1 in SetLog("Wrmsr_0F30")
        from _2 in Effect((env, cpu) => env.Msrs[cpu.ecx] = ((ulong)cpu.edx << 32) | cpu.eax)
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

    // HLT: 印を立てるだけ。待機(次の割り込みまで仮想時間を進める)はランナーが行う。
    static State<Unit> Hlt_F4 =>
        from _1 in SetLog("Hlt_F4")
        from _2 in SetCpu(cpu => { cpu.Halted = true; return cpu; })
        select unit;

    // SGDT/SIDT (0F 01 /0, /1): limit(16bit) + base(32bit) をメモリへ書き出す。
    static State<Unit> Sgdt(int mod, int rm, bool idt) =>
        from addr in GetMemOrRegAddr(mod, rm)
        from cpu in GetCpu
        from _1 in SetMemOrRegData(addr, (idt ? cpu.idt_limit : cpu.gdt_limit).ToTypeData())
        from _2 in SetMemOrRegData((addr.isMem, addr.addr + 2), (idt ? cpu.idt_base : cpu.gdt_base).ToTypeData())
        select unit;

    // LGDT/LIDT (0F 01 /2, /3): メモリから limit(16bit) + base(32bit) を読み込む。
    static State<Unit> Lgdt(int mod, int rm, bool idt) =>
        from addr in GetMemOrRegAddr(mod, rm)
        from dw in GetMemOrRegData16(addr)
        from dd in GetMemOrRegData32((addr.isMem, addr.addr + 2))
        from _ in SetCpu(cpu =>
        {
            if (idt) { cpu.idt_base = dd; cpu.idt_limit = dw; }
            else { cpu.gdt_base = dd; cpu.gdt_limit = dw; }
            return cpu;
        })
        select unit;

    // Group6 (0F 00): SLDT/STR/LLDT/LTR/VERR/VERW。LDT・タスクスイッチは未使用のため、
    // LLDT/LTR はセレクタを保持するだけ。VERR/VERW は記述子を照合して ZF に入れる(Linux は MDS 緩和の `verw` を頻繁に実行する)。
    static State<Unit> Group6_0F00 =>
        from _1 in SetLog("Group6_0F00")
        from m in ModRegRm()
        from _ in Group(
            m.reg,
            (0, SldtStr(m)),            // SLDT
            (1, SldtStr(m)),            // STR
            (2, LldtLtr(m)),            // LLDT
            (3, LldtLtr(m)),            // LTR
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
        from cpu in GetCpu
        from value in GetDataFromEnvCpu((env, c) => EnvDescriptorQuery(env, c, sel, limit))
        from _2 in _zf.Set(value != null)
        from _3 in When(value != null, SetRegData(m.reg, cpu.code32 != cpu.operand_size_prefix ? (value ?? 0).ToTypeData() : ((ushort)(value ?? 0)).ToTypeData()))
        select unit;

    // SLDT/STR: 保持しているセレクタを返す(レジスタ形式は32bit幅のときゼロ拡張)。
    static State<Unit> SldtStr((int mod, int reg, int rm) m) =>
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from cpu in GetCpu
        let sel = m.reg == 1 ? cpu.tr : cpu.ldtr
        from _ in addr.isMem ? SetMemOrRegData(addr, sel.ToTypeData()) : SetRegData(m.rm, ((uint)sel).ToTypeData())
        select unit;

    // LLDT/LTR: セレクタを保持するだけ(記述子の検証・ビジー化は行わない)。
    static State<Unit> LldtLtr((int mod, int reg, int rm) m) =>
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from sel in GetMemOrRegData16(addr)
        from _ in SetCpu(cpu => { if (m.reg == 3) cpu.tr = sel; else cpu.ldtr = sel; return cpu; })
        select unit;

    static State<Unit> Group7_0F01 =>
        from _1 in SetLog("Group7_0F01")
        from m in ModRegRm()
        from _ in Group(
            m.reg,
            (0, Sgdt(m.mod, m.rm, idt: false)),
            (1, Sgdt(m.mod, m.rm, idt: true)),
            (2, Lgdt(m.mod, m.rm, idt: false)),
            (3, Lgdt(m.mod, m.rm, idt: true)),
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
