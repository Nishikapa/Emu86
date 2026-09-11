namespace Emu86;

static public partial class Ext
{
    // ModRM 16ビットアドレッシング。実効オフセット・セグメントベース・消費した disp バイト数を返す。
    static private (bool isMem, uint offset, uint segBase, int inc) EnvGetMemOrRegAddr16_(EmuEnvironment env, CPU cpu, int mod, int rm, uint instructionAddress)
    {
        if (mod == 3)
            return (false, (uint)rm, 0, 0);

        var (segment_base, ss_base) = EnvSegBases(cpu);

        if (mod == 0 && rm == 6) // [d16]
            return (true, EnvGetMemoryData16(env, instructionAddress), segment_base, 2);

        // rm ごとのレジスタ組み合わせ(BP を含む形は既定セグメントが SS)
        var regsum = rm switch
        {
            0 => (uint)(cpu.bx + cpu.si),
            1 => (uint)(cpu.bx + cpu.di),
            2 => (uint)(cpu.bp + cpu.si),
            3 => (uint)(cpu.bp + cpu.di),
            4 => cpu.si,
            5 => cpu.di,
            6 => cpu.bp,
            _ => cpu.bx
        };
        var segBase = rm is 2 or 3 or 6 ? ss_base : segment_base;

        return mod switch
        {
            0 => (true, regsum, segBase, 0),
            1 => (true, (uint)(regsum + (sbyte)EnvGetMemoryData8(env, instructionAddress)), segBase, 1),
            _ => (true, regsum + EnvGetMemoryData16(env, instructionAddress), segBase, 2),
        };
    }
    // ModRM 32ビットアドレッシング(SIB 対応)。実効オフセット・セグメントベース・消費 disp バイト数を返す。
    static private (bool isMem, uint offset, uint segBase, int inc) EnvGetMemOrRegAddr32_(EmuEnvironment env, CPU cpu, int mod, int rm, uint instructionAddress)
    {
        if (mod == 3)
            return (false, (uint)rm, 0, 0);

        var (segment_base, ss_base) = EnvSegBases(cpu);

        // SIB バイト(rm=4)。base=5 かつ mod=0 は「ベースなし、disp32 が続く」特殊ケース。
        // ベースが ESP/EBP のときは既定セグメントが SS になる。
        if (rm == 4)
        {
            var sib = EnvGetMemoryData8(env, instructionAddress);
            var scaled = (uint)((1 << ((sib >> 6) & 3)) * EnvGetIndexRegData32(cpu, (sib >> 3) & 7));
            var basef = sib & 7;
            var sb = basef is 4 or 5 ? ss_base : segment_base;
            return (mod, basef) switch
            {
                (0, 5) => (true, scaled + EnvGetMemoryData32(env, instructionAddress + 1), segment_base, 5),
                (0, _) => (true, scaled + ReadRegister32(cpu, basef), sb, 1),
                (1, _) => (true, (uint)(scaled + ReadRegister32(cpu, basef) + (sbyte)EnvGetMemoryData8(env, instructionAddress + 1)), sb, 2),
                _ => (true, scaled + ReadRegister32(cpu, basef) + EnvGetMemoryData32(env, instructionAddress + 1), sb, 5),
            };
        }

        if (mod == 0 && rm == 5) // [d32]
            return (true, EnvGetMemoryData32(env, instructionAddress), segment_base, 4);

        // ベースレジスタ(EBP ベースは SS)
        var segBase = rm == 5 ? ss_base : segment_base;
        var reg = ReadRegister32(cpu, rm);
        return mod switch
        {
            0 => (true, reg, segBase, 0),
            1 => (true, (uint)(reg + (sbyte)EnvGetMemoryData8(env, instructionAddress)), segBase, 1),
            _ => (true, reg + EnvGetMemoryData32(env, instructionAddress), segBase, 4),
        };
    }

    static private (bool isMem, uint offset, uint segBase, int inc) EnvGetMemOrRegAddr_(EmuEnvironment env, CPU cpu, int mod, int rm) =>
        // 32ビットコードではデフォルトが32ビットModRMになり、address_size_prefix(0x67)で反転する
        (cpu.code32 != cpu.address_size_prefix) ?
        EnvGetMemOrRegAddr32_(env, cpu, mod, rm, GetCodeAddr(cpu).addr) :
        EnvGetMemOrRegAddr16_(env, cpu, mod, rm, GetCodeAddr(cpu).addr);

    // 物理アドレス(セグメントベース + 実効オフセット)を返す通常版。
    static public State<MemAddr> GetMemOrRegAddr(int mod, int rm) =>
        (env, cpu, opcodes) =>
        {
            var data = EnvGetMemOrRegAddr_(env, cpu, mod, rm);
            cpu.eip += (uint)data.inc;
            return (true, (data.isMem, data.offset + data.segBase), cpu, string.Empty);
        };

    // 実効オフセットのみを返す版(LEA 用: セグメントベースを加算しない)。
    static public State<MemAddr> GetMemOrRegOffset(int mod, int rm) =>
        (env, cpu, opcodes) =>
        {
            var data = EnvGetMemOrRegAddr_(env, cpu, mod, rm);
            cpu.eip += (uint)data.inc;
            return (true, (data.isMem, data.offset), cpu, string.Empty);
        };
}
