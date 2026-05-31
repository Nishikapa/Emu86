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

    // REP が前置できる文字列命令のオペコード集合。
    static readonly HashSet<int> stringOps = [0xA4, 0xA5, 0xA6, 0xA7, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF];

    // REP (0xF3): 後続の文字列命令を CX 回繰り返す。CX==0 なら 1 回も実行しない。
    static State<Unit> Rep_F3 => (env, cpu1, ope) =>
    {
        var (ok, op, cpu2, log) = GetMemoryDataIp8(env, cpu1, ope);
        if (!ok)
            return (false, default, cpu1, log);

        if (!stringOps.Contains(op) || !oneByte.TryGetValue(op, out var state))
            return (false, default, cpu1, log);

        var cpu = cpu2;
        while (_cx.getter(cpu) != 0)
        {
            var (ok2, _, cpuN, _) = state(env, cpu, [(byte)op]);
            if (!ok2)
                return (false, default, cpu1, log);
            cpu = _cx.setter(cpuN)((ushort)(_cx.getter(cpuN) - 1));
        }
        return (true, unit, cpu, log);
    };

    static State<Unit> Lea_8D =>
        from _1 in SetLog("Lea_8D")
        from m in ModRegRm()
        // LEA はメモリを読まず、実効アドレス(オフセット)そのものを reg へ書く。
        from addr in GetMemOrRegAddr(m.mod, m.rm)
        from _2 in SetRegData16(m.reg, (ushort)addr.addr)
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
        (0xA4, 2, Movs_A4_A5),
        (0xA8, 2, Test_A8_A9),
        (0xAA, 2, Stos_AA_AB),
        (0xAC, 2, Lods_AC_AD),
        (0xB0, 16, Mov_B0_BF),
        (0xC2, 1, Ret_C2),
        (0xC3, 1, Ret_C3),
        (0xE4, 2, In_E4_E5),
        (0xE6, 2, Out_E6_E7),
        (0xE8, 1, Call_E8),
        (0xE9, 1, Jump_E9),
        (0xEA, 1, FarJump_EA),
        (0xEB, 1, Jmp_EB),
        (0xF3, 1, Rep_F3),
        (0xF4, 1, Hlt_F4),
        (0xF5, 1, Cmc_F5),
        (0xF8, 1, Clc_F8),
        (0xF9, 1, Stc_F9),
        (0xFA, 1, Cli_FA),
        (0xFB, 1, Sti_FB),
        (0xFC, 1, Cld_FC),
        (0xFD, 1, Std_FD),
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
