namespace Emu86;

static public partial class Ext
{
    // 32ビット汎用レジスタを番号で読む。
    static private uint ReadRegister32(CPU cpu, int register) => register switch
    {
        0 => cpu.eax,
        1 => cpu.ecx,
        2 => cpu.edx,
        3 => cpu.ebx,
        4 => cpu.esp,
        5 => cpu.ebp,
        6 => cpu.esi,
        _ => cpu.edi
    };

    // SIB の index(4=なし)。
    static private uint EnvGetIndexRegData32(CPU cpu, int reg) =>
        reg == 4 ? 0 : ReadRegister32(cpu, reg);
}
