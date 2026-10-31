using static Emu86.CPU;
using static Emu86.Conversions;
using static Emu86.Unit;
using static Emu86.States;
using static Emu86.CpuStates;

namespace Emu86;

// フラグ操作命令: CLI/STI、CLD/STD、CLC/STC/CMC、SAHF/LAHF。
static partial class Instructions
{
    // CLI/STI: 割り込み許可フラグ(IF、この実装では jf)を操作する。
    static State<Unit> Cli_FA =>
        from _1 in SetLog("Cli_FA")
        from _2 in _jf.Set(false)
        select unit;

    static State<Unit> Sti_FB =>
        from _1 in SetLog("Sti_FB")
        from _2 in _jf.Set(true)
        select unit;

    static State<Unit> Cld_FC =>
        from _1 in SetLog("Cld_FC")
        from _2 in _df.Set(false)
        select unit;

    static State<Unit> Std_FD =>
        from _1 in SetLog("Std_FD")
        from _2 in _df.Set(true)
        select unit;

    // SAHF (0x9E): AH の下位8bit を FLAGS の下位8bit(SF/ZF/AF/PF/CF)へ転送する。
    static State<Unit> Sahf_9E =>
        from _1 in SetLog("Sahf_9E")
        from _2 in SetCpu(cpu =>
        {
            // FLAGS 下位8bit のうち bit1 は常に 1。AH をそのまま反映する。
            cpu.eflags = (cpu.eflags & 0xFFFFFF00) | cpu.ah | 0x02u;
            return cpu;
        })
        select unit;

    // LAHF (0x9F): FLAGS の下位8bit を AH へ転送する。
    static State<Unit> Lahf_9F =>
        from _1 in SetLog("Lahf_9F")
        from _2 in SetCpu(cpu => { cpu.ah = (byte)(cpu.eflags & 0xFF); return cpu; })
        select unit;

    static State<Unit> Clc_F8 =>
        from _1 in SetLog("Clc_F8")
        from _2 in _cf.Set(false)
        select unit;

    static State<Unit> Stc_F9 =>
        from _1 in SetLog("Stc_F9")
        from _2 in _cf.Set(true)
        select unit;

    static State<Unit> Cmc_F5 =>
        from _1 in SetLog("Cmc_F5")
        from _2 in _cf.Modify(cf => !cf)
        select unit;
}
