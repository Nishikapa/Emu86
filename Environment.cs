using System;
using System.Collections.Generic;
using System.Linq;
using static Emu86.CPU;

namespace Emu86
{
    static public partial class Ext
    {
        /// Mem /////////////////////////////////////
        static public IEnumerable<byte> EnvGetMemoryDatas(EmuEnvironment env, ushort segment, ushort offset)
        {
            var addr = GetMemoryAddr(segment, offset).addr;
            return env.OneMegaMemory_.Skip((int)addr);
        }

        static public IEnumerable<byte> EnvGetMemoryDatas(EmuEnvironment env, ushort segment, ushort offset, int length) =>
            EnvGetMemoryDatas(env, segment, offset).Take(length);

        static public (bool isMem, uint addr) GetMemoryAddr
            (
                ushort segment,
                ushort offset
            ) => (true, ((uint)(0x10 * segment + offset)));

        /// Reg /////////////////////////////////////
        static private Accessor<CPU, byte>[] ArrayReg8 => new Accessor<CPU, byte>[]
        {
            CPU._al,CPU._cl,CPU._dl,CPU._bl,
            CPU._ah,CPU._ch,CPU._dh,CPU._bh
        };
        static private Accessor<CPU, ushort>[] ArrayReg16 => new Accessor<CPU, ushort>[]
        {
            CPU._ax,CPU._cx,CPU._dx,CPU._bx,
            CPU._sp,CPU._bp,CPU._si,CPU._di
        };
        static private Accessor<CPU, uint>[] ArrayReg32 => new Accessor<CPU, uint>[]
        {
            CPU._eax,CPU._ecx,CPU._edx,CPU._ebx,
            CPU._esp,CPU._ebp,CPU._esi,CPU._edi
        };
        static private Accessor<CPU, ushort>[] ArraySreg => new Accessor<CPU, ushort>[]
        {
            CPU._es,CPU._cs,CPU._ss,CPU._ds,CPU._fs,CPU._gs
        };

        static private Func<int, Func<CPU, ushort>> GetSReg3 => EnvGetDataFromCPU(ArraySreg);
        static public Func<byte, Func<int, Func<CPU, CPU>>> EnvSetRegData8 => EnvSetDataFromCPU(ArrayReg8);
        static public Func<ushort, Func<int, Func<CPU, CPU>>> EnvSetRegData16 => EnvSetDataFromCPU(ArrayReg16);
        static public Func<uint, Func<int, Func<CPU, CPU>>> EnvSetRegData32 => EnvSetDataFromCPU(ArrayReg32);

        static public Func<ushort, Func<int, Func<CPU, CPU>>> EnvSetSReg3 => EnvSetDataFromCPU(ArraySreg);

        static public State<Unit> SetRegData8(int reg, byte db) =>
            SetCpu(EnvSetRegData8(db)(reg));

        static public State<Unit> SetRegData16(int reg, ushort dw) =>
            SetCpu(EnvSetRegData16(dw)(reg));

        static public State<Unit> SetRegData32(int reg, uint dd) =>
            SetCpu(EnvSetRegData32(dd)(reg));

        static public State<Unit> SetCrReg(int reg, uint data) =>
            Choice(
                reg,
                (0, _cr0),
                (2, _cr2),
                (3, _cr3)
            ).SetCpu(data);

        static public State<Unit> SetRegData(int reg, (int type, byte db, ushort dw, uint dd) data) =>
            SetCpu(EnvSetRegData(data)(reg));

        static public State<uint> GetRegData32(int reg) =>
            GetDataFromCpu(EnvGetDataFromCPU(ArrayReg32)(reg));

        static public State<(int type, byte db, ushort dw, uint dd)> GetRegData(int reg, int type) =>
            GetDataFromCpu(GetTypeData_<CPU>(
                type,
                EnvGetDataFromCPU(ArrayReg8)(reg),
                EnvGetDataFromCPU(ArrayReg16)(reg),
                EnvGetDataFromCPU(ArrayReg32)(reg)
                )
            );

        static public State<uint> GetCrReg(int reg) =>
            Choice(
                reg,
                (0, Get(_cr0)),
                (2, Get(_cr2)),
                (3, Get(_cr3))
            );

        static public State<Unit> SetSReg3(int reg, (int type, byte db, ushort dw, uint dd) data) =>
            SetCpu(
                cpu =>
                {
                    var (type, db, dw, dd) = data;
                    switch (type)
                    {
                        case 0:
                        case 1:
                            cpu = EnvSetSReg3(dw)(reg)(cpu);
                            break;
                        case 2:
                            throw new NotImplementedException();
                        default:
                            throw new Exception();
                    }
                    return cpu;
                }
            );

        /// MemReg //////////////////////////////////
        static private (bool isMem, uint addr, int inc) EnvGetMemOrRegAddr16_(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp)
        {
            var segment_base =
                segment == default(ushort?) ?
                ((uint)cpu.ds) * 0x10 :
                ((uint)segment) * 0x10;

            var ss_base = ((uint)cpu.ss) * 0x10;

            switch (mod)
            {
                case 0:
                    {
                        (Func<uint> func, int inc)[] array = {
                                (() => segment_base + cpu.bx + cpu.si, 0),                                              // DS:[BX+SI]
                                (() => segment_base + cpu.bx + cpu.di, 0),                                              // DS:[BX+DI]
                                (() => segment_base + cpu.bp + cpu.si, 0),                                              // DS:[BP+SI]
                                (() => segment_base + cpu.bp + cpu.di, 0),                                              // DS:[BP+DI]
                                (() => segment_base + cpu.si, 0),                                                       // DS:[SI]
                                (() => segment_base + cpu.di, 0),                                                       // DS:[DI]
                                (() => segment_base + (uint)disp.ElementAt(0) + 0x100 * (uint)disp.ElementAt(1), 2),    // DS:[d16]
                                (() => segment_base + cpu.bx, 0),                                                       // DS:[BX]
                        };
                        var (func, inc) = array[rm];
                        return (true, func(), inc);
                    }
                case 1:
                    {
                        var d8 = (int)(sbyte)disp.ElementAt(0);
                        Func<uint>[] array =
                        {
                            ()=> (uint)(segment_base + cpu.bx + cpu.si + d8),   // DS:[BX+SI+d8]
                            ()=> (uint)(segment_base + cpu.bx + cpu.di + d8),   // DS:[BX+DI+d8]
                            ()=> (uint)(segment_base + cpu.bp + cpu.si + d8),   // DS:[BP+SI+d8]
                            ()=> (uint)(segment_base + cpu.bp + cpu.di + d8),   // DS:[BP+DI+d8]
                            ()=> (uint)(segment_base + cpu.si + d8),            // DS:[SI+d8]
                            ()=> (uint)(segment_base + cpu.di + d8),            // DS:[DI+d8]
                            ()=> (uint)(ss_base + cpu.bp + d8),                 // SS:[bp+d8]
                            ()=> (uint)(segment_base + cpu.di + d8),            // DS:[DI+d8]
                        };
                        return (true, array[rm](), 1);
                    }
                case 2:
                    {
                        var d16 = (uint)disp.ElementAt(0) + 0x100 * (uint)disp.ElementAt(1);
                        Func<uint>[] array =
                        {
                            () => (uint)(segment_base + cpu.bx + cpu.si + d16),     // DS:[BX+SI+d16]
                            ()=>  (uint)(segment_base + cpu.bx + cpu.di + d16),     // DS:[BX+DI+d16]
                            ()=>  (uint)(segment_base + cpu.bp + cpu.si + d16),     // DS:[BP+SI+d16]
                            ()=>  (uint)(segment_base + cpu.bp + cpu.di + d16),     // DS:[BP+DI+d16]
                            ()=>  (uint)(segment_base + cpu.si + d16),              // DS:[SI+d16]
                            ()=>  (uint)(segment_base + cpu.di + d16),              // DS:[DI+d16]
                            ()=>  (uint)(ss_base + cpu.bp + d16),                   // SS:[bp+d16]
                            ()=>  (uint)(segment_base + cpu.di + d16),              // DS:[DI+d16]
                        };
                        return (true, array[rm](), 2);
                    }
                case 3:
                    return (false, (uint)rm, 0);
                default:
                    throw new Exception();
            }
        }
        static private (bool isMem, uint addr, int inc) EnvGetMemOrRegAddr32_(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp)
        {
            var segment_base =
                segment == default(ushort?) ?
                ((uint)cpu.ds) * 0x10 :
                ((uint)segment) * 0x10;

            var ss_base = ((uint)cpu.ss) * 0x10;

            int inc = 0;

            switch (mod)
            {
                case 0:
                    {
                        uint addr = 0;
                        switch (rm)
                        {
                            case 0: // DS:[EAX]
                                addr = segment_base + cpu.eax;
                                break;
                            case 1: // DS:[ECX]
                                addr = segment_base + cpu.ecx;
                                break;
                            case 2: // DS:[EDX]
                                addr = segment_base + cpu.edx;
                                break;
                            case 3: // DS:[EBX]
                                addr = segment_base + cpu.ebx;
                                break;
                            case 4: // 
                                {
                                    var sib = (int)(sbyte)disp.ElementAt(0);

                                    var ss = ((sib >> 6) & 0x3);
                                    var indexf = ((sib >> 3) & 0x7);
                                    var basef = (sib & 0x7);

                                    var base_ = EnvGetDataFromCPU(ArrayReg32)(basef)(cpu);

                                    var index_ = EnvGetIndexRegData32(cpu, indexf);

                                    addr = (uint)(base_ + (1 << ss) * index_);

                                    inc = 1;
                                }
                                break;
                            case 5: // DS:[d32]
                                {
                                    addr = segment_base + disp.ToUint32();
                                    inc = 4;
                                }
                                break;
                            case 6: // DS:[ESI]
                                addr = segment_base + cpu.esi;
                                break;
                            case 7: // DS:[EDI]
                                addr = segment_base + cpu.edi;
                                break;
                            default:
                                throw new Exception();
                        }
                        return (true, addr, inc);
                    }
                case 1:
                    {
                        uint addr = 0;
                        var d8 = (int)(sbyte)disp.ElementAt(0);
                        switch (rm)
                        {
                            case 0: // DS:[EAX+d8]
                                addr = (uint)(segment_base + cpu.eax + d8);
                                break;
                            case 1: // DS:[ECX+d8]
                                addr = (uint)(segment_base + cpu.ecx + d8);
                                break;
                            case 2: // DS:[EDX+d8]
                                addr = (uint)(segment_base + cpu.edx + d8);
                                break;
                            case 3: // DS:[EBX+d8]
                                addr = (uint)(segment_base + cpu.ebx + d8);
                                break;
                            case 4: // SS:[EBP+d8] 
                                {
                                    var sib = (int)(sbyte)disp.ElementAt(0);

                                    var ss = ((sib >> 6) & 0x3);
                                    var indexf = ((sib >> 3) & 0x7);
                                    var basef = (sib & 0x7);

                                    var base_ = EnvGetDataFromCPU(ArrayReg32)(basef)(cpu);

                                    var index_ = EnvGetIndexRegData32(cpu, indexf);

                                    d8 = (int)(sbyte)disp.ElementAt(1);

                                    addr = (uint)(base_ + (1 << ss) * index_ + d8);

                                    return (true, addr, 2);
                                }
                            case 5: // SS:[EBP+d8]
                                addr = (uint)(ss_base + cpu.ebp + d8);
                                break;
                            case 6: // DS:[ESI+d8]
                                addr = (uint)(segment_base + cpu.esi + d8);
                                break;
                            case 7: // DS:[EDI+d8]
                                addr = (uint)(segment_base + cpu.edi + d8);
                                break;
                            default:
                                throw new Exception();
                        }
                        return (true, addr, 1);
                    }
                case 2:
                    {
                        uint addr = 0;

                        var d32 = disp.ToUint32();

                        switch (rm)
                        {
                            case 0: // DS:[EAX+d32]
                                addr = (uint)(segment_base + cpu.eax + d32);
                                break;
                            case 1: // DS:[ECX+d32]
                                addr = (uint)(segment_base + cpu.ecx + d32);
                                break;
                            case 2: // DS:[EDX+d32]
                                addr = (uint)(segment_base + cpu.edx + d32);
                                break;
                            case 3: // DS:[EBX+d32]
                                addr = (uint)(segment_base + cpu.ebx + d32);
                                break;
                            case 4: // SS:[ESP+d32]
                                throw new NotImplementedException();
                                addr = (uint)(ss_base + cpu.esp + d32);
                                break;
                            case 5: // SS:[EBP+d32]
                                addr = (uint)(ss_base + cpu.ebp + d32);
                                break;
                            case 6: // DS:[ESI+d16]
                                addr = (uint)(segment_base + cpu.esi + d32);
                                break;
                            case 7: // DS:[EDI+d32]
                                addr = (uint)(segment_base + cpu.edi + d32);
                                break;
                            default:
                                throw new Exception();
                        }
                        return (true, addr, 4);
                    }
                case 3:
                    return (false, (uint)rm, 0);
                default:
                    throw new Exception();
            }
        }
        static public State<(bool isMem, uint addr)> GetMemOrRegAddr(int mod, int rm) =>
            from address_size in _address_size_prefix.Get()
            from data in GetDataFromEnvCpu(
                (env, cpu) =>
                address_size ?
                EnvGetMemOrRegAddr32_(cpu, mod, rm, cpu.cs, EnvGetMemoryDatas(env, cpu.cs, cpu.ip)) :
                EnvGetMemOrRegAddr16_(cpu, mod, rm, cpu.cs, EnvGetMemoryDatas(env, cpu.cs, cpu.ip))
            )
            from _ in IpInc(data.inc)
            select (data.isMem, data.addr);

        static public State<byte> GetMemOrRegData8((bool isMem, uint addr) t) =>
            GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData8(env, t.addr) : EnvGetDataFromCPU(ArrayReg8)((int)t.addr)(cpu));

        static public State<ushort> GetMemOrRegData16((bool isMem, uint addr) t) =>
            GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData16(env, t.addr) : EnvGetDataFromCPU(ArrayReg16)((int)t.addr)(cpu));

        static public State<uint> GetMemOrRegData32((bool isMem, uint addr) t) =>
            GetDataFromEnvCpu((env, cpu) => t.isMem ? EnvGetMemoryData32(env, t.addr) : EnvGetDataFromCPU(ArrayReg32)((int)t.addr)(cpu));

        static Func<int, Func<CPU, CPU>> EnvSetRegData((int type, byte db, ushort dw, uint dd) data) =>
            SetTypeData<Func<int, Func<CPU, CPU>>>
            (
                data,
                EnvSetRegData8,
                EnvSetRegData16,
                EnvSetRegData32
            );

        static Action<EmuEnvironment, uint> EnvSetMemoryData((int type, byte db, ushort dw, uint dd) data) =>
            SetTypeData<Action<EmuEnvironment, uint>>
            (
                data,
                db => (env, addr) => { env.OneMegaMemory_[addr] = db; },
                dw => (env, addr) => { EnvSetMemoryDatas(env, addr, dw.ToByteArray()); },
                dd => (env, addr) => { EnvSetMemoryDatas(env, addr, dd.ToByteArray()); }
            );

        static public State<Unit> SetMemOrRegData(
            (bool isMem, uint addr) t,
            (int type, byte db, ushort dw, uint dd) data) =>
            SetCpu((env, cpu) =>
            {
                if (t.isMem)
                {
                    EnvSetMemoryData(data)(env, t.addr);
                    return cpu;
                }
                else
                {
                    return EnvSetRegData(data)((int)t.addr)(cpu);
                }
            });

        static public State<(int type, byte db, ushort dw, uint dd)> GetMemOrRegData
        (
            (bool isMem, uint addr) t,
            Boolean w) =>
            from operand_size in _operand_size_prefix.Get()
            from ret in Choice(
                w ? (operand_size ? 2 : 1) : 0,
                GetMemOrRegData8(t),
                GetMemOrRegData16(t),
                GetMemOrRegData32(t)
            )
            select ret;

        static public State<((int type, byte db, ushort dw, uint dd) data, (bool isMem, uint addr) input)> GetMemOrRegData
            (
                ushort segment,
                ushort offset,
                Boolean w
            ) =>
            from addr in GetMemoryAddr(segment, offset).ToState()
            from data in GetMemOrRegData(addr, w)
            select (data, addr);

        static public State<((int type, byte db, ushort dw, uint dd) data, (bool isMem, uint addr) input)> GetMemOrRegData
            (
                int mod,
                int rm,
                Boolean w
            ) =>
            from addr in GetMemOrRegAddr(mod, rm)
            from data in GetMemOrRegData(addr, w)
            select (data, addr);

        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        static private uint EnvGetIndexRegData32(CPU cpu, int reg) =>
            new uint[]
            {
                cpu.eax,
                cpu.ecx,
                cpu.edx,
                cpu.ebx,
                0,
                cpu.ebp,
                cpu.esi,
                cpu.edi,
            }[reg];

        static private Func<T, Func<int, Func<CPU, CPU>>> EnvSetDataFromCPU<T>(Accessor<CPU, T>[] array) =>
            data => reg => cpu => array[reg].setter(cpu)(data);

        static private void EnvSetMemoryDatas(EmuEnvironment env, uint addr, byte[] data)
        {
            for (int i = 0; i < data.Length; ++i)
            {
                env.OneMegaMemory_[addr + i] = data[i];
            }
        }

        static private byte EnvGetMemoryData8(EmuEnvironment env, uint addr) => env.OneMegaMemory_[addr];
        static private ushort EnvGetMemoryData16(EmuEnvironment env, uint addr) => env.OneMegaMemory_.Skip((int)addr).ToUint16();
        static private uint EnvGetMemoryData32(EmuEnvironment env, uint addr) => env.OneMegaMemory_.Skip((int)addr).ToUint32();
    }

    public class EmuEnvironment
    {
        public EmuEnvironment()
        {
            // bios
            {
                var biosdata = Properties.Resources.bios;
                Array.Copy(biosdata, 0, this.OneMegaMemory_, 0x100000 - biosdata.Length, biosdata.Length);
            }
        }

        public byte[] OneMegaMemory_ = new byte[1024 * 1024];
    }
}
