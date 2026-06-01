using static Emu86.CPU;
using static Emu86.Ext;
using static Emu86.Unit;

namespace Emu86;

struct OpecodeDic
{
    public State<Unit> state;
    public OpecodeDic[] next;
}

static class Program
{
    static State<Unit> FarJump_EA =>
        from _1 in SetLog("FarJump_EA")
        from offset in GetMemoryDataIp16
        from segment in GetMemoryDataIp16
        from _2 in _ip.Set(offset)
        from _3 in _cs.Set(segment)
        select unit;

    static State<Unit> Jump_E9 =>
        from _1 in SetLog("Jump_E9")
        from offset in GetMemoryDataIp16
        from _2 in IpInc((short)offset)
        select unit;

    static State<Unit> Group1_83 =>
        from _ in SetLog("Group1_83")
        from d1 in ModRegRm()
        from d2 in GetMemOrRegAddr(d1.mod, d1.rm)
        from d3 in GetMemoryDataIp8
        from d4 in GetMemOrRegData(d2, true)
        from d5 in Calc(d4.type, d4.db, d4.dw, d4.dd, d3, (ushort)(sbyte)d3, (uint)(sbyte)d3, d1.reg)
        from d6 in SetMemOrRegData(d2, d5)
        select d6;

    static State<Unit> Movs_A4_A5 =>
        from _1 in SetLog("Movs_A4_A5")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from _2 in Movs(w)
        select unit;

    static State<Unit> Stos_AA_AB =>
        from _1 in SetLog("Stos_AA_AB")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from _2 in Stos(w)
        select unit;

    static State<Unit> Lods_AC_AD =>
        from _1 in SetLog("Lods_AC_AD")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from _2 in Lods(w)
        select unit;

    static State<Unit> Cmps_A6_A7 =>
        from _1 in SetLog("Cmps_A6_A7")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from _2 in Cmps(w)
        select unit;

    static State<Unit> Scas_AE_AF =>
        from _1 in SetLog("Scas_AE_AF")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from _2 in Scas(w)
        select unit;

    // REP が前置できる文字列命令のオペコード集合。
    static readonly HashSet<int> stringOps = [0xA4, 0xA5, 0xA6, 0xA7, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF];
    // ZF を見て継続判定する比較系（CMPS/SCAS）。それ以外は CX のみで判定する。
    static readonly HashSet<int> zfStringOps = [0xA6, 0xA7, 0xAE, 0xAF];

    // 文字列命令を CX 回繰り返す共通ループ。
    //   repZf==null     : REP    (0xF3, 非比較系) — CX 回そのまま繰り返す
    //   repZf==true     : REPE   (0xF3, 比較系)   — ZF==0 になったら打ち切り
    //   repZf==false    : REPNE  (0xF2)           — ZF==1 になったら打ち切り
    static State<Unit> RepLoop(bool? repZf) => (env, cpu1, ope) =>
    {
        var (ok, op, cpu2, log) = GetMemoryDataIp8(env, cpu1, ope);
        if (!ok)
            return (false, default, cpu1, log);

        if (!stringOps.Contains(op) || !oneByte.TryGetValue(op, out var state))
            return (false, default, cpu1, log);

        // 比較系のみ ZF 条件を適用する（MOVS/STOS/LODS は CX だけで回す）。
        var checkZf = repZf is bool && zfStringOps.Contains(op);

        var cpu = cpu2;
        while (_cx.getter(cpu) != 0)
        {
            var (ok2, _, cpuN, _) = state(env, cpu, [op]);
            if (!ok2)
                return (false, default, cpu1, log);
            cpu = _cx.setter(cpuN)((ushort)(_cx.getter(cpuN) - 1));

            if (checkZf && _zf.getter(cpu) != repZf.Value)
                break;
        }
        return (true, unit, cpu, log);
    };

    // REP / REPE / REPZ (0xF3)
    static State<Unit> Rep_F3 => RepLoop(true);
    // REPNE / REPNZ (0xF2)
    static State<Unit> Repne_F2 => RepLoop(false);

    static State<Unit> Lea_8D =>
        from _1 in SetLog("Lea_8D")
        from m in ModRegRm()
        // LEA はメモリを読まず、実効アドレス(オフセット)そのものを reg へ書く。
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from _2 in SetRegData16(m.reg, (ushort)addr.addr)
        select unit;

    // MOV r/m, imm (0xC6/0xC7)
    static State<Unit> Mov_C6_C7 =>
        from _1 in SetLog("Mov_C6_C7")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from imm in w ? GetMemoryDataIp16.Select(v => v.ToTypeData())
                      : GetMemoryDataIp8.Select(v => v.ToTypeData())
        from _2 in SetMemOrRegData(addr, imm)
        select unit;

    // MOV AL/AX, [moffs] (0xA0/0xA1): 直接アドレス(DS:offset16)から読み込む。
    static State<Unit> Mov_A0_A1 =>
        from _1 in SetLog("Mov_A0_A1")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from offset in GetMemoryDataIp16
        from _2 in MovAccFromMoffs(w, offset)
        select unit;

    // MOV [moffs], AL/AX (0xA2/0xA3): アキュムレータを直接アドレス(DS:offset16)へ書き込む。
    static State<Unit> Mov_A2_A3 =>
        from _1 in SetLog("Mov_A2_A3")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from offset in GetMemoryDataIp16
        from _2 in MovMoffsFromAcc(w, offset)
        select unit;

    static State<Unit> Mov_8E =>
        from _1 in SetLog("Mov_8E")
        from m in ModRegRm()
        from data in GetMemOrRegData(m.mod, m.rm, true)
        from _2 in SetSReg3(m.reg, data.data)
        select unit;

    static State<Unit> Mov_0F20 =>
        from _1 in SetLog("Mov_0F20")
        from m in ModRegRm()
        from _2 in SetResult(3 == m.mod)
        from data in GetCrReg(m.reg)
        from _3 in SetRegData32(m.rm, data)
        select unit;

    static State<Unit> Mov_0F22 =>
        from _1 in SetLog("Mov_0F22")
        from m in ModRegRm()
        from _2 in SetResult(3 == m.mod)
        from data in GetRegData32(m.rm)
        from _3 in SetCrReg(m.reg, data)
        select unit;

    static State<Unit> Jcc_0F80_0F8F =>
        from _1 in SetLog("Jcc_0F80_0F8F")
        from opecode in Opecodes
        let type = opecode[1] & 0xF
        from f in Jcc(type)
        from offset in GetMemoryDataIp16
        let inc = f ? (short)offset : 0
        from _2 in IpInc(inc)
        select unit;

    static State<Unit> Mov_B0_BF =>
        from _1 in SetLog("Mov_B0_BF")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x08)
        let reg = opecode[0] & 0x07
        from data in GetMemoryDataIp(w)
        from _2 in SetRegData(reg, data.data)
        select unit;

    static State<Unit> Mov_88_8B =>
        from _1 in SetLog("Mov_88_8B")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        let d = 0 != (opecode[0] & 0x02)
        from m in ModRegRm()
        from rmData in GetMemOrRegData(m.mod, m.rm, w)
        from regData in GetRegData(m.reg, rmData.data.type)
        // d=1: reg <- r/m (0x8A/0x8B) , d=0: r/m <- reg (0x88/0x89)
        from _2 in d ? SetRegData(m.reg, rmData.data) : SetMemOrRegData(rmData.input, regData)
        select unit;

    static State<Unit> Arithmetic =>
        from _1 in SetLog("Arithmetic")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 1)
        let d = 0 != (opecode[0] & 2)
        let kind = (opecode[0] >> 3) & 0x7
        from m in ModRegRm()
        from rmData in GetMemOrRegData(m.mod, m.rm, w)
        from regData in GetRegData(m.reg, rmData.data.type)
        let d1 = d ? regData : rmData.data
        let d2 = d ? rmData.data : regData
        from ret in Calc(d1, d2, kind)
        from _2 in d ? SetRegData(m.reg, ret) : SetMemOrRegData(rmData.input, ret)
        select unit;

    // Group2 (0xD0/0xD1): r/m, 1
    static State<Unit> Group2_D0_D1 =>
        from _1 in SetLog("Group2_D0_D1")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, w)
        from ret in Group2(data, 1, m.reg)
        from _2 in SetMemOrRegData(addr, ret)
        select unit;

    // Group2 (0xD2/0xD3): r/m, CL
    static State<Unit> Group2_D2_D3 =>
        from _1 in SetLog("Group2_D2_D3")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, w)
        from cl in GetRegData(1, 0) // CL = reg8 #1
        from ret in Group2(data, cl.db, m.reg)
        from _2 in SetMemOrRegData(addr, ret)
        select unit;

    // Group2 (0xC0/0xC1): r/m, imm8
    static State<Unit> Group2_C0_C1 =>
        from _1 in SetLog("Group2_C0_C1")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, w)
        from imm in GetMemoryDataIp8
        from ret in Group2(data, imm, m.reg)
        from _2 in SetMemOrRegData(addr, ret)
        select unit;

    // Group3 (0xF6/0xF7): reg で TEST/NOT/NEG/MUL/IMUL/DIV/IDIV を選ぶ。
    static State<Unit> Group3_F6_F7 =>
        from _1 in SetLog("Group3_F6_F7")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, w)
        from _2 in Choice(
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
    static State<Unit> Group3_Test((int type, byte db, ushort dw, uint dd) data) =>
        from imm in GetMemoryDataIp_(data.type)
        from _ in data.type == 0 ? update_eflags((byte)(data.db & imm.db))
                : data.type == 1 ? update_eflags((ushort)(data.dw & imm.dw))
                : update_eflags(data.dd & imm.dd)
        select unit;

    // NOT r/m : ビット反転（フラグ変化なし）。
    static State<Unit> Group3_Not((bool isMem, uint addr) addr, (int type, byte db, ushort dw, uint dd) data) =>
        SetMemOrRegData(addr,
            data.type == 0 ? ((byte)~data.db).ToTypeData()
          : data.type == 1 ? ((ushort)~data.dw).ToTypeData()
          : (~data.dd).ToTypeData());

    // NEG r/m : 0 - r/m。フラグは SUB と同じ。
    static State<Unit> Group3_Neg((bool isMem, uint addr) addr, (int type, byte db, ushort dw, uint dd) data) =>
        from _f in data.type == 0 ? update_eflags_sub((byte)0, data.db)
                 : data.type == 1 ? update_eflags_sub((ushort)0, data.dw)
                 : update_eflags_sub(0u, data.dd) // 注: byte/ushort の (byte)0/(ushort)0 はオーバーロード解決を明示するため残す
        from _w in SetMemOrRegData(addr,
            data.type == 0 ? ((byte)(0 - data.db)).ToTypeData()
          : data.type == 1 ? ((ushort)(0 - data.dw)).ToTypeData()
          : (0u - data.dd).ToTypeData())
        select unit;

    // MUL r/m : 符号なし乗算。AL*r/m8->AX, AX*r/m16->DX:AX, EAX*r/m32->EDX:EAX。
    static State<Unit> Group3_Mul((int type, byte db, ushort dw, uint dd) data) =>
        from cpu in GetCpu
        let upper =
            data.type == 0 ? (uint)((cpu.al * data.db) >> 8) & 0xFF :
            data.type == 1 ? ((uint)cpu.ax * data.dw >> 16) & 0xFFFF :
                             (uint)((ulong)cpu.eax * data.dd >> 32)
        from _1 in SetCpu(c =>
        {
            if (data.type == 0) { c.ax = (ushort)(c.al * data.db); }
            else if (data.type == 1) { uint p = (uint)c.ax * data.dw; c.ax = (ushort)p; c.dx = (ushort)(p >> 16); }
            else { ulong p = (ulong)c.eax * data.dd; c.eax = (uint)p; c.edx = (uint)(p >> 32); }
            return c;
        })
        from _2 in SetCpu((_cf, upper != 0), (_of, upper != 0))
        select unit;

    // IMUL r/m : 符号付き乗算。
    static State<Unit> Group3_Imul((int type, byte db, ushort dw, uint dd) data) =>
        from cpu in GetCpu
        let prod = data.type == 0 ? (sbyte)cpu.al * (sbyte)data.db
                 : data.type == 1 ? (short)cpu.ax * (short)data.dw
                 : (long)(int)cpu.eax * (int)data.dd
        let of = data.type == 0 ? prod < sbyte.MinValue || prod > sbyte.MaxValue
               : data.type == 1 ? prod < short.MinValue || prod > short.MaxValue
               : prod < int.MinValue || prod > int.MaxValue
        from _1 in SetCpu(c =>
        {
            if (data.type == 0) { c.ax = (ushort)(short)prod; }
            else if (data.type == 1) { c.ax = (ushort)prod; c.dx = (ushort)(prod >> 16); }
            else { c.eax = (uint)prod; c.edx = (uint)(prod >> 32); }
            return c;
        })
        from _2 in SetCpu((_cf, of), (_of, of))
        select unit;

    // DIV r/m : 符号なし除算。商と剰余を AL/AH, AX/DX, EAX/EDX へ。
    static State<Unit> Group3_Div((int type, byte db, ushort dw, uint dd) data) =>
        SetCpu(c =>
        {
            if (data.type == 0)
            {
                if (data.db == 0) throw new DivideByZeroException();
                uint dividend = c.ax;
                c.al = (byte)(dividend / data.db);
                c.ah = (byte)(dividend % data.db);
            }
            else if (data.type == 1)
            {
                if (data.dw == 0) throw new DivideByZeroException();
                uint dividend = ((uint)c.dx << 16) | c.ax;
                c.ax = (ushort)(dividend / data.dw);
                c.dx = (ushort)(dividend % data.dw);
            }
            else
            {
                if (data.dd == 0) throw new DivideByZeroException();
                ulong dividend = ((ulong)c.edx << 32) | c.eax;
                c.eax = (uint)(dividend / data.dd);
                c.edx = (uint)(dividend % data.dd);
            }
            return c;
        });

    // IDIV r/m : 符号付き除算。
    static State<Unit> Group3_Idiv((int type, byte db, ushort dw, uint dd) data) =>
        SetCpu(c =>
        {
            if (data.type == 0)
            {
                if (data.db == 0) throw new DivideByZeroException();
                int dividend = (short)c.ax;
                c.al = (byte)(sbyte)(dividend / (sbyte)data.db);
                c.ah = (byte)(sbyte)(dividend % (sbyte)data.db);
            }
            else if (data.type == 1)
            {
                if (data.dw == 0) throw new DivideByZeroException();
                int dividend = (int)(((uint)c.dx << 16) | c.ax);
                c.ax = (ushort)(short)(dividend / (short)data.dw);
                c.dx = (ushort)(short)(dividend % (short)data.dw);
            }
            else
            {
                if (data.dd == 0) throw new DivideByZeroException();
                long dividend = (long)(((ulong)c.edx << 32) | c.eax);
                c.eax = (uint)(dividend / (int)data.dd);
                c.edx = (uint)(dividend % (int)data.dd);
            }
            return c;
        });

    // INC r/m: type に応じて +1 し、フラグ(CF以外)を更新して書き戻す。
    static State<Unit> IncData((bool isMem, uint addr) addr, (int type, byte db, ushort dw, uint dd) data) =>
        from _f in data.type == 0 ? update_eflags_inc(data.db)
                 : data.type == 1 ? update_eflags_inc(data.dw)
                 : update_eflags_inc(data.dd)
        from _w in SetMemOrRegData(addr,
            data.type == 0 ? ((byte)(data.db + 1)).ToTypeData()
          : data.type == 1 ? ((ushort)(data.dw + 1)).ToTypeData()
          : (data.dd + 1).ToTypeData())
        select unit;

    // DEC r/m: type に応じて -1 し、フラグ(CF以外)を更新して書き戻す。
    static State<Unit> DecData((bool isMem, uint addr) addr, (int type, byte db, ushort dw, uint dd) data) =>
        from _f in data.type == 0 ? update_eflags_dec(data.db)
                 : data.type == 1 ? update_eflags_dec(data.dw)
                 : update_eflags_dec(data.dd)
        from _w in SetMemOrRegData(addr,
            data.type == 0 ? ((byte)(data.db - 1)).ToTypeData()
          : data.type == 1 ? ((ushort)(data.dw - 1)).ToTypeData()
          : (data.dd - 1).ToTypeData())
        select unit;

    // Group4 (0xFE): reg=0 INC r/m8, reg=1 DEC r/m8
    static State<Unit> Group4_FE =>
        from _1 in SetLog("Group4_FE")
        from m in ModRegRm()
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from data in GetMemOrRegData(addr, false)
        from _2 in Choice(
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
        from _2 in Choice(
            m.reg,
            (0, IncData(addr, data)),
            (1, DecData(addr, data)),
            (2, Call_rm(data.dw)),      // 近傍間接 CALL
            (4, _ip.Set(data.dw)),      // 近傍間接 JMP
            (6, Push16(data.dw))        // PUSH r/m16
        )
        select unit;

    // 近傍間接 CALL: 戻り番地(現在のIP)を push してから IP を target に設定する。
    static State<Unit> Call_rm(ushort target) =>
        from ret in GetDataFromCpu(cpu => cpu.ip)
        from _1 in Push16(ret)
        from _2 in _ip.Set(target)
        select unit;

    static State<Unit> Cli_FA =>
        from _ in SetLog("Cli_FA")
        select unit;

    static State<Unit> Sti_FB =>
        from _ in SetLog("Sti_FB")
        select unit;

    static State<Unit> Cld_FC =>
        from _1 in SetLog("Cld_FC")
        from _2 in _df.Set(false)
        select unit;

    static State<Unit> Std_FD =>
        from _1 in SetLog("Std_FD")
        from _2 in _df.Set(true)
        select unit;

    static State<Unit> Test_84_85 =>
        from _1 in SetLog("Test_84_85")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from rmData in GetMemOrRegData(m.mod, m.rm, w)
        from regData in GetRegData(m.reg, rmData.data.type)
        // TEST は AND の結果を捨ててフラグ(CF=OF=0, ZF/SF)だけ更新する。
        from _2 in w
            ? update_eflags((ushort)(rmData.data.dw & regData.dw))
            : update_eflags((byte)(rmData.data.db & regData.db))
        select unit;

    static State<Unit> Test_A8_A9 =>
        from _1 in SetLog("Test_A8_A9")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from acc in GetRegData(0, w ? 1 : 0) // 0=AL/AX
        from imm in GetMemoryDataIp_(acc.type)
        from _2 in w
            ? update_eflags((ushort)(acc.dw & imm.dw))
            : update_eflags((byte)(acc.db & imm.db))
        select unit;

    static State<Unit> Xchg_91_97 =>
        from _1 in SetLog("Xchg_91_97")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from ax in GetRegData16(0)
        from rv in GetRegData16(reg)
        from _2 in SetRegData16(0, rv)
        from _3 in SetRegData16(reg, ax)
        select unit;

    static State<Unit> Inc_40_47 =>
        from _1 in SetLog("Inc_40_47")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from v in GetRegData16(reg)
        from _2 in update_eflags_inc(v)
        from _3 in SetRegData16(reg, (ushort)(v + 1))
        select unit;

    static State<Unit> Dec_48_4F =>
        from _1 in SetLog("Dec_48_4F")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from v in GetRegData16(reg)
        from _2 in update_eflags_dec(v)
        from _3 in SetRegData16(reg, (ushort)(v - 1))
        select unit;

    static State<Unit> Call_E8 =>
        from _1 in SetLog("Call_E8")
        from offset in GetMemoryDataIp16
        // rel16 読み取り後の IP（＝次命令アドレス）を戻り番地として push する。
        from ret in GetDataFromCpu(cpu => cpu.ip)
        from _2 in Push16(ret)
        from _3 in IpInc((short)offset)
        select unit;

    static State<Unit> Ret_C3 =>
        from _1 in SetLog("Ret_C3")
        from ret in Pop16
        from _2 in _ip.Set(ret)
        select unit;

    static State<Unit> Ret_C2 =>
        from _1 in SetLog("Ret_C2")
        from imm in GetMemoryDataIp16
        from ret in Pop16
        from _2 in _ip.Set(ret)
        from _3 in SetCpu(cpu => { cpu.sp += imm; return cpu; })
        select unit;

    static State<Unit> Push_50_57 =>
        from _1 in SetLog("Push_50_57")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from value in GetRegData16(reg)
        from _2 in Push16(value)
        select unit;

    static State<Unit> Pop_58_5F =>
        from _1 in SetLog("Pop_58_5F")
        from opecode in Opecodes
        let reg = opecode[0] & 0x07
        from value in Pop16
        from _2 in SetRegData16(reg, value)
        select unit;

    static State<Unit> PushImm_68 =>
        from _1 in SetLog("PushImm_68")
        from value in GetMemoryDataIp16
        from _2 in Push16(value)
        select unit;

    static State<Unit> PushImm_6A =>
        from _1 in SetLog("PushImm_6A")
        from value in GetMemoryDataIp8
        from _2 in Push16((ushort)(sbyte)value)
        select unit;

    static State<Unit> Pushf_9C =>
        from _1 in SetLog("Pushf_9C")
        from fl in GetDataFromCpu(cpu => (ushort)cpu.eflags)
        from _2 in Push16(fl)
        select unit;

    static State<Unit> Popf_9D =>
        from _1 in SetLog("Popf_9D")
        from fl in Pop16
        from _2 in SetCpu(cpu => { cpu.eflags = (cpu.eflags & 0xFFFF0000) | fl; return cpu; })
        select unit;

    static State<Unit> Nop_90 =>
        from _ in SetLog("Nop_90")
        select unit;

    static State<Unit> Hlt_F4 =>
        from _ in SetLog("Hlt_F4")
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
        from _2 in SetCpu(cpu => { cpu.cf = !cpu.cf; return cpu; })
        select unit;

    static State<Unit> Jmp_EB =>
        from _1 in SetLog("Jmp_EB")
        from offset in GetMemoryDataIp8
        from _2 in IpInc((sbyte)offset)
        select unit;

    static State<Unit> Jcc_70_7F =>
        from _1 in SetLog("Jcc_70_7F")
        from opecode in Opecodes
        let type = opecode[0] & 0xF
        from f in Jcc(type)
        from offset in GetMemoryDataIp8
        let inc = f ? (sbyte)offset : 0
        from _2 in IpInc(inc)
        select unit;

    static State<Unit> Out_E6_E7 =>
        from _1 in SetLog("Out_E6_E7")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 1)
        from port in GetMemoryDataIp8
        from _2 in SetCpu((env, cpu) =>
        {
            env.IoPort[port] = cpu.al;
            if (w) { env.IoPort[port + 1] = cpu.ah; }
            return cpu;
        })
        select unit;

    static State<Unit> In_E4_E5 =>
        from _1 in SetLog("In_E4_E5")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 1)
        from port in GetMemoryDataIp8
        from _2 in SetCpu((env, cpu) =>
        {
            if (w) { cpu.ax = (ushort)(env.IoPort[port] | (env.IoPort[port + 1] << 8)); }
            else { cpu.al = env.IoPort[port]; }
            return cpu;
        })
        select unit;

    static State<Unit> Lgdt(int mod, int rm) =>
        from addr in GetMemOrRegAddr(mod, rm)
        from dw in GetMemOrRegData16(addr)
        from dd in GetMemOrRegData32((addr.isMem, addr.addr + 2))
        from _ in SetCpu(cpu => { cpu.gdt_base = dd; cpu.gdt_limit = dw; return cpu; })
        select unit;

    static State<Unit> Lidt(int mod, int rm) =>
        from addr in GetMemOrRegAddr(mod, rm)
        from dw in GetMemOrRegData16(addr)
        from dd in GetMemOrRegData32((addr.isMem, addr.addr + 2))
        from _ in SetCpu(cpu => { cpu.idt_base = dd; cpu.idt_limit = dw; return cpu; })
        select unit;

    static State<Unit> Group7_0F01 =>
        from _1 in SetLog("Group7_0F01")
        from m in ModRegRm()
        from _ in Choice(
            m.reg,
            (2, Lgdt(m.mod, m.rm)),
            (3, Lidt(m.mod, m.rm))
        )
        select unit;

    static State<Unit> Group1_80_81 =>
        from _1 in SetLog("Group1_80_81")
        from opecode in Opecodes
        let w = 0 != (opecode[0] & 0x01)
        from m in ModRegRm()
        from data1 in GetMemOrRegData(m.mod, m.rm, w)
        from data2 in GetMemoryDataIp_(data1.data.type)
        from ret in Calc(data1.data, data2, m.reg)
        from _ in SetMemOrRegData(data1.input, ret)
        select unit;

    static Dictionary<int, Accessor<CPU, bool>> PrefixStates =>
        new()
        {
            { 0x2E, _cs_prefix },
            { 0x26, _es_prefix },
            { 0x64, _fs_prefix },
            { 0x65, _gs_prefix },
            { 0x66, _operand_size_prefix },
            { 0x67, _address_size_prefix }
        };

    static OpecodeDic[] dicPrefixes =>
        [.. Enumerable.Range(0, 256).Select(
            index => PrefixStates.TryGetValue(index, out var acc) ?
                new OpecodeDic() { state = acc.Set(true) } :
                default
        )];

    static State<Unit> ClearPrefixes =>
        PrefixStates.Values
        .Select(acc => acc.Set(false))
        .Sequence()
        .Ignore();

    static (byte ope, int len, State<Unit> state)[] OneByteStates =>
    [
        (0x00, 6, Arithmetic),
        (0x08, 6, Arithmetic),
        (0x10, 6, Arithmetic),
        (0x18, 6, Arithmetic),
        (0x20, 6, Arithmetic),
        (0x28, 6, Arithmetic),
        (0x30, 6, Arithmetic),
        (0x38, 6, Arithmetic),
        (0x40, 8, Inc_40_47),
        (0x48, 8, Dec_48_4F),
        (0x50, 8, Push_50_57),
        (0x58, 8, Pop_58_5F),
        (0x68, 1, PushImm_68),
        (0x6A, 1, PushImm_6A),
        (0x70, 16, Jcc_70_7F),
        (0x80, 2, Group1_80_81),
        (0x83, 1, Group1_83),
        (0x84, 2, Test_84_85),
        (0x88, 4, Mov_88_8B),
        (0x8D, 1, Lea_8D),
        (0x8E, 1, Mov_8E),
        (0x90, 1, Nop_90),
        (0x91, 7, Xchg_91_97),
        (0x9C, 1, Pushf_9C),
        (0x9D, 1, Popf_9D),
        (0xA0, 2, Mov_A0_A1),
        (0xA2, 2, Mov_A2_A3),
        (0xA4, 2, Movs_A4_A5),
        (0xA6, 2, Cmps_A6_A7),
        (0xA8, 2, Test_A8_A9),
        (0xAA, 2, Stos_AA_AB),
        (0xAC, 2, Lods_AC_AD),
        (0xAE, 2, Scas_AE_AF),
        (0xB0, 16, Mov_B0_BF),
        (0xC0, 2, Group2_C0_C1),
        (0xC2, 1, Ret_C2),
        (0xC3, 1, Ret_C3),
        (0xC6, 2, Mov_C6_C7),
        (0xD0, 2, Group2_D0_D1),
        (0xD2, 2, Group2_D2_D3),
        (0xE4, 2, In_E4_E5),
        (0xE6, 2, Out_E6_E7),
        (0xE8, 1, Call_E8),
        (0xE9, 1, Jump_E9),
        (0xEA, 1, FarJump_EA),
        (0xEB, 1, Jmp_EB),
        (0xF2, 1, Repne_F2),
        (0xF3, 1, Rep_F3),
        (0xF6, 2, Group3_F6_F7),
        (0xF4, 1, Hlt_F4),
        (0xF5, 1, Cmc_F5),
        (0xF8, 1, Clc_F8),
        (0xF9, 1, Stc_F9),
        (0xFA, 1, Cli_FA),
        (0xFB, 1, Sti_FB),
        (0xFC, 1, Cld_FC),
        (0xFD, 1, Std_FD),
        (0xFE, 1, Group4_FE),
        (0xFF, 1, Group5_FF),
    ];

    static (byte ope1, byte ope2, int len, State<Unit> state)[] TwoBytesStates =>
    [
        (0x0F, 0x80, 0x10, Jcc_0F80_0F8F),
        (0x0F, 0x01, 0x01, Group7_0F01),
        (0x0F, 0x20, 0x01, Mov_0F20),
        (0x0F, 0x22, 0x01, Mov_0F22)
    ];

    static Dictionary<int, State<Unit>> oneByte =>
        OneByteStates
        .SelectMany(
            item =>
            Enumerable.Range(item.ope, item.len)
            .Select(ope => (ope, item.state))
        )
        .ToDictionary(
            item => item.ope,
            item => item.state
        );

    static Dictionary<int, Dictionary<int, State<Unit>>> twoBytes =>
        TwoBytesStates
        .ToLookup(item => item.ope1)
        .ToDictionary(
            item => (int)item.Key,
            item =>
                item.SelectMany(
                    item2 =>
                    Enumerable.Range(item2.ope2, item2.len)
                    .Select(ope => (ope, item2.state))
                )
                .ToDictionary(j => (int)j.ope, j => j.state)
            );

    static OpecodeDic[] dic =>
        [.. Enumerable.Range(0, 256).Select(
            index =>
                oneByte.TryGetValue(index, out var state) ? new OpecodeDic() { state = state } :
                twoBytes.TryGetValue(index, out var _dic) ? new OpecodeDic()
                {
                    next = Enumerable.Range(0, 256).Select(
                        index2 =>
                            _dic.TryGetValue(index2, out var state2) ?
                                new OpecodeDic() { state = state2 } :
                                default
                    ).ToArray()
                } : default
        )];

    static public State<Unit> CheckPrefix => (env, cpu1, ope) =>
    {
        var (IsSuccess1, op1, cpu2, log1) = GetMemoryDataIp8(env, cpu1, ope);

        if (!IsSuccess1)
        {
            return (false, default, cpu1, log1);
        }

        var data1 = dicPrefixes[op1];

        if (default != data1.state)
        {
            return data1.state(env, cpu2, [(byte)op1]);
        }

        return (false, default, cpu1, log1);
    };

    static public State<Unit> CheckPrefixes => CheckPrefix.Many0().Ignore();

    static public State<Unit> Execute => (env, cpu1, ope) =>
    {
        var (IsSuccess1, op1, cpu2, log1) = GetMemoryDataIp8(env, cpu1, ope);

        if (!IsSuccess1)
        {
            return (false, default, cpu1, log1);
        }

        var data1 = dic[op1];

        if (default != data1.state)
        {
            return data1.state(env, cpu2, [(byte)op1]);
        }

        if (default == data1.next)
        {
            return (false, default, cpu1, log1);
        }

        var (IsSuccess2, op2, cpu3, log2) = GetMemoryDataIp8(env, cpu2, ope);

        if (!IsSuccess2)
        {
            return (false, default, cpu1, log1);
        }

        var data2 = data1.next[op2];

        if (default != data2.state)
        {
            return data2.state(env, cpu3, [(byte)op1, (byte)op2]);
        }

        return (false, default, cpu1, log1 + log2);
    };

    static public State<Unit> Execute2 =>
        from _1 in CheckPrefixes
        from _2 in Execute
        from _3 in ClearPrefixes
        select unit;

    static void Main(string[] args)
    {
        var machine = Execute2.Many0();

        var init_cpu1 = new CPU();
        var init_cpu2 = _ip.setter(init_cpu1)(0xFFF0);
        var init_cpu3 = _cs.setter(init_cpu2)(0xF000);

        var env = new EmuEnvironment();

        var ret_ = machine(env, init_cpu3, default);
    }
}
