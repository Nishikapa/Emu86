using static Emu86.DataAccess;

namespace Emu86;

static public partial class Ext
{
    // ModRM の残り(SIB・変位)を現在の (E)IP から読み、実効オフセット・セグメントベース・消費バイト数を返す。
    // 構造の読み取り(ParseModRm)と実効アドレスの計算(EffectiveAddress)は高速コアと共有する。
    static private (bool isMem, uint offset, uint segBase, int inc) EnvGetMemOrRegAddr_(EmuEnvironment env, CPU cpu, int mod, int rm)
    {
        // 32ビットコードではデフォルトが32ビットModRMになり、address_size_prefix(0x67)で反転する
        var a32 = cpu.code32 != cpu.address_size_prefix;
        var modrm = ParseModRm(CodeSource.At(env, cpu), 0, (byte)((mod << 6) | rm), a32);
        if (modrm.Mod == 3)
            return (false, modrm.Rm, 0, 0);
        var (segment_base, ss_base) = EnvSegBases(cpu);
        var (isMem, offset, segBase) = EffectiveAddress(cpu, modrm, a32, segment_base, ss_base);
        return (isMem, offset, segBase, modrm.Length - 1);
    }

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
