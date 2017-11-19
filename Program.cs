using System.Collections.Generic;
using System.Linq;
using static Emu86.CPU;
using static Emu86.Ext;
using static Emu86.Unit;

namespace Emu86
{
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

        static State<Unit> Mov_88_89 =>
            from _1 in SetLog("Mov_88_89")
            from opecode in Opecodes
            let w = 0 != (opecode[0] & 0x01)
            from m in ModRegRm()
            from data in GetMemOrRegData(m.mod, m.rm, w)
            from _2 in SetRegData(m.reg, data.data)
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

        static State<Unit> Out_E6_E7 =>
            from _1 in SetLog("Out_E6_E7")
            from opecode in Opecodes
            let w = 0 != (opecode[0] & 1)
            from port in GetMemoryDataIp8
            select unit;

        static State<Unit> In_E4_E5 =>
            from _1 in SetLog("In_E4_E5")
            from opecode in Opecodes
            let w = 0 != (opecode[0] & 1)
            from port in GetMemoryDataIp8
            from _2 in SetCpu(cpu => { if (w) { cpu.ax = 0; } else { cpu.al = 0; } return cpu; })
            select unit;

        static State<Unit> Lgdt(int mod, int rm) =>
            from addr in GetMemOrRegAddr(mod, rm)
            from dw in GetMemOrRegData16(addr)
            from dd in GetMemOrRegData16((addr.isMem, addr.addr + 2))
            from _ in SetCpu(cpu => { cpu.idt_base = dd; cpu.idt_limit = dw; return cpu; })
            select unit;

        static State<Unit> Lidt(int mod, int rm) =>
            from addr in GetMemOrRegAddr(mod, rm)
            from dw in GetMemOrRegData16(addr)
            from dd in GetMemOrRegData16((addr.isMem, addr.addr + 2))
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
            new Dictionary<int, Accessor<CPU, bool>>
            {
                { 0x2E,  _cs_prefix },
                { 0x26,  _es_prefix },
                { 0x64,  _fs_prefix },
                { 0x65,  _gs_prefix },
                { 0x66,  _operand_size_prefix },
                { 0x67,  _address_size_prefix }
            };

        static OpecodeDic[] dicPrefixes =>
            Enumerable.Range(0, 256).Select
            (
                index => PrefixStates.TryGetValue(index, out var acc) ?
                    new OpecodeDic() { state = acc.Set(true) } :
                    default(OpecodeDic)
            ).ToArray();

        static State<Unit> ClearPrefixes =>
            PrefixStates.Values
            .Select(acc => acc.Set(false))
            .Sequence()
            .Ignore();

        static (byte ope, int len, State<Unit> state)[] OneByteStates =>
            new(byte ope, int len, State<Unit> state)[]
            {
                (0x00, 6, Arithmetic),
                (0x08, 6, Arithmetic),
                (0x10, 6, Arithmetic),
                (0x18, 6, Arithmetic),
                (0x20, 6, Arithmetic),
                (0x28, 6, Arithmetic),
                (0x30, 6, Arithmetic),
                (0x38, 6, Arithmetic),
                (0x80, 2, Group1_80_81),
                (0x83, 1, Group1_83),
                (0x88, 2, Mov_88_89),
                (0x8E, 1, Mov_8E),
                (0xB0, 16, Mov_B0_BF),
                (0xE4, 2, In_E4_E5),
                (0xE6, 2, Out_E6_E7),
                (0xE9, 1, Jump_E9),
                (0xEA, 1, FarJump_EA),
                (0xFA, 1, Cli_FA),
                (0xFB, 1, Sti_FB),
                (0xFC, 1, Cld_FC),
                (0xFD, 1, Std_FD),
            };

        static (byte ope1, byte ope2, int len, State<Unit> state)[] TwoBytesStates =>
            new(byte ope1, byte ope2, int len, State<Unit> state)[]
            {
                (0x0F, 0x80, 0x10, Jcc_0F80_0F8F),
                (0x0F, 0x01, 0x01, Group7_0F01),
                (0x0F, 0x20, 0x01, Mov_0F20),
                (0x0F, 0x22, 0x01, Mov_0F22)
            };

        static Dictionary<int, State<Unit>> oneByte =>
            OneByteStates
            .SelectMany
            (
                item =>
                Enumerable.Range(item.ope, item.len)
                .Select(ope => (ope: ope, state: item.state))
            )
            .ToDictionary
            (
                item => item.ope,
                item => item.state
            );

        static Dictionary<int, Dictionary<int, State<Unit>>> twoBytes =>
            TwoBytesStates
            .ToLookup(item => item.ope1)
            .ToDictionary(
                item => (int)item.Key,
                item =>
                    item.SelectMany
                    (
                        item2 =>
                        Enumerable.Range(item2.ope2, item2.len)
                        .Select(ope => (ope: ope, state: item2.state))
                    )
                    .ToDictionary(j => (int)j.ope, j => j.state)
                );

        static OpecodeDic[] dic =>
            Enumerable.Range(0, 256)
                .Select
                (
                    index =>
                        oneByte.TryGetValue(index, out var state) ? new OpecodeDic() { state = state } :
                        twoBytes.TryGetValue(index, out var _dic) ? new OpecodeDic()
                        {
                            next = Enumerable.Range(0, 256).Select(
                                index2 =>
                                    _dic.TryGetValue(index2, out var state2) ?
                                        new OpecodeDic() { state = state2 } :
                                        default(OpecodeDic)
                            ).ToArray()
                        } : default(OpecodeDic)
                ).ToArray();

        static public State<Unit> CheckPrefix => (env, cpu1, ope) =>
        {
            var (IsSuccess1, op1, cpu2, log1) = GetMemoryDataIp8(env, cpu1, ope);

            if (!IsSuccess1)
            {
                return (false, default(Unit), cpu1, log1);
            }

            var data1 = dicPrefixes[op1];

            if (default(State<Unit>) != data1.state)
            {
                return data1.state(env, cpu2, new[] { (byte)op1 });
            }

            return (false, default(Unit), cpu1, log1);
        };

        static public State<Unit> CheckPrefixes => CheckPrefix.Many0().Ignore();

        static public State<Unit> Execute => (env, cpu1, ope) =>
        {
            var (IsSuccess1, op1, cpu2, log1) = GetMemoryDataIp8(env, cpu1, ope);

            if (!IsSuccess1)
            {
                return (false, default(Unit), cpu1, log1);
            }

            var data1 = dic[op1];

            if (default(State<Unit>) != data1.state)
            {
                return data1.state(env, cpu2, new[] { (byte)op1 });
            }

            if (default(OpecodeDic[]) == data1.next)
            {
                return (false, default(Unit), cpu1, log1);
            }

            var (IsSuccess2, op2, cpu3, log2) = GetMemoryDataIp8(env, cpu2, ope);

            if (!IsSuccess2)
            {
                return (false, default(Unit), cpu1, log1);
            }

            var data2 = data1.next[op2];

            if (default(State<Unit>) != data2.state)
            {
                return data2.state(env, cpu3, new[] { (byte)op1, (byte)op2 });
            }

            return (false, default(Unit), cpu1, log1 + log2);
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

            var ret_ = machine(env, init_cpu3, default(byte[]));
        }
    }
}
