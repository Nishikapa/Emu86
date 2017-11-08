using System;
using System.Collections.Generic;
using System.Linq;

namespace Emu86
{
    static public partial class Ext
    {
        static public IEnumerable<byte> EnvGetMemoryDatas(EmuEnvironment env, ushort segment, ushort offset)
        {
            var addr = ((uint)segment) * 0x10 + offset;
            return env.OneMegaMemory_.Skip((int)addr);
        }
        static public IEnumerable<byte> EnvGetMemoryDatas(EmuEnvironment env, ushort segment, ushort offset, int length) => EnvGetMemoryDatas(env, segment, offset).Take(length);

        static public byte EnvGetMemoryData8(EmuEnvironment env, ushort segment, ushort offset) => EnvGetMemoryData8(env, ((uint)segment) * 0x10 + offset);
        static public ushort EnvGetMemoryData16(EmuEnvironment env, ushort segment, ushort offset) => EnvGetMemoryData16(env, ((uint)segment) * 0x10 + offset);
        static public uint EnvGetMemoryData32(EmuEnvironment env, ushort segment, ushort offset) => EnvGetMemoryData32(env, ((uint)segment) * 0x10 + offset);

        static public (bool isMem, uint addr, int inc) EnvGetMemOrRegAddr(EmuEnvironment env, CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, bool address_size)
            => address_size ?
            EnvGetMemOrRegAddr32_(cpu, mod, rm, segment, disp) :
            EnvGetMemOrRegAddr16_(cpu, mod, rm, segment, disp);

        static public byte EnvGetMemOrRegData8_(EmuEnvironment env, bool isMem, uint addr, CPU cpu) => isMem ? EnvGetMemoryData8(env, addr) : EnvGetRegData8(cpu, (int)addr);
        static public ushort EnvGetMemOrRegData16_(EmuEnvironment env, bool isMem, uint addr, CPU cpu) => isMem ? EnvGetMemoryData16(env, addr) : EnvGetRegData16(cpu, (int)addr);
        static public uint EnvGetMemOrRegData32_(EmuEnvironment env, bool isMem, uint addr, CPU cpu) => isMem ? EnvGetMemoryData32(env, addr) : EnvGetRegData32(cpu, (int)addr);

        static public CPU EnvSetMemOrRegData8_(EmuEnvironment env, bool isMem, uint addr, CPU cpu, byte data)
        {
            if (isMem)
            {
                EnvSetMemoryData8(env, addr, data);
                return cpu;
            }
            else
            {
                return EnvSetRegData8(cpu, (int)addr, data);
            }
        }
        static public CPU EnvSetMemOrRegData16_(EmuEnvironment env, bool isMem, uint addr, CPU cpu, ushort data)
        {
            if (isMem)
            {
                EnvSetMemoryData16(env, addr, data);
                return cpu;
            }
            else
            {
                return EnvSetRegData16(cpu, (int)addr, data);
            }
        }
        static public CPU EnvSetMemOrRegData32_(EmuEnvironment env, bool isMem, uint addr, CPU cpu, uint data)
        {
            if (isMem)
            {
                EnvSetMemoryData32(env, addr, data);
                return cpu;
            }
            else
            {
                return EnvSetRegData32(cpu, (int)addr, data);
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        static private uint EnvGetIndexRegData32(CPU cpu, int reg)
        {
            switch (reg)
            {
                case 0: return cpu.eax;
                case 1: return cpu.ecx;
                case 2: return cpu.edx;
                case 3: return cpu.ebx;
                case 4: return 0;
                case 5: return cpu.ebp;
                case 6: return cpu.esi;
                case 7: return cpu.edi;
                default: throw new Exception();
            }
        }
        static private Func<CPU, int, T, CPU> EnvSetDataFromCPU<T>(Accessor<CPU, T>[] array) => (cpu, reg, data) => array.ElementAt(reg).setter(cpu)(data);
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
                        var (func, inc) = array.ElementAt(rm);
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
                        return (true, array.ElementAt(rm)(), 1);
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
                        return (true, array.ElementAt(rm)(), 2);
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

                                    var base_ = EnvGetRegData32(cpu, basef);

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

                                    var base_ = EnvGetRegData32(cpu, basef);

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
        static private void EnvSetMemoryData16(EmuEnvironment env, uint addr, ushort data) => EnvSetMemoryDatas(env, addr, data.ToByteArray());
        static private void EnvSetMemoryData32(EmuEnvironment env, uint addr, uint data) => EnvSetMemoryDatas(env, addr, data.ToByteArray());
        static private (byte, int) EnvGetMemOrRegData8(EmuEnvironment env, CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, bool address_size)
        {
            var (isMem, addr, inc) = EnvGetMemOrRegAddr(env, cpu, mod, rm, segment, disp, address_size);
            return (EnvGetMemOrRegData8_(env, isMem, addr, cpu), inc);
        }
        static private (CPU, int) EnvSetMemOrRegData8(EmuEnvironment env, CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, byte data, bool address_size)
        {
            var (isMem, addr, inc) = EnvGetMemOrRegAddr(env, cpu, mod, rm, segment, disp, address_size);
            return (EnvSetMemOrRegData8_(env, isMem, addr, cpu, data), inc);
        }
        static private (ushort, int) EnvGetMemOrRegData16(EmuEnvironment env, CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, bool address_size)
        {
            var (isMem, addr, inc) = EnvGetMemOrRegAddr(env, cpu, mod, rm, segment, disp, address_size);
            return (EnvGetMemOrRegData16_(env, isMem, addr, cpu), inc);
        }
        static private (CPU, int) EnvSetMemOrRegData16(EmuEnvironment env, CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, ushort data, bool address_size)
        {
            var (isMem, addr, inc) = EnvGetMemOrRegAddr(env, cpu, mod, rm, segment, disp, address_size);
            return (EnvSetMemOrRegData16_(env, isMem, addr, cpu, data), inc);
        }
        static private (uint, int) EnvGetMemOrRegData32(EmuEnvironment env, CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, bool address_size)
        {
            var (isMem, addr, inc) = EnvGetMemOrRegAddr(env, cpu, mod, rm, segment, disp, address_size);
            return (EnvGetMemOrRegData32_(env, isMem, addr, cpu), inc);
        }
        static private (CPU, int) EnvSetMemoryData32(EmuEnvironment env, CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, uint data, bool address_size)
        {
            var (isMem, addr, inc) = EnvGetMemOrRegAddr(env, cpu, mod, rm, segment, disp, address_size);
            return (EnvSetMemOrRegData32_(env, isMem, addr, cpu, data), inc);
        }
        static private ushort EnvGetMemoryData16(EmuEnvironment env, uint addr) => (ushort)(EnvGetMemoryData8(env, (uint)(addr + 1)) * 0x100 + EnvGetMemoryData8(env, (uint)addr));
        static private void EnvSetMemoryDatas(EmuEnvironment env, uint addr, byte[] data)
        {
            for (int i = 0; i < data.Length; ++i)
            {
                EnvSetMemoryData8(env, (uint)(addr + i), data[i]);
            }
        }
        static private byte EnvGetMemoryData8(EmuEnvironment env, uint addr) => env.OneMegaMemory_[addr];
        static private void EnvSetMemoryData8(EmuEnvironment env, uint addr, byte data)
        {
            env.OneMegaMemory_[addr] = data;
        }
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
