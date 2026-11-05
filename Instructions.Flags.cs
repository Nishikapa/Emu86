using static Emu86.CPU;
using static Emu86.Conversions;
using static Emu86.Unit;
using static Emu86.States;
using static Emu86.CpuStates;
using static Emu86.Alu;

namespace Emu86;

// フラグ操作命令: CLC/STC/CMC、CLI/STI、CLD/STD、SAHF/LAHF。
static partial class Instructions
{
    // フラグを 1 つ立てるか降ろすだけの命令: CLC/STC(CF)、CLI/STI(割り込み許可フラグ IF。この実装では jf)、CLD/STD(DF)。
    static State<Unit> SetFlag(string name, Accessor<CPU, bool> flag, bool value) =>
        from _1 in SetLog(name)
        from _2 in flag.Set(value)
        select unit;

    static State<Unit> Clc_F8 => SetFlag("Clc_F8", _cf, false);

    static State<Unit> Stc_F9 => SetFlag("Stc_F9", _cf, true);

    static State<Unit> Cli_FA => SetFlag("Cli_FA", _jf, false);

    static State<Unit> Sti_FB => SetFlag("Sti_FB", _jf, true);

    static State<Unit> Cld_FC => SetFlag("Cld_FC", _df, false);

    static State<Unit> Std_FD => SetFlag("Std_FD", _df, true);

    // SAHF (0x9E): AH の下位8bit を FLAGS の下位8bit(SF/ZF/AF/PF/CF)へ転送する。
    static State<Unit> Sahf_9E =>
        from _1 in SetLog("Sahf_9E")
        from _2 in Effect(Sahf)
        select unit;

    // LAHF (0x9F): FLAGS の下位8bit を AH へ転送する。
    static State<Unit> Lahf_9F =>
        from _1 in SetLog("Lahf_9F")
        from flags in _eflags.Get()
        from _2 in _ah.Set((byte)flags)
        select unit;

    static State<Unit> Cmc_F5 =>
        from _1 in SetLog("Cmc_F5")
        from _2 in _cf.Modify(cf => !cf)
        select unit;
}
