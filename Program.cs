using System;
using System.Collections.Generic;
using System.Linq;
using static System.Console;

namespace Emu86
{
    class CPU
    {
        public UInt16 cs { get; set; }
        public UInt16 ds { get; set; }
        public UInt16 es { get; set; }
        public UInt16 ss { get; set; }
        public UInt16 fs { get; set; }
        public UInt16 gs { get; set; }

        public UInt32 eip { get; set; }
        public UInt16 ip { get { return (UInt16)(eip & 0xFFFF); } set { this.eip = (this.eip & 0xFFFF0000) + value; } }

        public UInt16 idt_limit { get; set; }
        public UInt32 idt_base { get; set; }
        public UInt16 gdt_limit { get; set; }
        public UInt32 gdt_base { get; set; }

        public UInt32 cr0 { get; set; }
        public UInt32 cr2 { get; set; }
        public UInt32 cr3 { get; set; }

        public UInt16 bp { get { return (UInt16)(ebp & 0xFFFF); } set { this.ebp = (this.ebp & 0xFFFF0000) + value; } }
        public UInt16 sp { get { return (UInt16)(esp & 0xFFFF); } set { this.esp = (this.esp & 0xFFFF0000) + value; } }

        public UInt32 ebp { get; set; }
        public UInt32 esp { get; set; }

        public UInt32 eflags { get; set; }

        private const UInt32 CF = 1;
        private const UInt32 PF = 4;
        private const UInt32 AF = 0x10;
        private const UInt32 ZF = 0x40;
        private const UInt32 SF = 0x80;
        private const UInt32 TF = 0x100;
        private const UInt32 JF = 0x200;
        private const UInt32 DF = 0x400;
        private const UInt32 OF = 0x800;
        private const UInt32 NT = 0x4000;

        private void UpdateEflags(UInt32 flag, bool f)
        {
            this.eflags = ((this.eflags & ~flag) | (f ? flag : 0));
        }
        private bool GetEflags(UInt32 flag) => 0 != (this.eflags & flag);

        public bool pe { get { return 0 != (this.cr0 & 0x1); } }

        public bool cf { get { return GetEflags(CF); } set { UpdateEflags(CF, value); } }
        public bool pf { get { return GetEflags(PF); } set { UpdateEflags(PF, value); } }
        public bool af { get { return GetEflags(AF); } set { UpdateEflags(AF, value); } }
        public bool zf { get { return GetEflags(ZF); } set { UpdateEflags(ZF, value); } }
        public bool sf { get { return GetEflags(SF); } set { UpdateEflags(SF, value); } }
        public bool tf { get { return GetEflags(TF); } set { UpdateEflags(TF, value); } }
        public bool jf { get { return GetEflags(JF); } set { UpdateEflags(JF, value); } }
        public bool df { get { return GetEflags(DF); } set { UpdateEflags(DF, value); } }
        public bool of { get { return GetEflags(OF); } set { UpdateEflags(OF, value); } }
        public bool nt { get { return GetEflags(NT); } set { UpdateEflags(NT, value); } }

        public void update_eflags_add16(UInt16 v1, UInt16 v2)
        {
            var result = (UInt32)v1 + (UInt32)v2;

            var signr = 0 != (result & 0x8000);
            var sign1 = 0 != (v1 & 0x8000);
            var sign2 = 0 != (v2 & 0x8000);

            this.cf = 0 != (result & 0x10000);
            this.zf = 0 == result;
            this.sf = 0 != (result & 0x8000);
            this.of = (sign1 == sign2) && (sign1 != signr);
        }
        public void update_eflags8(byte v)
        {
            this.cf = false;
            this.zf = (0 == v);
            this.sf = 0 != (v & 0x80);
            this.of = false;
        }
        public void update_eflags16(ushort v)
        {
            this.cf = false;
            this.zf = (0 == v);
            this.sf = 0 != (v & 0x8000);
            this.of = false;
        }
        public void update_eflags32(uint v)
        {
            this.cf = false;
            this.zf = (0 == v);
            this.sf = 0 != (v & 0x80000000);
            this.of = false;
        }
        public void update_eflags_sub16(UInt16 v1, UInt16 v2)
        {
            var result = (UInt32)v1 - (UInt32)v2;

            var signr = 0 != (result & 0x8000);
            var sign1 = 0 != (v1 & 0x8000);
            var sign2 = 0 != (v2 & 0x8000);

            this.cf = 0 != (result & 0x10000);
            this.zf = 0 == result;
            this.sf = 0 != (result & 0x8000);
            this.of = (sign1 != sign2) && (sign1 != signr);
        }
        public void update_eflags_add32(UInt32 v1, UInt32 v2)
        {
            var result = (UInt64)v1 + (UInt64)v2;

            var signr = 0 != (result & 0x80000000);
            var sign1 = 0 != (v1 & 0x80000000);
            var sign2 = 0 != (v2 & 0x80000000);

            this.cf = 0 != (result & 0x100000000);
            this.zf = 0 == result;
            this.sf = 0 != (result & 0x80000000);
            this.of = (sign1 == sign2) && (sign1 != signr);
        }
        public void update_eflags_sub32(UInt32 v1, UInt32 v2)
        {
            var result = (UInt64)v1 - (UInt64)v2;

            var signr = 0 != (result & 0x80000000);
            var sign1 = 0 != (v1 & 0x80000000);
            var sign2 = 0 != (v2 & 0x80000000);

            this.cf = 0 != (result & 0x100000000);
            this.zf = 0 == result;
            this.sf = 0 != (result & 0x80000000);
            this.of = (sign1 != sign2) && (sign1 != signr);
        }


        public byte al { get { return (byte)(eax & 0xFF); } set { this.eax = (this.eax & 0xFFFFFF00) + value; } }
        public byte bl { get { return (byte)(ebx & 0xFF); } set { this.ebx = (this.ebx & 0xFFFFFF00) + value; } }
        public byte cl { get { return (byte)(ecx & 0xFF); } set { this.ecx = (this.ecx & 0xFFFFFF00) + value; } }
        public byte dl { get { return (byte)(edx & 0xFF); } set { this.edx = (this.edx & 0xFFFFFF00) + value; } }

        public byte ah { get { return (byte)((eax >> 8) & 0xFF); } set { this.eax = (this.eax & 0xFFFF00FF) + ((UInt32)value << 8); } }
        public byte bh { get { return (byte)((ebx >> 8) & 0xFF); } set { this.ebx = (this.ebx & 0xFFFF00FF) + ((UInt32)value << 8); } }
        public byte ch { get { return (byte)((ecx >> 8) & 0xFF); } set { this.ecx = (this.ecx & 0xFFFF00FF) + ((UInt32)value << 8); } }
        public byte dh { get { return (byte)((edx >> 8) & 0xFF); } set { this.edx = (this.edx & 0xFFFF00FF) + ((UInt32)value << 8); } }

        public UInt16 ax { get { return (UInt16)(eax & 0xFFFF); } set { this.eax = (this.eax & 0xFFFF0000) + value; } }
        public UInt16 bx { get { return (UInt16)(ebx & 0xFFFF); } set { this.ebx = (this.ebx & 0xFFFF0000) + value; } }
        public UInt16 cx { get { return (UInt16)(ecx & 0xFFFF); } set { this.ecx = (this.ecx & 0xFFFF0000) + value; } }
        public UInt16 dx { get { return (UInt16)(edx & 0xFFFF); } set { this.edx = (this.edx & 0xFFFF0000) + value; } }

        public UInt32 eax { get; set; }
        public UInt32 ebx { get; set; }
        public UInt32 ecx { get; set; }
        public UInt32 edx { get; set; }

        public UInt16 si { get { return (UInt16)(esi & 0xFFFF); } set { this.esi = (this.esi & 0xFFFF0000) + value; } }
        public UInt16 di { get { return (UInt16)(edi & 0xFFFF); } set { this.edi = (this.edi & 0xFFFF0000) + value; } }

        public UInt32 esi { get; set; }
        public UInt32 edi { get; set; }
    }

    class Program
    {
        static byte[] OneMegaMemory_ = new byte[1024 * 1024];
        static IEnumerable<byte> GetMemoryDatas(UInt16 segment, UInt16 offset)
        {
            var addr = ((UInt32)segment) * 0x10 + offset;
            return OneMegaMemory_.Skip((int)addr);
        }
        static byte GetMemoryData8(UInt32 addr)
        {
            return OneMegaMemory_[addr];
        }
        static byte GetMemoryData8(UInt16 segment, UInt16 offset)
        {
            var addr = ((UInt32)segment) * 0x10 + offset;
            return OneMegaMemory_[addr];
        }
        static void SetMemoryData8(UInt32 addr, byte data)
        {
            OneMegaMemory_[addr] = data;
        }
        static UInt16 GetMemoryData16(UInt32 addr) => (UInt16)(OneMegaMemory_[addr + 1] * 0x100 + OneMegaMemory_[addr]);
        static UInt16 GetMemoryData16(UInt16 segment, UInt16 offset) =>
            (UInt16)((UInt16)GetMemoryData8(segment, offset) +
            0x100 * (UInt16)GetMemoryData8(segment, (UInt16)(1 + offset)));

        static void SetMemoryData16(UInt32 addr, UInt16 data)
        {
            OneMegaMemory_[addr + 0] = (byte)(data & 0xFF);
            OneMegaMemory_[addr + 1] = (byte)((data >> 8) & 0xFF);
        }
        static UInt32 GetMemoryData32(UInt16 segment, UInt16 offset) =>
            (UInt32)GetMemoryData8(segment, offset) +
            0x100 * (UInt32)GetMemoryData8(segment, (UInt16)(1 + offset)) +
            0x10000 * (UInt32)GetMemoryData8(segment, (UInt16)(2 + offset)) +
            0x1000000 * (UInt32)GetMemoryData8(segment, (UInt16)(3 + offset));

        static UInt32 GetMemoryData32(UInt32 addr) =>
            (UInt32)OneMegaMemory_[addr] +
            0x100 * (UInt32)OneMegaMemory_[1 + addr] +
            0x10000 * (UInt32)OneMegaMemory_[2 + addr] +
            0x1000000 * (UInt32)OneMegaMemory_[3 + addr];
        static void SetMemoryData32(UInt32 addr, UInt32 data)
        {
            OneMegaMemory_[addr + 0] = (byte)(data & 0xFF);
            OneMegaMemory_[addr + 1] = (byte)((data >> 8) & 0xFF);
            OneMegaMemory_[addr + 2] = (byte)((data >> 16) & 0xFF);
            OneMegaMemory_[addr + 3] = (byte)((data >> 24) & 0xFF);
        }
        static (UInt32, int) GetMemoryAddr16_(CPU cpu, int mod, int rm, UInt16? segment, IEnumerable<byte> disp)
        {
            var segment_base =
                segment == default(UInt16?) ?
                ((UInt32)cpu.ds) * 0x10 :
                ((UInt32)segment) * 0x10;

            var ss_base = ((UInt32)cpu.ss) * 0x10;

            int inc = 0;

            switch (mod)
            {
                case 0:
                    {
                        UInt32 addr = 0;
                        switch (rm)
                        {
                            case 0: // DS:[BX+SI]
                                addr = segment_base + cpu.bx + cpu.si;
                                break;
                            case 1: // DS:[BX+DI]
                                addr = segment_base + cpu.bx + cpu.di;
                                break;
                            case 2: // DS:[BP+SI]
                                addr = segment_base + cpu.bp + cpu.si;
                                break;
                            case 3: // DS:[BP+DI]
                                addr = segment_base + cpu.bp + cpu.di;
                                break;
                            case 4: // DS:[SI]
                                addr = segment_base + cpu.si;
                                break;
                            case 5: // DS:[DI]
                                addr = segment_base + cpu.di;
                                break;
                            case 6: // DS:[d16]
                                addr = segment_base + (uint)disp.ElementAt(0) + 0x100 * (uint)disp.ElementAt(1);
                                inc = 2;
                                break;
                            case 7: // DS:[BX]
                                addr = segment_base + cpu.bx;
                                break;
                            default:
                                throw new Exception();
                        }
                        return (addr, inc);
                    }
                case 1:
                    {
                        UInt32 addr = 0;
                        var d8 = (int)(sbyte)disp.ElementAt(0);
                        switch (rm)
                        {
                            case 0: // DS:[BX+SI+d8]
                                addr = (uint)(segment_base + cpu.bx + cpu.si + d8);
                                break;
                            case 1: // DS:[BX+DI+d8]
                                addr = (uint)(segment_base + cpu.bx + cpu.di + d8);
                                break;
                            case 2: // DS:[BP+SI+d8]
                                addr = (uint)(segment_base + cpu.bp + cpu.si + d8);
                                break;
                            case 3: // DS:[BP+DI+d8]
                                addr = (uint)(segment_base + cpu.bp + cpu.di + d8);
                                break;
                            case 4: // DS:[SI+d8]
                                addr = (uint)(segment_base + cpu.si + d8);
                                break;
                            case 5: // DS:[DI+d8]
                                addr = (uint)(segment_base + cpu.di + d8);
                                break;
                            case 6: // SS:[bp+d8]
                                addr = (uint)(ss_base + cpu.bp + d8);
                                break;
                            case 7: // DS:[DI+d8]
                                addr = (uint)(segment_base + cpu.di + d8);
                                break;
                            default:
                                throw new Exception();
                        }
                        return (addr, 1);
                    }
                case 2:
                    {
                        UInt32 addr = 0;
                        var d16 = (uint)disp.ElementAt(0) + 0x100 * (uint)disp.ElementAt(1);
                        switch (rm)
                        {
                            case 0: // DS:[BX+SI+d16]
                                addr = (uint)(segment_base + cpu.bx + cpu.si + d16);
                                break;
                            case 1: // DS:[BX+DI+d16]
                                addr = (uint)(segment_base + cpu.bx + cpu.di + d16);
                                break;
                            case 2: // DS:[BP+SI+d16]
                                addr = (uint)(segment_base + cpu.bp + cpu.si + d16);
                                break;
                            case 3: // DS:[BP+DI+d16]
                                addr = (uint)(segment_base + cpu.bp + cpu.di + d16);
                                break;
                            case 4: // DS:[SI+d16]
                                addr = (uint)(segment_base + cpu.si + d16);
                                break;
                            case 5: // DS:[DI+d16]
                                addr = (uint)(segment_base + cpu.di + d16);
                                break;
                            case 6: // SS:[bp+d16]
                                addr = (uint)(ss_base + cpu.bp + d16);
                                break;
                            case 7: // DS:[DI+d16]
                                addr = (uint)(segment_base + cpu.di + d16);
                                break;
                            default:
                                throw new Exception();
                        }
                        return (addr, 2);
                    }
                default:
                    throw new Exception();
            }
        }
        static (UInt32, int) GetMemoryAddr32_(CPU cpu, int mod, int rm, UInt16? segment, IEnumerable<byte> disp)
        {
            var segment_base =
                segment == default(UInt16?) ?
                ((UInt32)cpu.ds) * 0x10 :
                ((UInt32)segment) * 0x10;

            var ss_base = ((UInt32)cpu.ss) * 0x10;

            int inc = 0;

            switch (mod)
            {
                case 0:
                    {
                        UInt32 addr = 0;
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
                                    addr = segment_base +
                                        (uint)disp.ElementAt(0) +
                                        0x100 * (uint)disp.ElementAt(1) +
                                        0x10000 * (uint)disp.ElementAt(2) +
                                        0x1000000 * (uint)disp.ElementAt(3);
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
                        return (addr, inc);
                    }
                case 1:
                    {
                        UInt32 addr = 0;
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

                                    return (addr, 2);
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
                        return (addr, 1);
                    }
                case 2:
                    {
                        UInt32 addr = 0;

                        var d32 =
                            (uint)disp.ElementAt(0) +
                            0x100 * (uint)disp.ElementAt(1) +
                            0x10000 * (uint)disp.ElementAt(2) +
                            0x1000000 * (uint)disp.ElementAt(3);

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
                        return (addr, 4);
                    }
                default:
                    throw new Exception();
            }
        }
        static (UInt32, int) GetMemoryAddr(CPU cpu, int mod, int rm, UInt16? segment, IEnumerable<byte> disp, bool address_size)
            => address_size ?
            GetMemoryAddr32_(cpu, mod, rm, segment, disp) :
            GetMemoryAddr16_(cpu, mod, rm, segment, disp);
        static byte GetRegData8(CPU cpu, int reg)
        {
            switch (reg)
            {
                case 0: return cpu.al;
                case 1: return cpu.cl;
                case 2: return cpu.dl;
                case 3: return cpu.bl;
                case 4: return cpu.ah;
                case 5: return cpu.ch;
                case 6: return cpu.dh;
                case 7: return cpu.bh;
                default: throw new Exception();
            }
        }
        static void SetRegData8(CPU cpu, int reg, byte data)
        {
            switch (reg)
            {
                case 0: cpu.al = data; break;
                case 1: cpu.cl = data; break;
                case 2: cpu.dl = data; break;
                case 3: cpu.bl = data; break;
                case 4: cpu.ah = data; break;
                case 5: cpu.ch = data; break;
                case 6: cpu.dh = data; break;
                case 7: cpu.bh = data; break;
                default: throw new Exception();
            }
        }
        static ushort GetRegData16(CPU cpu, int reg)
        {
            switch (reg)
            {
                case 0: return cpu.ax;
                case 1: return cpu.cx;
                case 2: return cpu.dx;
                case 3: return cpu.bx;
                case 4: return cpu.sp;
                case 5: return cpu.bp;
                case 6: return cpu.si;
                case 7: return cpu.di;
                default: throw new Exception();
            }
        }
        static void SetRegData16(CPU cpu, int reg, ushort data)
        {
            switch (reg)
            {
                case 0: cpu.ax = data; break;
                case 1: cpu.cx = data; break;
                case 2: cpu.dx = data; break;
                case 3: cpu.bx = data; break;
                case 4: cpu.sp = data; break;
                case 5: cpu.bp = data; break;
                case 6: cpu.si = data; break;
                case 7: cpu.di = data; break;
                default: throw new Exception();
            }
        }
        static uint GetRegData32(CPU cpu, int reg)
        {
            switch (reg)
            {
                case 0: return cpu.eax;
                case 1: return cpu.ecx;
                case 2: return cpu.edx;
                case 3: return cpu.ebx;
                case 4: return cpu.esp;
                case 5: return cpu.ebp;
                case 6: return cpu.esi;
                case 7: return cpu.edi;
                default: throw new Exception();
            }
        }
        static uint GetIndexRegData32(CPU cpu, int reg)
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
        static void SetRegData32(CPU cpu, int reg, uint data)
        {
            switch (reg)
            {
                case 0: cpu.eax = data; break;
                case 1: cpu.ecx = data; break;
                case 2: cpu.edx = data; break;
                case 3: cpu.ebx = data; break;
                case 4: cpu.esp = data; break;
                case 5: cpu.ebp = data; break;
                case 6: cpu.esi = data; break;
                case 7: cpu.edi = data; break;
                default: throw new Exception();
            }
        }
        static ushort GetSReg3(CPU cpu, int reg)
        {
            switch (reg)
            {
                case 0: return cpu.es;
                case 1: return cpu.cs;
                case 2: return cpu.ss;
                case 3: return cpu.ds;
                case 4: return cpu.fs;
                case 5: return cpu.gs;
                default: throw new Exception();
            }
        }
        static void SetSReg3(CPU cpu, int reg, ushort data)
        {
            switch (reg)
            {
                case 0: cpu.es = data; break;
                case 1: cpu.cs = data; break;
                case 2: cpu.ss = data; break;
                case 3: cpu.ds = data; break;
                case 4: cpu.fs = data; break;
                case 5: cpu.gs = data; break;
                default: throw new Exception();
            }
        }
        static (byte, int) GetMemoryData8(CPU cpu, int mod, int rm, UInt16? segment, IEnumerable<byte> disp, bool address_size)
        {
            switch (mod)
            {
                case 0:
                case 1:
                case 2:
                    {
                        (var addr, var inc) = GetMemoryAddr(cpu, mod, rm, segment, disp, address_size);
                        return (GetMemoryData8(addr), inc);
                    }
                case 3:
                    {
                        switch (rm)
                        {
                            case 0: // AL
                                return (cpu.al, 0);
                            case 1: // CL
                                return (cpu.cl, 0);
                            case 2: // DL
                                return (cpu.dl, 0);
                            case 3: // BL
                                return (cpu.bl, 0);
                            case 4: // AH
                                return (cpu.ah, 0);
                            case 5: // CH
                                return (cpu.ch, 0);
                            case 6: // DH
                                return (cpu.dh, 0);
                            case 7: // BH
                                return (cpu.bh, 0);
                            default:
                                throw new Exception();
                        }
                    }
                default:
                    throw new Exception();
            }
        }
        static int SetMemoryData8(CPU cpu, int mod, int rm, UInt16? segment, IEnumerable<byte> disp, byte data, bool address_size)
        {
            switch (mod)
            {
                case 0:
                case 1:
                case 2:
                    {
                        (var addr, var inc) = GetMemoryAddr(cpu, mod, rm, segment, disp, address_size);
                        SetMemoryData8(addr, data);
                        return inc;
                    }
                case 3:
                    {
                        switch (rm)
                        {
                            case 0: // AL
                                cpu.al = data;
                                return 0;
                            case 1: // CL
                                cpu.cl = data;
                                return 0;
                            case 2: // DL
                                cpu.dl = data;
                                return 0;
                            case 3: // BL
                                cpu.bl = data;
                                return 0;
                            case 4: // AH
                                cpu.ah = data;
                                return 0;
                            case 5: // CH
                                cpu.ch = data;
                                return 0;
                            case 6: // DH
                                cpu.dh = data;
                                return 0;
                            case 7: // BH
                                cpu.bh = data;
                                return 0;
                            default:
                                throw new Exception();
                        }
                    }
                default:
                    throw new Exception();
            }
        }
        static (UInt16, int) GetMemoryData16(CPU cpu, int mod, int rm, UInt16? segment, IEnumerable<byte> disp, bool address_size)
        {
            switch (mod)
            {
                case 0:
                case 1:
                case 2:
                    {
                        (var addr, var inc) = GetMemoryAddr(cpu, mod, rm, segment, disp, address_size);
                        return (GetMemoryData16(addr), inc);
                    }
                case 3:
                    {
                        switch (rm)
                        {
                            case 0: // AX
                                return (cpu.ax, 0);
                            case 1: // CX
                                return (cpu.cx, 0);
                            case 2: // DX
                                return (cpu.dx, 0);
                            case 3: // BX
                                return (cpu.bx, 0);
                            case 4: // SP
                                return (cpu.sp, 0);
                            case 5: // BP
                                return (cpu.bp, 0);
                            case 6: // SI
                                return (cpu.si, 0);
                            case 7: // DI
                                return (cpu.di, 0);
                            default:
                                throw new Exception();
                        }
                    }
                default:
                    throw new Exception();
            }
        }
        static int SetMemoryData16(CPU cpu, int mod, int rm, UInt16? segment, IEnumerable<byte> disp, ushort data, bool address_size)
        {
            switch (mod)
            {
                case 0:
                case 1:
                case 2:
                    {
                        (var addr, var inc) = GetMemoryAddr(cpu, mod, rm, segment, disp, address_size);
                        SetMemoryData16(addr, data);
                        return inc;
                    }
                case 3:
                    {
                        switch (rm)
                        {
                            case 0: // AX
                                cpu.ax = data;
                                return 0;
                            case 1: // CX
                                cpu.cx = data;
                                return 0;
                            case 2: // DX
                                cpu.dx = data;
                                return 0;
                            case 3: // BX
                                cpu.bx = data;
                                return 0;
                            case 4: // SP
                                cpu.sp = data;
                                return 0;
                            case 5: // BP
                                cpu.bp = data;
                                return 0;
                            case 6: // SI
                                cpu.si = data;
                                return 0;
                            case 7: // DI
                                cpu.di = data;
                                return 0;
                            default:
                                throw new Exception();
                        }
                    }
                default:
                    throw new Exception();
            }
        }
        static (UInt32, int) GetMemoryData32(CPU cpu, int mod, int rm, UInt16? segment, IEnumerable<byte> disp, bool address_size)
        {
            switch (mod)
            {
                case 0:
                case 1:
                case 2:
                    {
                        (var addr, var inc) = GetMemoryAddr(cpu, mod, rm, segment, disp, address_size);
                        return (GetMemoryData32(addr), inc);
                    }
                case 3:
                    {
                        switch (rm)
                        {
                            case 0: // EAX
                                return (cpu.eax, 0);
                            case 1: // ECX
                                return (cpu.ecx, 0);
                            case 2: // EDX
                                return (cpu.edx, 0);
                            case 3: // EBX
                                return (cpu.ebx, 0);
                            case 4: // ESP
                                return (cpu.esp, 0);
                            case 5: // EBP
                                return (cpu.ebp, 0);
                            case 6: // ESI
                                return (cpu.esi, 0);
                            case 7: // EDI
                                return (cpu.edi, 0);
                            default:
                                throw new Exception();
                        }
                    }
                default:
                    throw new Exception();
            }
        }
        static int SetMemoryData32(CPU cpu, int mod, int rm, UInt16? segment, IEnumerable<byte> disp, uint data, bool address_size)
        {
            switch (mod)
            {
                case 0:
                case 1:
                case 2:
                    {
                        (var addr, var inc) = GetMemoryAddr(cpu, mod, rm, segment, disp, address_size);
                        SetMemoryData32(addr, data);
                        return inc;
                    }
                case 3:
                    {
                        switch (rm)
                        {
                            case 0: // EAX
                                cpu.eax = data;
                                return 0;
                            case 1: // ECX
                                cpu.ecx = data;
                                return 0;
                            case 2: // EDX
                                cpu.edx = data;
                                return 0;
                            case 3: // EBX
                                cpu.ebx = data;
                                return 0;
                            case 4: // ESP
                                cpu.esp = data;
                                return 0;
                            case 5: // EBP
                                cpu.ebp = data;
                                return 0;
                            case 6: // ESI
                                cpu.esi = data;
                                return 0;
                            case 7: // EDI
                                cpu.edi = data;
                                return 0;
                            default:
                                throw new Exception();
                        }
                    }
                default:
                    throw new Exception();
            }
        }
        static bool Jcc(CPU cpu, int type)
        {
            switch (type)
            {
                case 0x0:// JO
                    return cpu.of;
                case 0x1:// JNO
                    return !cpu.of;
                case 0x2:// JB
                    return cpu.cf;
                case 0x3:// JNB
                    return !cpu.cf;
                case 0x4:// JE
                    return cpu.zf;
                case 0x5:// JNE
                    return !cpu.zf;
                case 0x6:// JBE
                    return cpu.cf || cpu.zf;
                case 0x7:// JNBE
                    return (!cpu.cf) && (!cpu.zf);
                case 0x8:// JS
                    return cpu.sf;
                case 0x9:// JNS
                    return !cpu.sf;
                case 0xA:// JP
                    return cpu.pf;
                case 0xB:// JNP
                    return !cpu.pf;
                case 0xC:// JL
                    return (cpu.sf != cpu.of) && (!cpu.zf);
                case 0xD:// JNL
                    return cpu.sf == cpu.of;
                case 0xE:// JLE
                    return cpu.sf != cpu.of;
                case 0xF:// JNLE
                    return (cpu.sf == cpu.of) && (!cpu.zf);
                default:
                    throw new Exception();
            }
        }

        static void Main(string[] args)
        {
            // bios
            {
                var biosdata = Properties.Resources.bios;
                Array.Copy(biosdata, 0, OneMegaMemory_, 0x100000 - biosdata.Length, biosdata.Length);
            }

            var cpu = new CPU()
            {
                cs = 0xF000,
                ip = 0xFFF0,
            };

            while (true)
            {
                bool operand_size = false;
                bool address_size = false;

                var segment = default(UInt16?);

                aaa:

                var b =
                    GetMemoryData8(cpu.cs, cpu.ip);

                WriteLine();
                Write($"{cpu.ip.ToString("X4")} ");
                Write($"{b.ToString("X2")} ");

                if (cpu.pe)
                {
                    ++cpu.eip;
                }
                else
                {
                    ++cpu.ip;
                }

                switch (b)
                {
                    case 0x0f:
                        {
                            var d1 = GetMemoryData8(cpu.cs, cpu.ip);
                            ++cpu.ip;
                            switch (d1)
                            {
                                case 0x01:  // Group 7
                                    {
                                        var d2 = GetMemoryData8(cpu.cs, cpu.ip);
                                        var mod = ((d2 >> 6) & 0x3);
                                        var type = ((d2 >> 3) & 0x7);
                                        var rm = (d2 & 0x7);

                                        ++cpu.ip;

                                        switch (type)
                                        {
                                            case 2:// LGDT
                                                {
                                                    Write("LGDT");

                                                    (var addr2, var inc) =
                                                        GetMemoryAddr(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                                    cpu.ip += (ushort)inc;

                                                    cpu.gdt_limit = GetMemoryData16(addr2);
                                                    cpu.gdt_base = GetMemoryData32(addr2 + 2);
                                                    {
                                                        var e1 = GetMemoryData32(cpu.gdt_base);
                                                        var e2 = GetMemoryData32(cpu.gdt_base + 4);
                                                        var e3 = GetMemoryData32(cpu.gdt_base + 8);
                                                        var e4 = GetMemoryData32(cpu.gdt_base + 0xc);
                                                        var e5 = GetMemoryData32(cpu.gdt_base + 0x10);
                                                        var e6 = GetMemoryData32(cpu.gdt_base + 0x14);
                                                        var e7 = GetMemoryData32(cpu.gdt_base + 0x18);
                                                        var e8 = GetMemoryData32(cpu.gdt_base + 0x1c);
                                                    }
                                                }
                                                break;
                                            case 3:// LIDT
                                                {
                                                    Write("LIDT");

                                                    (var addr2, var inc) =
                                                        GetMemoryAddr(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                                    cpu.ip += (ushort)inc;

                                                    cpu.idt_limit = GetMemoryData16(addr2);
                                                    cpu.idt_base = GetMemoryData32(addr2 + 2);
                                                }
                                                break;
                                            default:
                                                throw new NotImplementedException();
                                        }
                                    }
                                    break;
                                case 0x20:
                                    {
                                        Write("MOV");

                                        var d2 = GetMemoryData8(cpu.cs, cpu.ip);
                                        var mod = ((d2 >> 6) & 0x3);
                                        var eee = ((d2 >> 3) & 0x7);
                                        var reg = (d2 & 0x7);

                                        ++cpu.ip;

                                        if (3 != mod)
                                        {
                                            throw new NotImplementedException();
                                        }

                                        UInt32 data1 = 0;

                                        switch (eee)
                                        {
                                            case 0:
                                                data1 = cpu.cr0;
                                                break;
                                            case 2:
                                                data1 = cpu.cr2;
                                                break;
                                            case 3:
                                                data1 = cpu.cr2;
                                                break;
                                            default:
                                                throw new NotImplementedException();
                                        }

                                        SetRegData32(cpu, reg, data1);
                                    }
                                    break;
                                case 0x22:
                                    {
                                        var d2 = GetMemoryData8(cpu.cs, cpu.ip);
                                        var mod = ((d2 >> 6) & 0x3);
                                        var eee = ((d2 >> 3) & 0x7);
                                        var reg = (d2 & 0x7);

                                        ++cpu.ip;

                                        if (3 != mod)
                                        {
                                            throw new NotImplementedException();
                                        }

                                        var data1 = GetRegData32(cpu, reg);

                                        switch (eee)
                                        {
                                            case 0:
                                                cpu.cr0 = data1;
                                                throw new NotImplementedException();
                                                break;
                                            case 2:
                                                cpu.cr2 = data1;
                                                break;
                                            case 3:
                                                cpu.cr2 = data1;
                                                break;
                                            default:
                                                throw new NotImplementedException();
                                        }
                                    }
                                    break;
                                case 0x80:
                                case 0x81:
                                case 0x82:
                                case 0x83:
                                case 0x84:
                                case 0x85:
                                case 0x86:
                                case 0x87:
                                case 0x88:
                                case 0x89:
                                case 0x8A:
                                case 0x8B:
                                case 0x8C:
                                case 0x8D:
                                case 0x8E:
                                case 0x8F:
                                    {
                                        Write("Jcc");

                                        var type = d1 & 0xF;

                                        if (Jcc(cpu, type))
                                        {
                                            cpu.ip = (ushort)(cpu.ip + (short)GetMemoryData16(cpu.cs, cpu.ip));
                                        }

                                        ++cpu.ip;
                                        ++cpu.ip;
                                    }
                                    break;
                                case 0xB6:
                                case 0xB7:
                                case 0xBE:
                                case 0xBF:
                                    {
                                        var w = 0 != (d1 & 1);
                                        var z = 0 != (d1 & 8);

                                        var d2 = GetMemoryData8(cpu.cs, cpu.ip);
                                        var mod = ((d2 >> 6) & 0x3);
                                        var reg = ((d2 >> 3) & 0x7);
                                        var rm = (d2 & 0x7);

                                        ++cpu.ip;

                                        if (w)
                                        {
                                            (var dw, var inc) = GetMemoryData16(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                            cpu.ip += (ushort)inc;

                                            if (operand_size)
                                            {
                                                var dd = z ? (uint)(short)dw : (uint)dw;

                                                SetRegData32(cpu, reg, dd);
                                            }
                                            else
                                            {
                                                SetRegData16(cpu, reg, dw);
                                            }
                                        }
                                        else
                                        {
                                            (var db, var inc) = GetMemoryData8(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                            cpu.ip += (ushort)inc;

                                            if (operand_size)
                                            {
                                                var dd = z ? (uint)(sbyte)db : (uint)db;

                                                SetRegData32(cpu, reg, dd);
                                            }
                                            else
                                            {
                                                var dw = z ? (ushort)(sbyte)db : (ushort)db;
                                                SetRegData16(cpu, reg, dw);
                                            }
                                        }
                                    }

                                    break;
                                default:
                                    throw new NotImplementedException();
                            }
                        }
                        break;
                    // ES:
                    case 0x26:
                        Write("ES:");
                        segment = cpu.es;
                        goto aaa;
                    // CS:
                    case 0x2e:
                        Write("CS:");
                        segment = cpu.cs;
                        goto aaa;

                    case 0x00: // ADD 
                    case 0x01: // ADD
                    case 0x02: // ADD
                    case 0x03: // ADD
                    case 0x04: // ADD
                    case 0x05: // ADD

                    case 0x08: // OR 
                    case 0x09: // OR
                    case 0x0A: // OR
                    case 0x0B: // OR
                    case 0x0C: // OR
                    case 0x0D: // OR

                    case 0x10: // ADC 
                    case 0x11: // ADC
                    case 0x12: // ADC
                    case 0x13: // ADC
                    case 0x14: // ADC
                    case 0x15: // ADC

                    case 0x18: // SBB
                    case 0x19: // SBB
                    case 0x1A: // SBB
                    case 0x1B: // SBB
                    case 0x1C: // SBB
                    case 0x1D: // SBB

                    case 0x20: // AND 
                    case 0x21: // AND
                    case 0x22: // AND
                    case 0x23: // AND
                    case 0x24: // AND
                    case 0x25: // AND

                    case 0x28: // SUB
                    case 0x29: // SUB
                    case 0x2A: // SUB
                    case 0x2B: // SUB
                    case 0x2C: // SUB
                    case 0x2D: // SUB

                    case 0x30: // XOR
                    case 0x31: // XOR
                    case 0x32: // XOR
                    case 0x33: // XOR
                    case 0x34: // XOR
                    case 0x35: // XOR

                    case 0x38: // CMP
                    case 0x39: // CMP
                    case 0x3A: // CMP
                    case 0x3B: // CMP
                    case 0x3C: // CMP
                    case 0x3D: // CMP
                        {
                            var w = 0 != (b & 1);
                            var d = 0 != (b & 2);

                            var d1 = GetMemoryData8(cpu.cs, cpu.ip);

                            var mod = ((d1 >> 6) & 0x3);
                            var reg = ((d1 >> 3) & 0x7);
                            var rm = (d1 & 0x7);

                            ++cpu.ip;

                            var db1 = default(byte);
                            var db2 = default(byte);
                            var dw1 = default(ushort);
                            var dw2 = default(ushort);
                            var dd1 = default(uint);
                            var dd2 = default(uint);
                            var inc = default(int);

                            if (d)
                            {
                                if (w)
                                {
                                    if (operand_size)
                                    {
                                        (dd2, inc) = GetMemoryData32(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);
                                        dd1 = GetRegData32(cpu, reg);
                                    }
                                    else
                                    {
                                        (dw2, inc) = GetMemoryData16(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);
                                        dw1 = GetRegData16(cpu, reg);
                                    }
                                }
                                else
                                {
                                    (db2, inc) = GetMemoryData8(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);
                                    db1 = GetRegData8(cpu, reg);
                                }
                            }
                            else
                            {
                                if (w)
                                {
                                    if (operand_size)
                                    {
                                        (dd1, inc) = GetMemoryData32(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);
                                        dd2 = GetRegData32(cpu, reg);
                                    }
                                    else
                                    {
                                        (dw1, inc) = GetMemoryData16(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);
                                        dw2 = GetRegData16(cpu, reg);
                                    }
                                }
                                else
                                {
                                    (db1, inc) = GetMemoryData8(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);
                                    db2 = GetRegData8(cpu, reg);
                                }
                            }

                            cpu.ip += (ushort)inc;

                            var kind = (b >> 3) & 0x7;

                            (db1, dw1, dd1) = Calc(cpu, w, operand_size, db1, db2, dw1, dw2, dd1, dd2, kind);

                            if (d)
                            {
                                if (w)
                                {
                                    if (operand_size)
                                    {
                                        SetRegData32(cpu, reg, dd1);
                                    }
                                    else
                                    {
                                        SetRegData16(cpu, reg, dw1);
                                    }
                                }
                                else
                                {
                                    SetRegData8(cpu, reg, db1);
                                }
                            }
                            else
                            {
                                if (w)
                                {
                                    if (operand_size)
                                    {
                                        inc = SetMemoryData32(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), dd1, address_size);
                                    }
                                    else
                                    {
                                        inc = SetMemoryData16(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), dw1, address_size);
                                    }
                                }
                                else
                                {
                                    inc = SetMemoryData8(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), db1, address_size);
                                }
                            }

                            cpu.ip += (ushort)inc;
                        }
                        break;
                    case 0x40:
                    case 0x41:
                    case 0x42:
                    case 0x43:
                    case 0x44:
                    case 0x45:
                    case 0x46:
                    case 0x47:
                        {
                            Write("MOV");
                            var reg = b & 0x7;
                            var data = GetRegData16(cpu, reg);
                            ++data;
                            SetRegData16(cpu, reg, data);
                        }
                        break;
                    // PUSH
                    case 0x50:
                    case 0x51:
                    case 0x52:
                    case 0x53:
                    case 0x54:
                    case 0x55:
                    case 0x56:
                    case 0x57:
                        {
                            Write("PUSH");
                            var reg = b & 0x7;

                            if (operand_size)
                            {
                                --cpu.sp;
                                --cpu.sp;
                                --cpu.sp;
                                --cpu.sp;

                                var stack_addr = (UInt32)cpu.sp + ((UInt32)cpu.ss) * 0x10;

                                var stack_value = GetRegData32(cpu, reg);

                                SetMemoryData32(stack_addr, stack_value);
                            }
                            else
                            {
                                --cpu.sp;
                                --cpu.sp;

                                var stack_addr = (UInt32)cpu.sp + ((UInt32)cpu.ss) * 0x10;

                                var stack_value = GetRegData16(cpu, reg);

                                SetMemoryData16(stack_addr, stack_value);
                            }
                        }
                        break;
                    // POP
                    case 0x58:
                    case 0x59:
                    case 0x5A:
                    case 0x5B:
                    case 0x5C:
                    case 0x5D:
                    case 0x5E:
                    case 0x5F:
                        {
                            Write("POP");
                            var reg = b & 0x7;

                            var stack_addr = (UInt32)cpu.sp + ((UInt32)cpu.ss) * 0x10;

                            if (operand_size)
                            {
                                var stack_value = GetMemoryData32(stack_addr);

                                ++cpu.sp;
                                ++cpu.sp;
                                ++cpu.sp;
                                ++cpu.sp;

                                SetRegData32(cpu, reg, stack_value);
                            }
                            else
                            {
                                var stack_value = GetMemoryData16(stack_addr);

                                ++cpu.sp;
                                ++cpu.sp;

                                SetRegData16(cpu, reg, stack_value);
                            }
                        }
                        break;

                    // operand size
                    case 0x66:
                        {
                            Write("OPSIZE:");
                            operand_size = true;
                            goto aaa;
                        }
                    // address size
                    case 0x67:
                        {
                            Write("ADDR_SIZE:");
                            address_size = true;
                            goto aaa;
                        }
                    case 0x70:
                    case 0x71:
                    case 0x72:
                    case 0x73:
                    case 0x74:
                    case 0x75:
                    case 0x76:
                    case 0x77:
                    case 0x78:
                    case 0x79:
                    case 0x7A:
                    case 0x7B:
                    case 0x7C:
                    case 0x7D:
                    case 0x7E:
                    case 0x7F:
                        {
                            Write("Jcc");

                            var type = b & 0xF;
                            if (Jcc(cpu, type))
                            {
                                var offset = (sbyte)GetMemoryData8(cpu.cs, cpu.ip);
                                cpu.ip = (ushort)((short)cpu.ip + offset);
                            }
                            else
                            {
                            }
                            ++cpu.ip;
                        }
                        break;
                    case 0x80:// Group 1
                    case 0x81:// Group 1
                        {
                            var w = 0 != (b & 0x1);
                            var d1 = GetMemoryData8(cpu.cs, cpu.ip);

                            var mod = ((d1 >> 6) & 0x3);
                            var kind = ((d1 >> 3) & 0x7);
                            var rm = (d1 & 0x7);

                            ++cpu.ip;

                            var db1 = default(byte);
                            var db2 = default(byte);
                            var dw1 = default(ushort);
                            var dw2 = default(ushort);
                            var dd1 = default(uint);
                            var dd2 = default(uint);

                            int inc = 0;

                            var ipBak = cpu.ip;

                            if (w)
                            {
                                if (operand_size)
                                {
                                    (dd1, inc) = GetMemoryData32(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                    cpu.ip += (ushort)inc;

                                    dd2 = GetMemoryData32(cpu.cs, cpu.ip);

                                    ++cpu.ip;
                                    ++cpu.ip;
                                    ++cpu.ip;
                                    ++cpu.ip;
                                }
                                else
                                {
                                    (dw1, inc) = GetMemoryData16(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                    cpu.ip += (ushort)inc;

                                    dw2 = GetMemoryData16(cpu.cs, cpu.ip);

                                    ++cpu.ip;
                                    ++cpu.ip;
                                }
                            }
                            else
                            {
                                (db1, inc) = GetMemoryData8(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                cpu.ip += (ushort)inc;

                                db2 = GetMemoryData8(cpu.cs, cpu.ip);

                                ++cpu.ip;
                            }

                            (db1, dw1, dd1) = Calc(cpu, w, operand_size, db1, db2, dw1, dw2, dd1, dd2, kind);

                            if (w)
                            {
                                if (operand_size)
                                {
                                    SetMemoryData32(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, ipBak), dd1, address_size);
                                }
                                else
                                {
                                    SetMemoryData16(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, ipBak), dw1, address_size);
                                }
                            }
                            else
                            {
                                SetMemoryData8(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, ipBak), db1, address_size);
                            }
                        }
                        break;
                    case 0x83:// Group 1
                        {
                            var d1 = GetMemoryData8(cpu.cs, cpu.ip);

                            var mod = ((d1 >> 6) & 0x3);
                            var kind = ((d1 >> 3) & 0x7);
                            var rm = (d1 & 0x7);

                            ++cpu.ip;

                            var db1 = default(byte);
                            var db2 = default(byte);
                            var dw1 = default(ushort);
                            var dw2 = default(ushort);
                            var dd1 = default(uint);
                            var dd2 = default(uint);

                            int inc = 0;

                            var ipBak = cpu.ip;

                            if (operand_size)
                            {
                                (dd1, inc) = GetMemoryData32(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                cpu.ip += (ushort)inc;

                                dd2 = (UInt32)(Int32)(sbyte)GetMemoryData8(cpu.cs, cpu.ip);

                                ++cpu.ip; // immediate の分
                            }
                            else
                            {
                                (dw1, inc) = GetMemoryData16(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                cpu.ip += (ushort)inc;

                                dw2 = (UInt16)(Int32)(sbyte)GetMemoryData8(cpu.cs, cpu.ip);

                                ++cpu.ip; // immediate の分
                            }

                            (db1, dw1, dd1) = Calc(cpu, true, operand_size, db1, db2, dw1, dw2, dd1, dd2, kind);

                            if (operand_size)
                            {
                                SetMemoryData32(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, ipBak), dd1, address_size);
                            }
                            else
                            {
                                SetMemoryData16(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, ipBak), dw1, address_size);
                            }
                        }
                        break;

                    case 0x84:
                    case 0x85:
                        {
                            Write("TEST");

                            var w = 0 != (b & 0x1);

                            var d1 = GetMemoryData8(cpu.cs, cpu.ip);

                            var mod = ((d1 >> 6) & 0x3);
                            var reg = ((d1 >> 3) & 0x7);
                            var rm = (d1 & 0x7);

                            ++cpu.ip;

                            int inc = 0;

                            var db1 = default(byte);
                            var db2 = default(byte);
                            var dw1 = default(ushort);
                            var dw2 = default(ushort);
                            var dd1 = default(uint);
                            var dd2 = default(uint);

                            if (w)
                            {
                                if (operand_size)
                                {
                                    (dd1, inc) = GetMemoryData32(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                    cpu.ip += (ushort)inc;

                                    dd2 = GetRegData32(cpu, reg);
                                }
                                else
                                {
                                    (dw1, inc) = GetMemoryData16(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                    cpu.ip += (ushort)inc;

                                    dw2 = GetRegData16(cpu, reg);
                                }
                            }
                            else
                            {
                                (db1, inc) = GetMemoryData8(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                cpu.ip += (ushort)inc;

                                db2 = GetRegData8(cpu, reg);
                            }

                            Calc(cpu, w, operand_size, db1, db2, dw1, dw2, dd1, dd2, 4);
                        }
                        break;
                    case 0x88:
                    case 0x89:
                        // Register to Memory
                        {
                            Write("MOV");

                            var w = 0 != (b & 0x1);

                            var d1 = GetMemoryData8(cpu.cs, cpu.ip);

                            var mod = ((d1 >> 6) & 0x3);
                            var reg = ((d1 >> 3) & 0x7);
                            var rm = (d1 & 0x7);

                            ++cpu.ip;

                            if (w)
                            {
                                if (operand_size)
                                {
                                    (var dw, var inc) = GetMemoryData32(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);
                                    cpu.ip += (ushort)inc;

                                    SetRegData32(cpu, reg, dw);
                                }
                                else
                                {
                                    (var dw, var inc) = GetMemoryData16(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);
                                    cpu.ip += (ushort)inc;

                                    SetRegData16(cpu, reg, dw);
                                }
                            }
                            else
                            {
                                (var db, var inc) = GetMemoryData8(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);
                                cpu.ip += (ushort)inc;

                                SetRegData8(cpu, reg, db);
                            }
                        }
                        break;
                    case 0x8a:
                    case 0x8b:
                        // Memory to Register
                        {
                            Write("MOV");

                            var w = 0 != (b & 0x1);

                            var d1 = GetMemoryData8(cpu.cs, cpu.ip);

                            var mod = ((d1 >> 6) & 0x3);
                            var reg = ((d1 >> 3) & 0x7);
                            var rm = (d1 & 0x7);

                            ++cpu.ip;

                            (var addr2, var inc) = GetMemoryAddr(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);
                            cpu.ip += (ushort)inc;

                            if (w)
                            {
                                if (operand_size)
                                {
                                    var dd = GetRegData32(cpu, reg);
                                    SetMemoryData32(addr2, dd);
                                }
                                else
                                {
                                    var dw = GetRegData16(cpu, reg);
                                    SetMemoryData16(addr2, dw);
                                }
                            }
                            else
                            {
                                var dw = GetRegData8(cpu, reg);
                                SetMemoryData8(addr2, dw);
                            }
                        }
                        break;
                    case 0x8c:  //  MOV r/m16,Sreg**
                        {
                            Write("MOV");

                            var d1 = GetMemoryData8(cpu.cs, cpu.ip);

                            var mod = ((d1 >> 6) & 0x3);
                            var sreg = ((d1 >> 3) & 0x7);
                            var rm = (d1 & 0x7);

                            ++cpu.ip;

                            var dw = GetSReg3(cpu, sreg);

                            var inc = SetMemoryData16(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), dw, address_size);

                            cpu.ip += (ushort)inc;
                        }
                        break;
                    case 0x8d:
                        {
                            Write("LEA");

                            var d1 = GetMemoryData8(cpu.cs, cpu.ip);

                            var mod = ((d1 >> 6) & 0x3);
                            var reg = ((d1 >> 3) & 0x7);
                            var rm = (d1 & 0x7);

                            ++cpu.ip;

                            (var addr2, var inc) = GetMemoryAddr(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                            cpu.ip += (ushort)inc;

                            if (operand_size)
                            {
                                SetRegData32(cpu, reg, addr2);
                            }
                            else
                            {
                                SetRegData16(cpu, reg, (ushort)addr2);
                            }
                        }
                        break;
                    case 0x8e:
                        {
                            Write("MOV");

                            var d1 = GetMemoryData8(cpu.cs, cpu.ip);

                            var mod = ((d1 >> 6) & 0x3);
                            var sreg = ((d1 >> 3) & 0x7);
                            var rm = (d1 & 0x7);

                            ++cpu.ip;

                            (ushort dw, int inc) = GetMemoryData16(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                            cpu.ip += (ushort)inc;

                            SetSReg3(cpu, sreg, dw);
                        }
                        break;

                    case 0xA0:
                    case 0xA1:
                        {
                            Write("MOV");

                            var w = 0 != (b & 0x1);

                            var addr2 =
                                segment == default(UInt16?) ?
                                ((UInt32)cpu.ds) * 0x10 :
                                ((UInt32)segment) * 0x10 +
                                (UInt32)GetMemoryData16(cpu.cs, cpu.ip);

                            cpu.ip += 2;

                            if (w)
                            {
                                cpu.ax = GetMemoryData16(addr2);
                            }
                            else
                            {
                                cpu.al = GetMemoryData8(addr2);
                            }
                        }
                        break;

                    case 0xb0:
                    case 0xb1:
                    case 0xb2:
                    case 0xb3:

                    case 0xb4:
                    case 0xb5:
                    case 0xb6:
                    case 0xb7:

                    case 0xb8:
                    case 0xb9:
                    case 0xba:
                    case 0xbb:

                    case 0xbc:
                    case 0xbd:
                    case 0xbe:
                    case 0xbf:
                        {
                            Write("MOV");

                            var w = 0 != (b & 0x08);
                            var reg = b & 0x07;

                            if (operand_size)
                            {
                                if (w)
                                {
                                    var data = GetMemoryData32(cpu.cs, cpu.ip);

                                    SetRegData32(cpu, reg, data);

                                    cpu.ip += 4;
                                }
                                else
                                {
                                    var data = GetMemoryData16(cpu.cs, cpu.ip);

                                    SetRegData16(cpu, reg, data);

                                    cpu.ip += 2;
                                }
                            }
                            else
                            {
                                var data = GetMemoryData8(cpu.cs, cpu.ip);

                                SetRegData8(cpu, reg, data);

                                ++cpu.ip;
                            }
                        }
                        break;
                    case 0xC3:
                        {
                            // RET
                            Write("RET");

                            var stack_addr = (UInt32)cpu.sp + ((UInt32)cpu.ss) * 0x10;

                            if (operand_size)
                            {
                                cpu.eip = GetMemoryData32(stack_addr);

                                ++cpu.sp;
                                ++cpu.sp;
                                ++cpu.sp;
                                ++cpu.sp;
                            }
                            else
                            {
                                cpu.ip = GetMemoryData16(stack_addr);

                                ++cpu.sp;
                                ++cpu.sp;
                            }
                        }
                        break;
                    case 0xe9:
                        {
                            // jmp relative 
                            Write("JMP");

                            var data = (short)GetMemoryData16(cpu.cs, cpu.ip);

                            ++cpu.ip;
                            ++cpu.ip;

                            cpu.ip = (ushort)(cpu.ip + (short)data);
                        }
                        break;
                    // jmp 
                    case 0xea:
                        {
                            Write("JMP");

                            var offset = GetMemoryData16(cpu.cs, cpu.ip);
                            ++cpu.ip;
                            ++cpu.ip;
                            cpu.cs = GetMemoryData16(cpu.cs, cpu.ip);
                            cpu.ip = offset;
                        }
                        break;
                    case 0xC6:
                    case 0xC7:
                        {
                            Write("MOV");

                            var d1 = GetMemoryData8(cpu.cs, cpu.ip);

                            var w = 0 != (b & 1);

                            var mod = ((d1 >> 6) & 0x3);
                            var type = ((d1 >> 3) & 0x7);
                            var rm = (d1 & 0x7);

                            ++cpu.ip;

                            switch (type)
                            {
                                case 0:
                                    {
                                        if (w)
                                        {
                                            (var addr2, var inc) = GetMemoryAddr(cpu, mod, rm, segment, GetMemoryDatas(cpu.cs, cpu.ip), address_size);

                                            cpu.ip += (ushort)inc;

                                            SetMemoryData16(addr2, GetMemoryData16(cpu.cs, cpu.ip));

                                            ++cpu.ip;
                                            ++cpu.ip;
                                        }
                                        else
                                        {
                                            throw new NotImplementedException();
                                        }
                                    }
                                    break;
                                default:
                                    throw new NotImplementedException();
                            }
                        }
                        break;
                    case 0xE4:
                    case 0xE5:
                        {
                            Write("IN");
                            var w = 0 != (b & 1);
                            var port = GetMemoryData8(cpu.cs, cpu.ip);
                            ++cpu.ip;

                            if (w)
                            {
                                cpu.ax = 0;
                            }
                            else
                            {
                                cpu.al = 0;
                            }
                        }
                        break;
                    case 0xE6:
                    case 0xE7:
                        {
                            Write("OUT");
                            var w = 0 != (b & 1);
                            var port = GetMemoryData8(cpu.cs, cpu.ip);
                            ++cpu.ip;
                        }
                        break;
                    case 0xE8:
                        {
                            Write("CALL");

                            if (operand_size)
                            {
                                var data = (int)GetMemoryData32(cpu.cs, cpu.ip);

                                ++cpu.ip;
                                ++cpu.ip;
                                ++cpu.ip;
                                ++cpu.ip;

                                --cpu.sp;
                                --cpu.sp;
                                --cpu.sp;
                                --cpu.sp;

                                var stack_addr = (UInt32)cpu.sp + ((UInt32)cpu.ss) * 0x10;

                                SetMemoryData32(stack_addr, cpu.eip);

                                cpu.ip = (ushort)(cpu.ip + (short)data);
                            }
                            else
                            {
                                var data = (short)GetMemoryData16(cpu.cs, cpu.ip);

                                ++cpu.ip;
                                ++cpu.ip;

                                --cpu.sp;
                                --cpu.sp;

                                var stack_addr = (UInt32)cpu.sp + ((UInt32)cpu.ss) * 0x10;

                                SetMemoryData16(stack_addr, cpu.ip);

                                cpu.ip = (ushort)(cpu.ip + data);
                            }
                        }
                        break;
                    case 0xFA:
                        {
                            Write("CLI");
                        }
                        break;
                    case 0xFB:
                        {
                            Write("STI");
                        }
                        break;
                    case 0xFC:
                        {
                            Write("CLD");
                            cpu.df = false;
                        }
                        break;
                    case 0xFD:
                        {
                            Write("STD");
                            cpu.df = true;
                        }
                        break;
                    case 0xFF:
                        {
                            var d1 = GetMemoryData8(cpu.cs, cpu.ip);

                            var mod = ((d1 >> 6) & 0x3);
                            var type = ((d1 >> 3) & 0x7);
                            var rm = (d1 & 0x7);
                            ++cpu.ip;

                            throw new NotImplementedException();
                        }
                        break;

                    default:
                        throw new NotImplementedException();
                }
            }
        }

        private static (byte, ushort, uint) Calc(CPU cpu, bool w, bool operand_size, byte db1, byte db2, ushort dw1, ushort dw2, uint dd1, uint dd2, int kind)
        {
            switch (kind)
            {
                case 0:// ADD
                    {
                        Write("ADD");
                        if (w)
                        {
                            if (operand_size)
                            {
                                cpu.update_eflags_add32(dd1, dd2);

                                dd1 = dd1 - dd2;
                            }
                            else
                            {
                                cpu.update_eflags_add32((UInt32)dw1, (UInt32)dw2);

                                dw1 = (ushort)((UInt32)dw1 + (UInt32)dw2);
                            }
                        }
                        else
                        {
                            cpu.update_eflags_add32((UInt32)db1, (UInt32)db2);

                            db1 = (byte)((UInt32)db1 + (UInt32)db2);
                        }
                    }
                    break;
                case 1:// OR
                    {
                        Write("OR");
                        if (w)
                        {
                            if (operand_size)
                            {
                                dd1 = dd1 | dd2;
                            }
                            else
                            {
                                dw1 = (ushort)(dw1 | dw2);
                            }
                        }
                        else
                        {
                            db1 = (byte)(db1 | db2);
                        }
                    }
                    break;
                case 2:// ADC
                    {
                        Write("ADC");
                        if (w)
                        {
                            if (operand_size)
                            {
                                var data2 = dd2 + (UInt32)(cpu.cf ? 1 : 0);

                                cpu.update_eflags_add32(dd1, data2);

                                dd1 = dd1 - data2;
                            }
                            else
                            {
                                var data2 = dw2 + (UInt32)(cpu.cf ? 1 : 0);

                                cpu.update_eflags_add32((UInt32)dw1, data2);

                                dw1 = (ushort)((UInt32)dw1 - data2);
                            }
                        }
                        else
                        {
                            var data2 = db2 + (UInt32)(cpu.cf ? 1 : 0);

                            cpu.update_eflags_add32((UInt32)db1, data2);

                            db1 = (byte)((UInt32)db1 - data2);
                        }
                    }
                    break;
                case 3:// SBB
                    {
                        Write("SBB");
                        if (w)
                        {
                            if (operand_size)
                            {
                                var data2 = dd2 + (UInt32)(cpu.cf ? 1 : 0);

                                cpu.update_eflags_sub32(dd1, data2);

                                dd1 = dd1 - data2;
                            }
                            else
                            {
                                var data2 = dw2 + (UInt32)(cpu.cf ? 1 : 0);

                                cpu.update_eflags_sub32((UInt32)dw1, data2);

                                dw1 = (ushort)((UInt32)dw1 - data2);
                            }
                        }
                        else
                        {
                            var data2 = db2 + (UInt32)(cpu.cf ? 1 : 0);

                            cpu.update_eflags_sub32((UInt32)db1, data2);

                            db1 = (byte)((UInt32)db1 - data2);
                        }
                    }
                    break;
                case 4:// AND
                    {
                        Write("AND");
                        if (w)
                        {
                            if (operand_size)
                            {
                                dd1 = dd1 & dd2;
                                cpu.update_eflags32(dd1);
                            }
                            else
                            {
                                dw1 = (ushort)(dw1 & dw2);
                                cpu.update_eflags32(dw1);
                            }
                        }
                        else
                        {
                            db1 = (byte)(db1 & db2);
                            cpu.update_eflags32(db1);
                        }
                    }
                    break;
                case 5:// SUB
                    {
                        Write("SUB");
                        if (w)
                        {
                            if (operand_size)
                            {
                                cpu.update_eflags_sub32(dd1, dd2);

                                dd1 = dd1 - dd2;
                            }
                            else
                            {
                                cpu.update_eflags_sub32((UInt32)dw1, (UInt32)dw2);

                                dw1 = (ushort)((UInt32)dw1 - (UInt32)dw2);
                            }
                        }
                        else
                        {
                            cpu.update_eflags_sub32((UInt32)db1, (UInt32)db2);

                            db1 = (byte)((UInt32)db1 - (UInt32)db2);
                        }
                    }
                    break;
                case 6:// XOR
                    {
                        Write("XOR");
                        if (w)
                        {
                            if (operand_size)
                            {
                                dd1 = dd1 ^ dd2;
                            }
                            else
                            {
                                dw1 = (ushort)(dw1 ^ dw2);
                            }
                        }
                        else
                        {
                            db1 = (byte)(db1 ^ db2);
                        }
                    }
                    break;
                case 7:// CMP
                    {
                        Write("CMP");
                        if (w)
                        {
                            if (operand_size)
                            {
                                cpu.update_eflags_sub32(dd1, dd2);
                            }
                            else
                            {
                                cpu.update_eflags_sub32(dw1, dw2);
                            }
                        }
                        else
                        {
                            cpu.update_eflags_sub32(db1, db2);
                        }
                    }
                    break;
                default:
                    throw new NotImplementedException();
            }
            return (db1, dw1, dd1);
        }
    }
}
