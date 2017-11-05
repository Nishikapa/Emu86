using System;
using System.Collections.Generic;
using System.Linq;

namespace Emu86
{
    public class EmuEnvironment
    {
        public EmuEnvironment()
        {
            // bios
            {
                var biosdata = Properties.Resources.bios;
                Array.Copy(biosdata, 0, OneMegaMemory_, 0x100000 - biosdata.Length, biosdata.Length);
            }
        }

        byte[] OneMegaMemory_ = new byte[1024 * 1024];

        public IEnumerable<byte> GetMemoryDatas(ushort segment, ushort offset)
        {
            var addr = ((uint)segment) * 0x10 + offset;
            return this.OneMegaMemory_.Skip((int)addr);
        }
        public IEnumerable<byte> GetMemoryDatas(ushort segment, ushort offset, int length) => GetMemoryDatas(segment, offset).Take(length);

        public byte GetMemoryData8(uint addr) => this.OneMegaMemory_[addr];

        public byte GetMemoryData8(ushort segment, ushort offset) => GetMemoryData8(((uint)segment) * 0x10 + offset);

        public void SetMemoryData8(uint addr, byte data)
        {
            OneMegaMemory_[addr] = data;
        }
        public ushort GetMemoryData16(uint addr) => (ushort)(OneMegaMemory_[addr + 1] * 0x100 + OneMegaMemory_[addr]);

        public ushort GetMemoryData16(ushort segment, ushort offset) => GetMemoryData16(((uint)segment) * 0x10 + offset);

        public void SetMemoryDatas(uint addr, byte[] data)
        {
            for (int i = 0; i < data.Length; ++i)
            {
                this.OneMegaMemory_[addr + i] = data[i];
            }
        }

        public void SetMemoryData16(uint addr, ushort data) => SetMemoryDatas(addr, data.ToByteArray());

        public uint GetMemoryData32(uint addr) => this.OneMegaMemory_.Skip((int)addr).ToUint32();

        public uint GetMemoryData32(ushort segment, ushort offset) => GetMemoryData32(((uint)segment) * 0x10 + offset);

        public void SetMemoryData32(uint addr, uint data) => SetMemoryDatas(addr, data.ToByteArray());

        private (bool isMem, uint addr, int inc) GetMemoryAddr16_(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp)
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
                        (var func, var inc) = array.ElementAt(rm);
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
        private (bool isMem, uint addr, int inc) GetMemoryAddr32_(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp)
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

                                    var base_ = GetRegData32(cpu, basef);

                                    var index_ = GetIndexRegData32(cpu, indexf);

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

                                    var base_ = GetRegData32(cpu, basef);

                                    var index_ = GetIndexRegData32(cpu, indexf);

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

        public (bool isMem, uint addr, int inc) GetMemoryAddr(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, bool address_size)
            => address_size ?
            GetMemoryAddr32_(cpu, mod, rm, segment, disp) :
            GetMemoryAddr16_(cpu, mod, rm, segment, disp);

        public byte GetMemOrRegData8_(bool isMem, uint addr, CPU cpu) => isMem ? GetMemoryData8(addr) : GetRegData8(cpu, (int)addr);
        public (byte, int) GetMemOrRegData8(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, bool address_size)
        {
            (var isMem, var addr, var inc) = GetMemoryAddr(cpu, mod, rm, segment, disp, address_size);
            return (GetMemOrRegData8_(isMem, addr, cpu), inc);
        }
        public CPU SetMemOrRegData8_(bool isMem, uint addr, CPU cpu, byte data)
        {
            if (isMem)
            {
                SetMemoryData8(addr, data);
                return cpu;
            }
            else
            {
                return SetRegData8(cpu, (int)addr, data);
            }
        }
        public (CPU, int) SetMemOrRegData8(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, byte data, bool address_size)
        {
            (var isMem, var addr, var inc) = GetMemoryAddr(cpu, mod, rm, segment, disp, address_size);
            return (SetMemOrRegData8_(isMem, addr, cpu, data), inc);
        }

        public ushort GetMemOrRegData16_(bool isMem, uint addr, CPU cpu) => isMem ? GetMemoryData16(addr) : GetRegData16(cpu, (int)addr);

        public (ushort, int) GetMemOrRegData16(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, bool address_size)
        {
            (var isMem, var addr, var inc) = GetMemoryAddr(cpu, mod, rm, segment, disp, address_size);
            return (GetMemOrRegData16_(isMem, addr, cpu), inc);
        }
        public CPU SetMemOrRegData16_(bool isMem, uint addr, CPU cpu, ushort data)
        {
            if (isMem)
            {
                SetMemoryData16(addr, data);
                return cpu;
            }
            else
            {
                return SetRegData16(cpu, (int)addr, data);
            }
        }
        public (CPU, int) SetMemOrRegData16(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, ushort data, bool address_size)
        {
            (var isMem, var addr, var inc) = GetMemoryAddr(cpu, mod, rm, segment, disp, address_size);
            return (SetMemOrRegData16_(isMem, addr, cpu, data), inc);
        }
        public uint GetMemOrRegData32_(bool isMem, uint addr, CPU cpu) => isMem ? GetMemoryData32(addr) : GetRegData32(cpu, (int)addr);
        public (uint, int) GetMemOrRegData32(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, bool address_size)
        {
            (var isMem, var addr, var inc) = GetMemoryAddr(cpu, mod, rm, segment, disp, address_size);
            return (GetMemOrRegData32_(isMem, addr, cpu), inc);
        }
        public CPU SetMemOrRegData32_(bool isMem, uint addr, CPU cpu, uint data)
        {
            if (isMem)
            {
                SetMemoryData32(addr, data);
                return cpu;
            }
            else
            {
                return SetRegData32(cpu, (int)addr, data);
            }
        }
        public (CPU, int) SetMemoryData32(CPU cpu, int mod, int rm, ushort? segment, IEnumerable<byte> disp, uint data, bool address_size)
        {
            (var isMem, var addr, var inc) = GetMemoryAddr(cpu, mod, rm, segment, disp, address_size);
            return (SetMemOrRegData32_(isMem, addr, cpu, data), inc);
        }

        static (Func<CPU, byte> getter, Func<CPU, byte, CPU> setter)[] ArrayReg8 = new(Func<CPU, byte> getter, Func<CPU, byte, CPU> setter)[]
        {
            (cpu=>cpu.al,(cpu,data)=>{cpu.al=data; return cpu; }),
            (cpu=>cpu.cl,(cpu,data)=>{cpu.cl=data; return cpu; }),
            (cpu=>cpu.dl,(cpu,data)=>{cpu.dl=data; return cpu; }),
            (cpu=>cpu.bl,(cpu,data)=>{cpu.bl=data; return cpu; }),
            (cpu=>cpu.ah,(cpu,data)=>{cpu.ah=data; return cpu; }),
            (cpu=>cpu.ch,(cpu,data)=>{cpu.ch=data; return cpu; }),
            (cpu=>cpu.dh,(cpu,data)=>{cpu.dh=data; return cpu; }),
            (cpu=>cpu.bh,(cpu,data)=>{cpu.bh=data; return cpu; }),
        };

        public static Func<CPU, int, byte> GetRegData8 = GetDataFromCPU(ArrayReg8);
        public static Func<CPU, int, byte, CPU> SetRegData8 = SetDataFromCPU(ArrayReg8);

        static (Func<CPU, ushort> getter, Func<CPU, ushort, CPU> setter)[] ArrayReg16 = new(Func<CPU, ushort> getter, Func<CPU, ushort, CPU> setter)[]
        {
            (cpu=>cpu.ax,(cpu,data)=>{cpu.ax=data; return cpu; }),
            (cpu=>cpu.cx,(cpu,data)=>{cpu.cx=data; return cpu; }),
            (cpu=>cpu.dx,(cpu,data)=>{cpu.dx=data; return cpu; }),
            (cpu=>cpu.bx,(cpu,data)=>{cpu.bx=data; return cpu; }),
            (cpu=>cpu.sp,(cpu,data)=>{cpu.sp=data; return cpu; }),
            (cpu=>cpu.bp,(cpu,data)=>{cpu.bp=data; return cpu; }),
            (cpu=>cpu.si,(cpu,data)=>{cpu.si=data; return cpu; }),
            (cpu=>cpu.di,(cpu,data)=>{cpu.di=data; return cpu; }),
        };

        public static Func<CPU, int, ushort> GetRegData16 = GetDataFromCPU(ArrayReg16);
        public static Func<CPU, int, ushort, CPU> SetRegData16 = SetDataFromCPU(ArrayReg16);

        static (Func<CPU, uint> getter, Func<CPU, uint, CPU> setter)[] ArrayReg32 = new(Func<CPU, uint> getter, Func<CPU, uint, CPU> setter)[]
        {
            (cpu=>cpu.eax,(cpu,data)=>{cpu.eax=data; return cpu; }),
            (cpu=>cpu.ecx,(cpu,data)=>{cpu.ecx=data; return cpu; }),
            (cpu=>cpu.edx,(cpu,data)=>{cpu.edx=data; return cpu; }),
            (cpu=>cpu.ebx,(cpu,data)=>{cpu.ebx=data; return cpu; }),
            (cpu=>cpu.esp,(cpu,data)=>{cpu.esp=data; return cpu; }),
            (cpu=>cpu.ebp,(cpu,data)=>{cpu.ebp=data; return cpu; }),
            (cpu=>cpu.esi,(cpu,data)=>{cpu.esi=data; return cpu; }),
            (cpu=>cpu.edi,(cpu,data)=>{cpu.edi=data; return cpu; }),
        };
        public static Func<CPU, int, uint> GetRegData32 = GetDataFromCPU(ArrayReg32);
        public static Func<CPU, int, uint, CPU> SetRegData32 = SetDataFromCPU(ArrayReg32);

        static (Func<CPU, ushort> getter, Func<CPU, ushort, CPU> setter)[] ArraySreg = new(Func<CPU, ushort> getter, Func<CPU, ushort, CPU> setter)[]
        {
            (cpu=>cpu.es,(cpu,data)=>{cpu.es=data; return cpu; }),
            (cpu=>cpu.cs,(cpu,data)=>{cpu.cs=data; return cpu; }),
            (cpu=>cpu.ss,(cpu,data)=>{cpu.ss=data; return cpu; }),
            (cpu=>cpu.ds,(cpu,data)=>{cpu.ds=data; return cpu; }),
            (cpu=>cpu.fs,(cpu,data)=>{cpu.fs=data; return cpu; }),
            (cpu=>cpu.gs,(cpu,data)=>{cpu.gs=data; return cpu; }),
        };

        public static Func<CPU, int, ushort> GetSReg3 = GetDataFromCPU(ArraySreg);
        public static Func<CPU, int, ushort, CPU> SetSReg3 = SetDataFromCPU(ArraySreg);

        public static uint GetIndexRegData32(CPU cpu, int reg)
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
        public static Func<CPU, int, T> GetDataFromCPU<T>((Func<CPU, T> getter, Func<CPU, T, CPU> setter)[] array) => (cpu, reg) => array.ElementAt(reg).getter(cpu);
        public static Func<CPU, int, T, CPU> SetDataFromCPU<T>((Func<CPU, T> getter, Func<CPU, T, CPU> setter)[] array) => (cpu, reg, data) => array.ElementAt(reg).setter(cpu, data);
    }
}
