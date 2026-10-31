using static Emu86.CPU;
using static Emu86.Conversions;
using static Emu86.Unit;
using static Emu86.Operands;
using static Emu86.States;
using static Emu86.DataAccess;
using static Emu86.BitOps;
using static Emu86.CpuStates;
using static Emu86.Fetch;
using static Emu86.Decoder;
using static Emu86.AluStates;
using static Emu86.Alu;

namespace Emu86;

// 算術・論理命令: ALU グループ、シフト・回転、乗除算、INC/DEC、TEST、ビット操作、BCD 調整、符号拡張、CMPXCHG/XADD。
static partial class Instructions
{
    // Group1 (0x80/0x81: r/m op imm、0x83: r/m op imm8 符号拡張)。即値は r/m のデータより先に取得する(高速コアと同じ順)。
    static State<Unit> Group1(string name, Func<int, State<Data>> immediate) =>
        from _1 in SetLog(name)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from imm in immediate(type)
        from _2 in Binary(Rm(addr, type), Const(imm), m.reg)
        select unit;

    static State<Unit> Group1_80_81 => Group1("Group1_80_81", GetMemoryDataIp_);

    static State<Unit> Group1_83 => Group1("Group1_83", Imm8SignExtended);

    // CMPXCHG r/m, r (0F B0/B1): acc(AL/AX/EAX) と r/m を比較し、
    // 一致なら r/m <- reg、不一致なら acc <- r/m。フラグは CMP(acc, r/m) と同じ。
    static State<Unit> Cmpxchg_0FB0_B1 =>
        from _1 in SetLog("Cmpxchg_0FB0_B1")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        let rm = Rm(addr, type)
        let acc = Reg(0, type)
        from dst in rm.Read
        from old in acc.Read
        from _f in Calc(old, dst, 7) // CMP acc, r/m のフラグ
        from _2 in old.Value == dst.Value ? Move(rm, Reg(m.reg, type)) : acc.Write(dst)
        select unit;

    // XADD r/m, r (0F C0/C1): temp = r/m + reg(フラグは ADD)、reg <- r/m 旧値、r/m <- temp。
    static State<Unit> Xadd_0FC0_C1 =>
        from _1 in SetLog("Xadd_0FC0_C1")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        let rm = Rm(addr, type)
        let reg = Reg(m.reg, type)
        from dst in rm.Read
        from src in reg.Read
        from sum in Calc(dst, src, 0)
        from _2 in reg.Write(dst)
        from _3 in rm.Write(sum)
        select unit;

    // Group9 (0F C7): /1 = CMPXCHG8B m64。EDX:EAX と m64 を比較し、
    // 一致なら m64 <- ECX:EBX, ZF=1、不一致なら EDX:EAX <- m64, ZF=0。
    static State<Unit> Group9_0FC7 =>
        from _1 in SetLog("Group9_0FC7")
        from m in ModRegRm()
        where m.reg == 1 && m.mod != 3
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from lo in GetMemOrRegData32(addr)
        from hi in GetMemOrRegData32((addr.isMem, addr.addr + 4))
        from cpu in GetCpu
        from _3 in cpu.eax == lo && cpu.edx == hi
            ? from _w1 in SetMemOrRegData(addr, cpu.ebx.ToTypeData())
              from _w2 in SetMemOrRegData((addr.isMem, addr.addr + 4), cpu.ecx.ToTypeData())
              from _z in _zf.Set(true)
              select unit
            : from _r in SetCpu(c => { c.eax = lo; c.edx = hi; return c; })
              from _z in _zf.Set(false)
              select unit
        select unit;

    // 符号付き乗算の積と、type の幅に収まらなければ立てる CF=OF。
    static (long product, bool overflow) SignedMultiply(int type, long a, long b)
    {
        var product = a * b;
        var overflow = type switch
        {
            0 => product < sbyte.MinValue || product > sbyte.MaxValue,
            1 => product < short.MinValue || product > short.MaxValue,
            _ => product < int.MinValue || product > int.MaxValue,
        };
        return (product, overflow);
    }

    // 倍幅の結果を書く: 8 ビットは AX、16 ビットは DX:AX、32 ビットは EDX:EAX(MUL/IMUL と、RDTSC/RDMSR の EDX:EAX)。
    static State<Unit> SetWide(int type, ulong value) =>
        SetCpu(cpu =>
        {
            if (type == 0) cpu.ax = (ushort)value;
            else if (type == 1) { cpu.ax = (ushort)value; cpu.dx = (ushort)(value >> 16); }
            else { cpu.eax = (uint)value; cpu.edx = (uint)(value >> 32); }
            return cpu;
        });

    // IMUL r, ...(2/3 オペランド形式): 積の下位を reg へ、収まらなければ CF=OF=1(SF/ZF 等は未定義)。
    static State<Unit> ImulInto(int reg, int type, long a, long b)
    {
        var (product, overflow) = SignedMultiply(type, a, b);
        return from _1 in SetRegData(reg, ((uint)product).ToTypeData(type))
               from _2 in SetCpu((_cf, overflow), (_of, overflow))
               select unit;
    }

    // IMUL r, r/m (0F AF): 2オペランド符号付き乗算 r = r * r/m。
    static State<Unit> Imul_0FAF =>
        from _1 in SetLog("Imul_0FAF")
        from m in ModRegRm()
        from src in GetMemOrRegData(m.mod, m.rm, true)
        from dst in GetRegData(m.reg, src.data.type)
        from _2 in ImulInto(m.reg, src.data.type, dst.Signed, src.data.Signed)
        select unit;

    // SHLD/SHRD r/m, r, (imm8 | CL): ダブル精度シフト。
    //   0F A4=SHLD imm8, 0F A5=SHLD CL, 0F AC=SHRD imm8, 0F AD=SHRD CL
    static State<Unit> ShldShrd =>
        from _1 in SetLog("ShldShrd")
        from opecode in Opecodes
        let left = (opecode[1] & 0x08) == 0   // A4/A5=SHLD(左), AC/AD=SHRD(右)
        let useCl = (opecode[1] & 0x01) != 0  // A5/AD=CL, A4/AC=imm8
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from dst in GetMemOrRegData(addr, true)
        from src in GetRegData(m.reg, dst.type)
        from cnt in useCl ? GetRegData(1, 0).Select(d => (int)d.db) : GetMemoryDataIp8.Select(b => (int)b)
        let count = cnt & 0x1F
        // count==0 は結果・フラグとも不変
        from _2 in Unless(count == 0, ApplyDoubleShift(addr, dst.type, DoubleShift(dst.Value, src.Value, count, Bits(dst.type), left)))
        select unit;

    static State<Unit> ApplyDoubleShift(
        MemAddr addr, int type, (uint result, bool cf, bool of) r) =>
        from _w in SetMemOrRegData(addr, r.result.ToTypeData(type))
        from _f in SetCpu(
            (_cf, r.cf),
            (_of, r.of),
            (_zf, (r.result & Mask(type)) == 0),
            (_sf, (r.result & Msb(type)) != 0)
        )
        select unit;

    // BT/BTS/BTR/BTC r/m, r (0F A3/AB/B3/BB): ビット番号はレジスタ reg。
    //   ope2 の bit3-4 で op を選ぶ: A3->BT(0) AB->BTS(1) B3->BTR(2) BB->BTC(3)
    // メモリオペランドでは「ビット列アドレッシング」: ビット番号(符号付き)を
    // ワード単位でアドレスへ繰り込む(基底 + (bit>>5)*4)。Linux の set_bit/test_bit が
    // 32 を超えるビット番号で多用するため、これがないとビットマップ操作が全て壊れる。
    static State<Unit> BitTest_reg =>
        from _1 in SetLog("BitTest_reg")
        from opecode in Opecodes
        let op = (opecode[1] >> 3) & 0x3
        from m in ModRegRm()
        from addr0 in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandType(true)
        from bitData in GetRegData(m.reg, type)
        let bitRaw = bitData.Signed
        let addr = addr0.isMem
            ? (true, (uint)(addr0.addr + (bitRaw >> (type == 1 ? 4 : 5)) * (type == 1 ? 2 : 4)))
            : addr0
        from data in GetMemOrRegData(addr, true)
        from _2 in BitTest(addr, data, bitRaw & (type == 1 ? 15 : 31), op)
        select unit;

    // Group8 (0F BA): BT/BTS/BTR/BTC r/m, imm8。reg=4..7 で op を選ぶ。
    static State<Unit> Group8_0FBA =>
        from _1 in SetLog("Group8_0FBA")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, true)
        from imm in GetMemoryDataIp8
        from _2 in BitTest(addr, data, imm, m.reg & 0x3)
        select unit;

    // BSF/BSR r, r/m (0F BC/BD): ビットスキャン。BC=前方(最下位), BD=後方(最上位)。
    static State<Unit> BitScan_0FBC_BD =>
        from _1 in SetLog("BitScan_0FBC_BD")
        from opecode in Opecodes
        let forward = opecode[1] == 0xBC
        from m in ModRegRm()
        from src in GetMemOrRegData(m.mod, m.rm, true)
        from _2 in BitScan(m.reg, src.data, forward)
        select unit;

    static State<Unit> Arithmetic =>
        from _1 in SetLog("Arithmetic")
        from opecode in Opecodes
        let kind = (opecode[0] >> 3) & 0x7
        let form = opecode[0] & 0x7
        // 下位3ビットが 4/5 のものは ModRM を持たない AL/AX/EAX, imm 形式。
        from _2 in (form < 4) ? ArithmeticModRM(kind) : ArithmeticAccImm(kind, 0 != (form & 1))
        select unit;

    // d=1 (02/03 など): reg <- reg op r/m、d=0 (00/01 など): r/m <- r/m op reg。
    static State<Unit> ArithmeticModRM(int kind) =>
        from opecode in Opecodes
        let d = 0 != (opecode[0] & 2)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from _ in d ? Binary(Reg(m.reg, type), Rm(addr, type), kind) : Binary(Rm(addr, type), Reg(m.reg, type), kind)
        select unit;

    static State<Unit> ArithmeticAccImm(int kind, bool w) =>
        from type in OperandType(w)
        from imm in GetMemoryDataIp_(type)
        from _ in Binary(Reg(0, type), Const(imm), kind)
        select unit;

    // Group2(シフト/ローテート): r/m を count 回。シフト量は 1 (D0/D1)、CL (D2/D3)、imm8 (C0/C1)。
    static State<Unit> ShiftRotate(string name, State<int> count) =>
        from _1 in SetLog(name)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        let rm = Rm(addr, type)
        from data in rm.Read
        from n in count
        from result in Group2(data, n, m.reg)
        from _2 in rm.Write(result)
        select unit;

    static State<Unit> Group2_D0_D1 => ShiftRotate("Group2_D0_D1", 1.ToState());

    static State<Unit> Group2_D2_D3 => ShiftRotate("Group2_D2_D3", _cl.Get().Select(cl => (int)cl));

    static State<Unit> Group2_C0_C1 => ShiftRotate("Group2_C0_C1", GetMemoryDataIp8.Select(imm => (int)imm));

    // Group3 (0xF6/0xF7): reg で TEST/NOT/NEG/MUL/IMUL/DIV/IDIV を選ぶ。
    static State<Unit> Group3_F6_F7 =>
        from _1 in SetLog("Group3_F6_F7")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from data in Rm(addr, type).Read
        from _2 in Group(
            m.reg,
            (0, Group3_Test(data)),
            (1, Group3_Test(data)),
            (2, Group3_Not(addr, data)),
            (3, Group3_Neg(addr, data)),
            (4, Group3_Mul(data)),
            (5, Group3_Imul(data)),
            (6, Group3_Div(data)),
            (7, Group3_Idiv(data))
        )
        select unit;

    // TEST r/m, imm : AND の結果を捨ててフラグ(CF=OF=0, ZF/SF)だけ更新する。
    static State<Unit> Group3_Test(Data data) =>
        from imm in GetMemoryDataIp_(data.type)
        from _ in Test(Const(data), Const(imm))
        select unit;

    // NOT r/m : ビット反転（フラグ変化なし）。
    static State<Unit> Group3_Not(MemAddr addr, Data data) =>
        SetMemOrRegData(addr, (~data.Value).ToTypeData(data.type));

    // NEG r/m : 0 - r/m。フラグは SUB と同じ。
    static State<Unit> Group3_Neg(MemAddr addr, Data data) =>
        from _f in update_eflags_sub(data.type, 0, data.Value)
        from _w in SetMemOrRegData(addr, (0u - data.Value).ToTypeData(data.type))
        select unit;

    // MUL r/m : 符号なし乗算。AL*r/m8->AX, AX*r/m16->DX:AX, EAX*r/m32->EDX:EAX。上位が 0 でなければ CF=OF=1。
    static State<Unit> Group3_Mul(Data data) =>
        from acc in GetRegData(0, data.type)
        let product = (ulong)acc.Value * data.Value
        let upper = product >> Bits(data.type)
        from _1 in SetWide(data.type, product)
        from _2 in SetCpu((_cf, upper != 0), (_of, upper != 0))
        select unit;

    // IMUL r/m : 符号付き乗算。積が下位の幅に収まらなければ CF=OF=1。
    static State<Unit> Group3_Imul(Data data) =>
        from acc in GetRegData(0, data.type)
        let result = SignedMultiply(data.type, acc.Signed, data.Signed)
        from _1 in SetWide(data.type, (ulong)result.product)
        from _2 in SetCpu((_cf, result.overflow), (_of, result.overflow))
        select unit;

    // IMUL r, r/m, imm (0x69 imm16/32, 0x6B imm8符号拡張): 3オペランド符号付き乗算。
    //   r = r/m * imm。結果が転送先サイズに収まらなければ CF=OF=1（SF/ZF等は未定義）。
    static State<Unit> Imul_69_6B =>
        from _1 in SetLog("Imul_69_6B")
        from opecode in Opecodes
        let imm8 = 0 != (opecode[0] & 0x02)   // 0x6B は imm8、0x69 は imm16/32
        from m in ModRegRm()
        from src in GetMemOrRegData(m.mod, m.rm, true)
        let type = src.data.type              // w=true なので 1(16bit) か 2(32bit)
        from imm in imm8 ? Imm8SignExtended(type) : GetMemoryDataIp_(type)
        from _2 in ImulInto(m.reg, type, src.data.Signed, imm.Signed)
        select unit;

    // DIV r/m : 符号なし除算。商と剰余を AL/AH, AX/DX, EAX/EDX へ(規則は Alu.Divide。IR と共有)。
    static State<Unit> Group3_Div(Data data) =>
        SetCpu(c => { Divide(c, data.type, data.Value, signed: false); return c; });

    // IDIV r/m : 符号付き除算。
    static State<Unit> Group3_Idiv(Data data) =>
        SetCpu(c => { Divide(c, data.type, data.Value, signed: true); return c; });

    // INC r/m: type に応じて +1 し、フラグ(CF以外)を更新して書き戻す。
    static State<Unit> IncData(MemAddr addr, Data data) =>
        from _f in update_eflags_inc(data)
        from _w in SetMemOrRegData(addr, (data.Value + 1).ToTypeData(data.type))
        select unit;

    // DEC r/m: type に応じて -1 し、フラグ(CF以外)を更新して書き戻す。
    static State<Unit> DecData(MemAddr addr, Data data) =>
        from _f in update_eflags_dec(data)
        from _w in SetMemOrRegData(addr, (data.Value - 1).ToTypeData(data.type))
        select unit;

    // Group4 (0xFE): reg=0 INC r/m8, reg=1 DEC r/m8
    static State<Unit> Group4_FE =>
        from _1 in SetLog("Group4_FE")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, false)
        from _2 in Group(
            m.reg,
            (0, IncData(addr, data)),
            (1, DecData(addr, data))
        )
        select unit;

    // Group5 (0xFF): INC/DEC/CALL/JMP r/m, PUSH r/m
    static State<Unit> Group5_FF =>
        from _1 in SetLog("Group5_FF")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, true)
        from _2 in Group(
            m.reg,
            (0, IncData(addr, data)),
            (1, DecData(addr, data)),
            (2, Call_rm(data)),               // 近傍間接 CALL
            (3, CallFar_rm(addr, data.type)), // far 間接 CALL m16:16/m16:32
            (4, JmpTo(data)),                 // 近傍間接 JMP
            (5, JmpFar_rm(addr, data.type)),  // far 間接 JMP m16:16/m16:32
            (6, Push(data))                   // PUSH r/m
        )
        select unit;

    // TEST r/m, r (0x84/0x85): AND の結果を捨ててフラグだけ更新する。
    static State<Unit> Test_84_85 =>
        from _1 in SetLog("Test_84_85")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from type in OperandTypeW
        from _2 in Test(Rm(addr, type), Reg(m.reg, type))
        select unit;

    // TEST AL/AX/EAX, imm (0xA8/0xA9)
    static State<Unit> Test_A8_A9 =>
        from _1 in SetLog("Test_A8_A9")
        from type in OperandTypeW
        from imm in GetMemoryDataIp_(type)
        from _2 in Test(Reg(0, type), Const(imm))
        select unit;

    // INC r (40-47) / DEC r (48-4F): オペコード下位 3 ビットのレジスタをオペランドサイズ幅で。
    static State<Unit> IncDecReg(string name, Func<MemAddr, Data, State<Unit>> step) =>
        from _1 in SetLog(name)
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from type in OperandType(true)
        from value in GetRegData(reg, type)
        from _2 in step((false, (uint)reg), value)
        select unit;

    static State<Unit> Inc_40_47 => IncDecReg("Inc_40_47", IncData);

    static State<Unit> Dec_48_4F => IncDecReg("Dec_48_4F", DecData);

    // CBW/CWDE (0x98): オペランドサイズ 16 なら AL→AX、32 なら AX→EAX の符号拡張。
    static State<Unit> Cbw_98 =>
        from _1 in SetLog("Cbw_98")
        from type in OperandType(true)
        from _2 in SetCpu(cpu =>
        {
            if (type == 2) cpu.eax = (uint)(int)(short)cpu.ax;
            else cpu.ax = (ushort)(short)(sbyte)cpu.al;
            return cpu;
        })
        select unit;

    // CWD/CDQ (0x99): オペランドサイズ 16 なら DX:AX、32 なら EDX:EAX の符号拡張。
    static State<Unit> Cwd_99 =>
        from _1 in SetLog("Cwd_99")
        from type in OperandType(true)
        from _2 in SetCpu(cpu =>
        {
            if (type == 2) cpu.edx = (int)cpu.eax < 0 ? 0xFFFFFFFF : 0;
            else cpu.dx = (ushort)((short)cpu.ax < 0 ? 0xFFFF : 0x0000);
            return cpu;
        })
        select unit;

    // BCD 調整命令群(規則は Alu.AdjustDecimal。IR と共有)。Linux は使わないが、Windows の HAL/NTLDR 等の 16/32bit コードが使う。
    // AAA (0x37) / AAS (0x3F): 加減算後の非パック BCD 調整。
    static State<Unit> AaaAas_37_3F =>
        from _1 in SetLog("AaaAas_37_3F")
        from opecode in Opecodes
        from _2 in SetCpu(cpu => { AdjustDecimal(cpu, opecode[0] == 0x37 ? 0 : 1, 0); return cpu; })
        select unit;

    // DAA (0x27) / DAS (0x2F): 加減算後のパック BCD 調整。
    static State<Unit> DaaDas_27_2F =>
        from _1 in SetLog("DaaDas_27_2F")
        from opecode in Opecodes
        from _2 in SetCpu(cpu => { AdjustDecimal(cpu, opecode[0] == 0x27 ? 2 : 3, 0); return cpu; })
        select unit;

    // AAM (D4 ib) / AAD (D5 ib): AL を基数 ib で分解 / AH:AL を基数 ib で結合。
    static State<Unit> AamAad_D4_D5 =>
        from _1 in SetLog("AamAad_D4_D5")
        from opecode in Opecodes
        from imm in GetMemoryDataIp8
        where opecode[0] != 0xD4 || imm != 0 // AAM 0 は #DE
        from _3 in SetCpu(cpu => { AdjustDecimal(cpu, opecode[0] == 0xD4 ? 4 : 5, imm); return cpu; })
        select unit;
}
