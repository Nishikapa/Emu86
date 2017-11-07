using System;
using static Emu86.Ext;
using static System.Console;

namespace Emu86
{
    static class Program
    {
        static State<Unit> FarJump_EA =
                            from opecode in Opecode(0xEA)
                            from _1 in SetLog("FarJump_EA")
                            from offset in GetMemoryDataIp16
                            from segment in GetMemoryDataIp16
                            from _2 in SetCpu(cpu => { cpu.cs = segment; cpu.ip = offset; return cpu; })
                            select Unit.unit;

        static State<Unit> Jump_E9 =
                            from opecode in Opecode(0xE9)
                            from _1 in SetLog("Jump_E9")
                            from offset in GetMemoryDataIp16
                            from _2 in SetCpu(cpu => { cpu.ip = (ushort)(cpu.ip + (short)offset); return cpu; })
                            select Unit.unit;

        static State<Unit> Group1_83 =
                            from prefixes in Prefixes
                            from opecode in Opecode(0x83)
                            from _ in SetLog("Group1_83")
                            from d1 in ModRegRm()
                            from d2 in GetMemOrRegAddr(d1.mod, d1.rm, prefixes)
                            from d3 in GetMemoryDataIp8
                            from d4 in GetMemOrRegData(d2, prefixes, true)
                            from d5 in Calc(d4.type, d4.db, d4.dw, d4.dd, d3, (ushort)(sbyte)d3, (uint)(sbyte)d3, d1.reg)
                            from d6 in SetMemOrRegData(d2, d5)
                            select d6;

        static State<Unit> Mov_8E =
                            from prefixes in Prefixes
                            from opecode in Opecode(0x8E)
                            from _1 in SetLog("Mov_8E")
                            from m in ModRegRm()
                            from data in GetMemOrRegData(m.mod, m.rm, prefixes, true)
                            from _2 in SetSReg3(m.reg, data.data)
                            select Unit.unit;

        static State<Unit> Mov_0F20 =
                            from prefixes in Prefixes
                            from opecode in Opecode(0x0F, 0x20)
                            from _1 in SetLog("Mov_0F20")
                            from m in ModRegRm()
                            from _2 in SetResult(3 == m.mod)
                            from data in GetCrReg(m.reg)
                            from _3 in SetRegData32(m.rm, data)
                            select Unit.unit;

        static State<Unit> Mov_0F22 =
                            from prefixes in Prefixes
                            from opecode in Opecode(0x0F, 0x22)
                            from _1 in SetLog("Mov_0F22")
                            from m in ModRegRm()
                            from _2 in SetResult(3 == m.mod)
                            from data in GetRegData32(m.rm)
                            from _3 in SetCrReg(m.reg, data)
                            select Unit.unit;

        static State<Unit> Jcc_0F80_0F8F =
                            from opecode1 in Opecode(0x0f)
                            from opecode2 in Range(0x80, 0x8F)
                            from _1 in SetLog("Jcc_0F80_0F8F")
                            let type = opecode2 & 0xF
                            from f in Jcc(type)
                            from offset in GetMemoryDataIp16
                            from _2 in SetCpu(cpu => { cpu.ip = (ushort)(cpu.ip + (f ? (short)offset : 0)); return cpu; })
                            select Unit.unit;

        static State<Unit> Mov_B0_BF =
                            from prefixes in Prefixes
                            from opecode in Range(0xB0, 0xBF)
                            from _1 in SetLog("Mov_B0_BF")
                            let w = 0 != (opecode & 0x08)
                            let reg = opecode & 0x07
                            from data in GetMemoryDataIp(prefixes, w)
                            from _2 in SetRegData(reg, data.data)
                            select Unit.unit;

        static State<Unit> Mov_88_89 =
                            from prefixes in Prefixes
                            from opecode in Contains(0x88, 0x89)
                            from _1 in SetLog("Mov_88_89")
                            let w = 0 != (opecode & 0x01)
                            from m in ModRegRm()
                            from data in GetMemOrRegData(m.mod, m.rm, prefixes, w)
                            from _2 in SetRegData(m.reg, data.data)
                            select Unit.unit;

        static State<Unit> Arithmetic =
                            from prefixes in Prefixes
                            from opecode in Contains(
                                0x00, 0x01, 0x02, 0x03, 0x04, 0x05, // ADD
                                0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, // OR
                                0x10, 0x11, 0x12, 0x13, 0x14, 0x15, // ADC
                                0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, // SBB
                                0x20, 0x21, 0x22, 0x23, 0x24, 0x25, // AND
                                0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, // SUB
                                0x30, 0x31, 0x32, 0x33, 0x34, 0x35, // XOR
                                0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D  // CMP
                            )
                            from _1 in SetLog("Arithmetic")
                            let w = 0 != (opecode & 1)
                            let d = 0 != (opecode & 2)
                            let kind = (opecode >> 3) & 0x7
                            from m in ModRegRm()
                            from rmData in GetMemOrRegData(m.mod, m.rm, prefixes, w)
                            from regData in GetRegData(m.reg, rmData.data.type)
                            let d1 = d ? regData : rmData.data
                            let d2 = d ? rmData.data : regData
                            from ret in Calc(d1, d2, kind)
                            from _2 in d ? SetRegData(m.reg, ret) : SetMemOrRegData(rmData.input, ret)
                            select Unit.unit;

        static State<Unit> Cli_FA =
                            from opecode1 in Opecode(0xFA)
                            from _ in SetLog("Cli_FA")
                            select Unit.unit;

        static State<Unit> Sti_FB =
                            from opecode1 in Opecode(0xFB)
                            from _ in SetLog("Sti_FB")
                            select Unit.unit;

        static State<Unit> Cld_FC =
                            from opecode1 in Opecode(0xFC)
                            from _1 in SetLog("Cld_FC")
                            from _2 in SetCpu(cpu => { cpu.df = false; return cpu; })
                            select Unit.unit;

        static State<Unit> Std_FD =
                            from opecode in Opecode(0xFA)
                            from _1 in SetLog("Std_FD")
                            from _2 in SetCpu(cpu => { cpu.df = true; return cpu; })
                            select Unit.unit;

        static State<Unit> Out_E6_E7 =
                            from opecode in Range(0xE6, 0xE7)
                            from _1 in SetLog("Out_E6_E7")
                            let w = 0 != (opecode & 1)
                            from port in GetMemoryDataIp8
                            select Unit.unit;

        static State<Unit> In_E4_E5 =
                            from opecode in Range(0xE4, 0xE5)
                            from _1 in SetLog("In_E4_E5")
                            let w = 0 != (opecode & 1)
                            from port in GetMemoryDataIp8
                            from _2 in SetCpu(cpu => { if (w) { cpu.ax = 0; } else { cpu.al = 0; } return cpu; })
                            select Unit.unit;

        static State<Unit> Lgdt(int mod, int rm, (Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size) prefixes) =>
                            from addr in GetMemOrRegAddr(mod, rm, prefixes)
                            from dw in GetMemOrRegData16(addr)
                            from dd in GetMemOrRegData16((addr.isMem, addr.addr + 2))
                            from _ in SetCpu(cpu => { cpu.idt_base = dd; cpu.idt_limit = dw; return cpu; })
                            select Unit.unit;

        static State<Unit> Lidt(int mod, int rm, (Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size) prefixes) =>
                            from addr in GetMemOrRegAddr(mod, rm, prefixes)
                            from dw in GetMemOrRegData16(addr)
                            from dd in GetMemOrRegData16((addr.isMem, addr.addr + 2))
                            from _ in SetCpu(cpu => { cpu.idt_base = dd; cpu.idt_limit = dw; return cpu; })
                            select Unit.unit;

        static State<Unit> Group7_0F01 =
                            from prefixes in Prefixes
                            from opecode in Opecode(0x0F, 0x01)
                            from _1 in SetLog("Group7_0F01")
                            from m in ModRegRm()
                            from _ in Choice(
                                ((2 == m.reg), Lgdt(m.mod, m.rm, prefixes)),
                                ((3 == m.reg), Lidt(m.mod, m.rm, prefixes))
                            )
                            select Unit.unit;

        static State<Unit> Group1_80_81 =
                            from prefixes in Prefixes
                            from opecode in Contains(0x80, 0x81)
                            let w = 0 != (opecode & 0x01)
                            from _1 in SetLog("Group1_80_81")
                            from m in ModRegRm()
                            from data1 in GetMemOrRegData(m.mod, m.rm, prefixes, w)
                            from data2 in GetMemoryDataIp_(data1.data.type)
                            from ret in Calc(data1.data, data2, m.reg)
                            from _ in SetMemOrRegData(data1.input, ret)
                            select Unit.unit;

        static void Main(string[] args)
        {
            var all =
                Choice
                (
                    FarJump_EA,
                    Jump_E9,
                    Group1_83,
                    Group7_0F01,
                    Group1_80_81,
                    Jcc_0F80_0F8F,
                    Arithmetic,
                    Mov_8E,
                    Mov_B0_BF,
                    Mov_88_89,
                    Mov_0F20,
                    Mov_0F22,
                    Cli_FA,
                    Sti_FB,
                    Cld_FC,
                    Std_FD,
                    Out_E6_E7,
                    In_E4_E5
                );

            var machine = all.Many0();

            var init_cpu = new CPU()
            {
                cs = 0xF000,
                ip = 0xFFF0,
            };

            var env = new EmuEnvironment();

            var ret_ = machine(env, init_cpu);

            WriteLine(ret_.log);
        }
    }
}
