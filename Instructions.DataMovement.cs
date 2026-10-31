using static Emu86.CPU;
using static Emu86.Conversions;
using static Emu86.Unit;
using static Emu86.Operands;
using static Emu86.States;
using static Emu86.DataAccess;
using static Emu86.Segments;
using static Emu86.CpuStates;
using static Emu86.Fetch;
using static Emu86.Decoder;
using static Emu86.AluStates;

namespace Emu86;

// データ転送命令: MOV の各形、LEA、XCHG、CMOVcc、SETcc、MOVZX/MOVSX、BSWAP、far ポインタのロード、XLAT。
static partial class Instructions
{
    static State<Unit> Lea_8D =>
        from _1 in SetLog("Lea_8D")
        from m in ModRegRm()
        // LEA はメモリを読まず、実効オフセット(セグメントベース非加算)を reg へ書く。
        from addr in GetMemOrRegOffset(m.mod, m.rm)
        from type in OperandType(true)
        from _2 in SetRegData(m.reg, addr.addr.ToTypeData(type))
        select unit;

    // MOV r/m, imm (0xC6/0xC7)
    static State<Unit> Mov_C6_C7 =>
        from _1 in SetLog("Mov_C6_C7")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from imm in GetMemoryDataIp_(type)
        from _2 in SetMemOrRegData(addr, imm)
        select unit;

    // moffs のオフセットを実効アドレスサイズで読む(32ビットコードでは32ビット、0x67で反転)。
    static State<uint> MoffsOffset =>
        from a32 in GetDataFromCpu(cpu => cpu.code32 != cpu.address_size_prefix)
        from off in a32 ? GetMemoryDataIp32 : GetMemoryDataIp16.Select(dw => (uint)dw)
        select off;

    // MOV AL/AX/EAX, [moffs] (0xA0/0xA1): 直接アドレス(DS:offset)から読み込む。
    static State<Unit> Mov_A0_A1 =>
        from _1 in SetLog("Mov_A0_A1")
        from offset in MoffsOffset
        from type in OperandTypeW
        from _2 in MovAccFromMoffs(type, offset)
        select unit;

    // MOV [moffs], AL/AX/EAX (0xA2/0xA3): アキュムレータを直接アドレス(DS:offset)へ書き込む。
    static State<Unit> Mov_A2_A3 =>
        from _1 in SetLog("Mov_A2_A3")
        from offset in MoffsOffset
        from type in OperandTypeW
        from _2 in MovMoffsFromAcc(type, offset)
        select unit;

    static State<Unit> Mov_8E =>
        from _1 in SetLog("Mov_8E")
        from m in ModRegRm()
        from data in GetMemOrRegData(m.mod, m.rm, true)
        from _2 in SetSReg3(m.reg, data.data)
        select unit;

    // BSWAP r32 (0F C8+r): 32 ビットレジスタのバイト順を反転する(ModRM なし)。
    static State<Unit> Bswap_0FC8_CF =>
        from _1 in SetLog("Bswap_0FC8_CF")
        from opecode in Opecodes
        let reg = opecode[1] & 0x07
        from v in GetRegData32(reg)
        from _2 in SetRegData32(reg, ((v & 0xFF) << 24) | ((v & 0xFF00) << 8) | ((v >> 8) & 0xFF00) | (v >> 24))
        select unit;

    // CMOVcc r, r/m (0F 40-4F): 条件成立時のみ r/m を reg へ転送する。
    static State<Unit> Cmov_0F40_4F =>
        from _1 in SetLog("Cmov_0F40_4F")
        from opecode in Opecodes
        let cond = opecode[1] & 0xF
        from m in ModRegRm()
        from src in GetMemOrRegData(m.mod, m.rm, true)
        from f in Jcc(cond)
        from _2 in When(f, SetRegData(m.reg, src.data))
        select unit;

    // SETcc r/m8 (0F 90-9F): 条件成立なら 1、不成立なら 0 を r/m8 に書く。
    static State<Unit> Setcc_0F90_9F =>
        from _1 in SetLog("Setcc_0F90_9F")
        from opecode in Opecodes
        let type = opecode[1] & 0xF
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from f in Jcc(type)
        from _2 in SetMemOrRegData(addr, ((byte)(f ? 1 : 0)).ToTypeData())
        select unit;

    // MOVZX/MOVSX reg, r/m8|r/m16 (0F B6/B7/BE/BF)
    //   bit0 of ope2: 0=ソース8bit, 1=ソース16bit
    //   0xBE/0xBF は符号拡張、0xB6/0xB7 はゼロ拡張。
    static State<Unit> MovzxMovsx_0FB6_BF =>
        from _1 in SetLog("MovzxMovsx_0FB6_BF")
        from opecode in Opecodes
        let srcW = 0 != (opecode[1] & 0x01)
        let signed = 0 != (opecode[1] & 0x08)
        from m in ModRegRm()
        // ソースサイズはオペコードで固定(8/16bit)のため、OperandType を介さず直接読む。
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from value in srcW
            ? GetMemOrRegData16(addr).Select(dw => signed ? (uint)(int)(short)dw : dw)
            : GetMemOrRegData8(addr).Select(db => signed ? (uint)(int)(sbyte)db : db)
        from type in OperandType(true)
        from _2 in SetRegData(m.reg, value.ToTypeData(type))
        select unit;

    static State<Unit> Mov_B0_BF =>
        from _1 in SetLog("Mov_B0_BF")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x08)
        let reg = opecode[0] & 0x07
        from data in GetMemoryDataIp(w)
        from _2 in SetRegData(reg, data.data)
        select unit;

    // d=1: reg <- r/m (0x8A/0x8B)、d=0: r/m <- reg (0x88/0x89)。転送元だけを読む(転送先のメモリは読まない)。
    static State<Unit> Mov_88_8B =>
        from _1 in SetLog("Mov_88_8B")
        from opecode in Opecodes
        let d = 0 != (opecode[0] & 0x02)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from _2 in d ? Move(Reg(m.reg, type), Rm(addr, type)) : Move(Rm(addr, type), Reg(m.reg, type))
        select unit;

    // LES (0xC4) / LDS (0xC5): メモリの far ポインタ m16:16/m16:32 を読み、
    // オフセットを reg、セレクタを ES/DS へロードする。レジスタ形式(mod=3)は #UD 扱いで停止。
    // (NTLDR 等の 16bit リアルモードコードが多用する。Linux は使わないため未実装だった。)
    static State<Unit> LesLds_C4_C5 =>
        from _1 in SetLog("LesLds_C4_C5")
        from opecode in Opecodes
        from _2 in LoadFarPtr(opecode[0] == 0xC4 ? 0 : 3) // ES / DS
        select unit;

    // LSS (0F B2) / LFS (0F B4) / LGS (0F B5): 2バイト版の far ポインタロード。
    static State<Unit> LssLfsLgs_0FB2_B5 =>
        from _1 in SetLog("LssLfsLgs_0FB2_B5")
        from opecode in Opecodes
        from _2 in LoadFarPtr(opecode[1] == 0xB2 ? 2 : opecode[1] == 0xB4 ? 4 : 5) // SS / FS / GS
        select unit;

    // far ポインタ m16:16/m16:32 を読み、オフセットを reg、セレクタを sreg へロードする共通部。
    static State<Unit> LoadFarPtr(int sreg) =>
        from m in ModRegRm()
        where 3 != m.mod
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandType(true)
        from pointer in FarPointerMem(addr, type)
        from _3 in SetRegData(m.reg, pointer.offset.ToTypeData(type))
        from _4 in LoadSReg(sreg, pointer.segment)
        select unit;

    // XCHG r/m, r (0x86/0x87)
    static State<Unit> Xchg_86_87 =>
        from _1 in SetLog("Xchg_86_87")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from _2 in Swap(Rm(addr, type), Reg(m.reg, type))
        select unit;

    static State<Unit> Xchg_91_97 =>
        from _1 in SetLog("Xchg_91_97")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from type in OperandType(true)
        from _2 in Swap(Reg(0, type), Reg(reg, type))
        select unit;

    // MOV r/m, Sreg (0x8C): セグメントレジスタを r/m16 へ格納する。
    static State<Unit> Mov_8C =>
        from _1 in SetLog("Mov_8C")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from sreg in GetSRegData(m.reg)
        from _2 in SetMemOrRegData(addr, sreg.ToTypeData())
        select unit;

    // XLAT (0xD7): AL <- [DS:BX + AL]
    static State<Unit> Xlat_D7 =>
        from _1 in SetLog("Xlat_D7")
        from _2 in Xlat
        select unit;
}
