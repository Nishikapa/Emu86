namespace Emu86;

static public partial class Operands
{
    // 32ビット汎用レジスタを番号で読む。
    static internal uint ReadRegister32(CPU cpu, int register) => register switch
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
    static internal uint EnvGetIndexRegData32(CPU cpu, int reg) =>
        reg == 4 ? 0 : ReadRegister32(cpu, reg);

    // 8/16 ビットの汎用レジスタを番号で読み書きする(IR の解釈器が使う。8 ビットは AL CL DL BL AH CH DH BH の順)。
    static internal byte ReadRegister8(CPU c, int r) => r switch
    {
        0 => c.al, 1 => c.cl, 2 => c.dl, 3 => c.bl,
        4 => c.ah, 5 => c.ch, 6 => c.dh, _ => c.bh,
    };

    static internal ushort ReadRegister16(CPU c, int r) => r switch
    {
        0 => c.ax, 1 => c.cx, 2 => c.dx, 3 => c.bx,
        4 => c.sp, 5 => c.bp, 6 => c.si, _ => c.di,
    };

    static internal void WriteRegister8(CPU c, int r, byte v)
    {
        switch (r)
        {
            case 0: c.al = v; break;
            case 1: c.cl = v; break;
            case 2: c.dl = v; break;
            case 3: c.bl = v; break;
            case 4: c.ah = v; break;
            case 5: c.ch = v; break;
            case 6: c.dh = v; break;
            default: c.bh = v; break;
        }
    }

    static internal void WriteRegister16(CPU c, int r, ushort v)
    {
        switch (r)
        {
            case 0: c.ax = v; break;
            case 1: c.cx = v; break;
            case 2: c.dx = v; break;
            case 3: c.bx = v; break;
            case 4: c.sp = v; break;
            case 5: c.bp = v; break;
            case 6: c.si = v; break;
            default: c.di = v; break;
        }
    }

    static internal void WriteRegister32(CPU c, int r, uint v)
    {
        switch (r)
        {
            case 0: c.eax = v; break;
            case 1: c.ecx = v; break;
            case 2: c.edx = v; break;
            case 3: c.ebx = v; break;
            case 4: c.esp = v; break;
            case 5: c.ebp = v; break;
            case 6: c.esi = v; break;
            default: c.edi = v; break;
        }
    }
}
