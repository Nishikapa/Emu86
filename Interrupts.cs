using static Emu86.CPU;
using static Emu86.States;
using static Emu86.Mmu;
using static Emu86.DataAccess;
using static Emu86.Segments;
using static Emu86.CpuStates;

namespace Emu86;

// 割り込み・例外の配送(リアル/V86/プロテクトモード、スタック切り替え)と IRET。
static public class Interrupts
{
    // INT vector: モードに応じて配送する。
    //   リアルモード      : IVT(物理 vector*4)から CS:IP。FLAGS/CS/IP を 16 ビットで push。
    //   プロテクトモード  : IDT ゲート(idt_base + vector*8)から CS:EIP。
    //                       EFLAGS/CS/EIP を 32 ビットで push(リング遷移なし = ring0 のみ対応)。
    static public State<Unit> Interrupt(int vector) =>
        from cpu0 in GetCpu
        from _ in cpu0.pe ? InterruptProtected(vector, hasError: false, 0) : InterruptReal(vector)
        select Unit.unit;

    // V86 モードの INT n: IOPL<3 なら #GP(0)。IOPL=3 でも IDT ゲートの DPL が 3 未満なら
    // #GP(vector*8+2) になる(NT はこれを KiTrap0D で受けて IVT 経由の反射をソフトウェアで行う)。
    static public State<Unit> InterruptFromV86(int vector) =>
        // DPL は上位 dword にある(飛び先は見ないので下位は読まない)。
        from gate in GetDataFromEnvCpu((env, cpu) => new Gate(Low: 0, High: EnvGetMemoryData32(env, cpu.idt_base + (uint)vector * 8 + 4)))
        from cpu in GetCpu
        let iopl = (cpu.eflags >> 12) & 3
        from _ in iopl < 3 ? GeneralProtection(0)
                : gate.Dpl < 3 ? GeneralProtection(((uint)vector << 3) | 2)
                : Interrupt(vector)
        select Unit.unit;

    // #GP を起こす。フォルトなので命令は完了せず、InstructionExecutor が命令開始時の CPU へ戻し、
    // ランナーがそこから Raise で配送する(#PF と同じ経路)。
    static public State<Unit> GeneralProtection(uint errorCode) =>
        (env, cpu, ope) => throw new GeneralProtectionException(errorCode);

    // フォルトの配送(命令開始時の状態から)。#PF は CR2 にフォルトアドレスを入れ、
    // エラーコードを持つ例外はそれを push する。
    static public State<Unit> Raise(Fault fault) =>
        from _1 in fault.Linear is uint linear ? _cr2.Set(linear) : Pass
        from _2 in InterruptProtected(fault.Vector, hasError: fault.ErrorCode is not null, fault.ErrorCode ?? 0)
        select Unit.unit;

    // リアルモードの INT 19h(SeaBIOS の POST 完了後のブート)で VGA BIOS スタブを導入する
    // (SeaBIOS はレガシー ROM 領域を走査しないため、この時点で IVT 10h を差し替える)。同じ時点で、SeaBIOS が作った
    // ACPI テーブルから MADT を外す(APIC は無いので、OS に 8259 PIC の機械として見せる)。
    static State<Unit> InterruptReal(int vector) =>
        from _stub in Effect((env, _) => { if (vector == 0x19) { VgaBiosStub.Install(env); AcpiTables.HideMadt(env); } })
        from fl in GetDataFromCpu(cpu => (ushort)cpu.eflags)
        from _1 in Push16(fl)
        from cs in GetSRegData(1)
        from _2 in Push16(cs)
        from ip in GetDataFromCpu(cpu => cpu.ip)
        from _3 in Push16(ip)
        from _if in SetCpu((_jf, false), (_tf, false))
        from newip in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData16(env, (uint)(vector * 4)))
        from newcs in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData16(env, (uint)(vector * 4 + 2)))
        from _4 in _ip.Set(newip)
        from _5 in LoadSReg(1, newcs)
        select Unit.unit;

    // IDT ゲート経由の配送。idt_base は線形アドレスだが、この関数はページング変換の
    // 起点(#PF ハンドラ自身)になりうるため、IDT は恒等マップ域にある前提で読む。
    static State<Unit> InterruptProtected(int vector, bool hasError, uint errorCode) =>
        from _log in Effect((env, cpu) =>
        {
            if (env.Diagnostics is { InterruptLog: not null } diagnostics && cpu.pg)
                diagnostics.LogInterrupt($"vec={vector:x2} err={(hasError ? errorCode.ToString("x") : "-")} at cs:eip={cpu.cs:x4}:{cpu.eip:x8} esp={cpu.esp:x8}",
                    fromV86: cpu.vm, env.Tsc, cpu.cr3);
        })
        from gate in GetDataFromEnvCpu((env, cpu) =>
            new Gate(EnvGetMemoryData32(env, cpu.idt_base + (uint)vector * 8), EnvGetMemoryData32(env, cpu.idt_base + (uint)vector * 8 + 4)))
        // 特権レベルが上がる(例: ring3 のユーザーモードから ring0 へ)ときは TSS の SS0:ESP0 へ
        // スタックを切り替え、旧 SS/ESP を先に積む(IRET で元へ戻る)。
        // V86 モードからの配送は VM を先に落として(以降のセグメントロードを記述子経由にする)、
        // 積む EFLAGS には VM=1 の元の値を使う。
        from cpu0 in GetCpu
        let fromVm = cpu0.vm
        let cpl = cpu0.cpl // VM を落とす前に評価する(V86 なら 3)
        let fl = cpu0.eflags
        from _vm in When(fromVm, _eflags.Modify(flags => flags & ~0x20000u))
        from _sw in SwitchToInnerStack(gate.Selector, cpl, fromVm)
        from _1 in Push(fl.ToTypeData())
        from cs in GetSRegData(1)
        from _2 in Push(((uint)cs).ToTypeData())
        from eip0 in GetDataFromCpu(cpu => cpu.eip)
        from _3 in Push(eip0.ToTypeData())
        // 一部の例外(#PF 等)は EIP の後ろにエラーコードを push する。
        from _e in When(hasError, Push(errorCode.ToTypeData()))
        // 割り込みゲートは IF をクリアする。トラップゲートは保つ。
        from _if in When(gate.IsInterruptGate, _jf.Set(false))
        from _t in _tf.Set(false)
        from _4 in _eip.Set(gate.Offset)
        from _5 in LoadSReg(1, gate.Selector)
        select Unit.unit;

    // 割り込み/例外配送での特権レベル変化: CPL より目標 CS の RPL が小さければ
    // TR が指す TSS から SS0/ESP0 を読んでスタックを切り替え、旧 SS と ESP を積む。
    // V86 モードから(fromVm)は、さらにその前に GS/FS/DS/ES を積み、これらを null にする
    // (ハンドラ側は KiTrap の冒頭でカーネル用セレクタをロードし直す)。
    static State<Unit> SwitchToInnerStack(ushort targetCs, int cpl, bool fromVm) =>
        When(cpl > (targetCs & 3),
            from ctx in GetDataFromEnvCpu((env, cpu) =>
                {
                    var tssBase = EnvReadDescriptor(env, cpu, cpu.tr).Base;
                    var esp0 = EnvGetMemoryData32(env, tssBase + 4);
                    var ss0 = (ushort)EnvGetMemoryData32(env, tssBase + 8);
                    return (oldSs: cpu.ss, oldEsp: cpu.esp, ss0, esp0, cpu.es, cpu.ds, cpu.fs, cpu.gs);
                })
            from _1 in LoadSReg(2, ctx.ss0)
            from _2 in _esp.Set(ctx.esp0)
            from _v in When(fromVm,
                from a in Push(((uint)ctx.gs).ToTypeData())
                from b in Push(((uint)ctx.fs).ToTypeData())
                from c in Push(((uint)ctx.ds).ToTypeData())
                from d in Push(((uint)ctx.es).ToTypeData())
                select Unit.unit)
            from _3 in Push(((uint)ctx.oldSs).ToTypeData())
            from _4 in Push(ctx.oldEsp.ToTypeData())
            from _n in When(fromVm, new[] { 0, 3, 4, 5 }.Select(NullSelector).Sequence().Ignore()) // ES DS FS GS
            select Unit.unit);

    // セグメントレジスタにヌルセレクタを入れる(記述子は読まない)。
    static State<Unit> NullSelector(int reg) => ArraySreg[reg].Set((ushort)0);

    // 外側の特権レベル(例: ring0 → ring3)へ戻るときは、さらに ESP と SS を pop してスタックを切り替える。
    // V86 モード内の far 復帰はリアルモード同様で、セレクタの下位ビットは特権を意味しない。
    static public State<Unit> ReturnToOuterStack(ushort targetCs) =>
        from cpu0 in GetCpu
        from _ in When(!cpu0.vm && (targetCs & 3) > (cpu0.cs & 3),
            from sp in Pop(2)
            from ss in Pop(2)
            from _1 in LoadSReg(2, (ushort)ss.dd)
            from _2 in _esp.Set(sp.dd)
            select Unit.unit)
        select Unit.unit;

    // IRET: モードに応じて復帰する(リアル/16bit と プロテクト/32bit)。
    static public State<Unit> Iret =>
        from cpu0 in GetCpu
        from _ in cpu0.pe && cpu0.code32 ? Iret32 : Iret16
        select Unit.unit;

    // 16 ビット IRET。V86 モード(IOPL=3 で実行可)では IOPL を変更できない(VM は上位ワードなので保たれる)。
    static State<Unit> Iret16 =>
        from ip in Pop16
        from _1 in _ip.Set(ip)
        from cs in Pop16
        from _2 in LoadSReg(1, cs)
        from fl in Pop16
        from vm in GetDataFromCpu(cpu => cpu.vm)
        let keep = vm ? 0x3000u : 0u
        from _3 in _eflags.Modify(flags => (flags & (0xFFFF0000 | keep)) | (fl & ~keep))
        select Unit.unit;

    static State<Unit> Iret32 =>
        from fault in GetDataFromEnvCpu(IretTargetFault)
        from _ in fault is uint selector
            ? GeneralProtection(selector)
            : (from eip in Pop(2)
               from cs in Pop(2)
               from fl in Pop(2)
               from cpu0 in GetCpu
               from _v in (fl.dd & 0x20000) != 0 && cpu0.cpl == 0
                   ? IretToV86(eip.dd, (ushort)cs.dd, fl.dd)
                   : (from _0 in ReturnToOuterStack((ushort)cs.dd) // 旧 SS で ESP/SS を pop してから CS をロードする
                      from _1 in _eip.Set(eip.dd)
                      from _2 in LoadSReg(1, (ushort)cs.dd)
                      from _3 in _eflags.Set(fl.dd)
                      select Unit.unit)
               select Unit.unit)
        select Unit.unit;

    // 32 ビット IRET の復帰先 CS をスタックを動かさずに検査し、違反なら #GP のエラーコード(セレクタ)を返す。
    // 非適合コードセグメントは DPL=RPL、適合コードは DPL<=RPL が必要で、RPL<CPL も不可。V86 への復帰は対象外。
    // NT はこれを意図的に使う: V86 の BIOS 呼び出しから戻る高速 BOP 経路は CS=000Bh(ring0 コードに RPL 3)の
    // IRET で #GP を起こし、KiTrap0D がその枠を認識して Ki386BiosCallReturnAddress へ ring0 のまま飛ぶ。
    static uint? IretTargetFault(EmuEnvironment env, CPU cpu)
    {
        if (!cpu.pe || cpu.vm) return null;
        var stack = cpu.ss_base + (cpu.stack32 ? cpu.esp : cpu.sp);
        var cs = (ushort)EnvGetMemoryData32(env, stack + 4);
        var flags = EnvGetMemoryData32(env, stack + 8);
        if ((flags & 0x20000) != 0 && cpu.cpl == 0) return null;
        if ((cs & 0xFFFC) == 0) return 0;
        // 種別と DPL は上位 dword にある(基底とリミットは見ないので下位は読まない)。
        var target = new Descriptor(Low: 0, High: EnvGetMemoryData32(env, cpu.gdt_base + (uint)(cs & 0xFFF8) + 4));
        var rpl = cs & 3;
        var allowed = target.IsCode && rpl >= cpu.cpl && (target.Conforming ? target.Dpl <= rpl : target.Dpl == rpl);
        return allowed ? null : (uint)(cs & 0xFFFC);
    }

    // ring0 からの IRET で EFLAGS.VM=1 を積んでいた場合は V86 モードへ入る: さらに ESP, SS, ES, DS, FS, GS を
    // pop し、全セグメントを セレクタ*16 基底の 16 ビットセグメントとしてロードする(NT の HAL は
    // この経路で BIOS(INT 10h 等)を V86 で呼ぶ)。
    static State<Unit> IretToV86(uint eip, ushort cs, uint flags) =>
        from sp in Pop(2)
        from ss in Pop(2)
        from es in Pop(2)
        from ds in Pop(2)
        from fs in Pop(2)
        from gs in Pop(2)
        from _f in _eflags.Set(flags) // 以降のセグメントロードは V86 規則
        from _1 in _eip.Set(eip)
        from _2 in LoadSReg(1, cs)
        from _3 in LoadSReg(2, (ushort)ss.dd)
        from _4 in _esp.Set(sp.dd)
        from _5 in LoadSReg(0, (ushort)es.dd)
        from _6 in LoadSReg(3, (ushort)ds.dd)
        from _7 in LoadSReg(4, (ushort)fs.dd)
        from _8 in LoadSReg(5, (ushort)gs.dd)
        select Unit.unit;
}
