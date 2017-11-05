using System;

namespace Emu86
{
    static public partial class Ext
    {
        static public CPU update_eflags_sub32(this CPU cpu, UInt32 v1, UInt32 v2)
        {
            var result = (UInt64)v1 - (UInt64)v2;

            var signr = 0 != (result & 0x80000000);
            var sign1 = 0 != (v1 & 0x80000000);
            var sign2 = 0 != (v2 & 0x80000000);

            cpu.cf = 0 != (result & 0x100000000);
            cpu.zf = 0 == result;
            cpu.sf = 0 != (result & 0x80000000);
            cpu.of = (sign1 != sign2) && (sign1 != signr);

            return cpu;
        }
        static public CPU update_eflags_add16(this CPU cpu, UInt16 v1, UInt16 v2)
        {
            var result = (UInt32)v1 + (UInt32)v2;

            var signr = 0 != (result & 0x8000);
            var sign1 = 0 != (v1 & 0x8000);
            var sign2 = 0 != (v2 & 0x8000);

            cpu.cf = 0 != (result & 0x10000);
            cpu.zf = 0 == result;
            cpu.sf = 0 != (result & 0x8000);
            cpu.of = (sign1 == sign2) && (sign1 != signr);

            return cpu;
        }
        static public CPU update_eflags8(this CPU cpu, byte v)
        {
            cpu.cf = false;
            cpu.zf = (0 == v);
            cpu.sf = 0 != (v & 0x80);
            cpu.of = false;

            return cpu;
        }
        static public CPU update_eflags16(this CPU cpu, ushort v)
        {
            cpu.cf = false;
            cpu.zf = (0 == v);
            cpu.sf = 0 != (v & 0x8000);
            cpu.of = false;

            return cpu;
        }
        static public CPU update_eflags32(this CPU cpu, uint v)
        {
            cpu.cf = false;
            cpu.zf = (0 == v);
            cpu.sf = 0 != (v & 0x80000000);
            cpu.of = false;

            return cpu;
        }
        static public CPU update_eflags_sub16(this CPU cpu, UInt16 v1, UInt16 v2)
        {
            var result = (UInt32)v1 - (UInt32)v2;

            var signr = 0 != (result & 0x8000);
            var sign1 = 0 != (v1 & 0x8000);
            var sign2 = 0 != (v2 & 0x8000);

            cpu.cf = 0 != (result & 0x10000);
            cpu.zf = 0 == result;
            cpu.sf = 0 != (result & 0x8000);
            cpu.of = (sign1 != sign2) && (sign1 != signr);

            return cpu;
        }
        static public CPU update_eflags_add32(this CPU cpu, UInt32 v1, UInt32 v2)
        {
            var result = (UInt64)v1 + (UInt64)v2;

            var signr = 0 != (result & 0x80000000);
            var sign1 = 0 != (v1 & 0x80000000);
            var sign2 = 0 != (v2 & 0x80000000);

            cpu.cf = 0 != (result & 0x100000000);
            cpu.zf = 0 == result;
            cpu.sf = 0 != (result & 0x80000000);
            cpu.of = (sign1 == sign2) && (sign1 != signr);

            return cpu;
        }
    }
    public struct CPU
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
}
