
using System;
using System.Collections.Generic;
using System.Linq;
using static Emu86.CPU;

namespace Emu86
{
    static public partial class Ext
    {
        static public State<((int type, byte db, ushort dw, uint dd) data, (bool isMem, uint addr) input)> GetMemoryDataIp(
            (Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size) prefixes,
            Boolean w) =>
            from cpu in GetCpu
            from addr in GetMemoryAddr(cpu.cs, cpu.ip)
            from data in GetMemOrRegData(addr, prefixes, w)
            from _ in SetCpu(cpu =>
            {
                switch (data.type)
                {
                    case 0:
                        cpu.ip += 1;
                        break;
                    case 1:
                        cpu.ip += 2;
                        break;
                    case 2:
                        cpu.ip += 4;
                        break;
                }
                return cpu;
            })
            select (data, (addr.isMem, addr.addr));

        static public State<(int type, byte db, ushort dw, uint dd)> GetMemoryDataIp_(int type) =>
            Choice(
                type,
                GetMemoryDataIp8.Select(ToTypeData),
                GetMemoryDataIp16.Select(ToTypeData),
                GetMemoryDataIp32.Select(ToTypeData)
            );

        static public State<Unit> SetCrReg(int reg, uint data) =>
            SetCpu(
                cpu =>
                {
                    switch (reg)
                    {
                        case 0:
                            cpu.cr0 = data;
                            break;
                        case 2:
                            cpu.cr2 = data;
                            break;
                        case 3:
                            cpu.cr3 = data;
                            break;
                        default:
                            throw new Exception();
                    }
                    return cpu;
                }
            );

        static public State<(int mod, int reg, int rm)> ModRegRm() =>
            from value in GetMemoryDataIp8
            let mod = ((value >> 6) & 0x3)
            let reg = ((value >> 3) & 0x7)
            let rm = (value & 0x7)
            select (mod, reg, rm);

        static public State<byte> Range(byte start, byte end) =>
            from value in GetMemoryDataIp8
            from _ in SetResult(start <= value && value <= end)
            select value;

        static public State<byte> Contains(params byte[] bs) =>
            from value in GetMemoryDataIp8
            from _ in SetResult(bs.Contains(value))
            select value;

        static public State<((int type, byte db, ushort dw, uint dd) data, (bool isMem, uint addr) input)> GetMemoryData
            (
                ushort segment,
                ushort offset,
                (Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size) prefixes,
                Boolean w
            ) =>
            from addr in GetMemoryAddr(segment, offset)
            from data in GetMemOrRegData(addr, prefixes, w)
            select (data, (addr.isMem, addr.addr));

        static public State<((int type, byte db, ushort dw, uint dd) data, (bool isMem, uint addr) input)> GetMemOrRegData
            (
                int mod,
                int rm,
                (Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size) prefixes,
                Boolean w
            ) =>
            from addr in GetMemOrRegAddr(mod, rm, prefixes)
            from data in GetMemOrRegData(addr, prefixes, w)
            select (data, (addr.isMem, addr.addr));

        static public State<(int type, byte db, ushort dw, uint dd)> GetMemOrRegData(
            (bool isMem, uint addr) t,
            (Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size) prefixes,
            Boolean w) =>
            Choice(
                w ? (prefixes.operand_size ? 2 : 1) : 0,
                GetMemOrRegData8(t).Select(ToTypeData),
                GetMemOrRegData16(t).Select(ToTypeData),
                GetMemOrRegData32(t).Select(ToTypeData)
            );

        static public State<Unit> SetMemOrRegData(
            (bool isMem, uint addr) t,
            (int type, byte db, ushort dw, uint dd) data) =>
            Choice
            (
                data.type,
                SetMemOrRegData8(t, data.db),
                SetMemOrRegData16(t, data.dw),
                SetMemOrRegData32(t, data.dd)
            );

        static public State<Unit> SetRegData(int reg, (int type, byte db, ushort dw, uint dd) data) =>
            Choice
            (
                data.type,
                SetRegData8(reg, data.db),
                SetRegData16(reg, data.dw),
                SetRegData32(reg, data.dd)
            );

        static public State<(int type, byte db, ushort dw, uint dd)> GetRegData(int reg, int type) =>
            Choice(
                type,
                GetRegData8(reg).Select(ToTypeData),
                GetRegData16(reg).Select(ToTypeData),
                GetRegData32(reg).Select(ToTypeData)
            );

        static public State<IEnumerable<byte>> Opecode(params byte[] bs) =>
            from datas in GetMemoryDataIp(bs.Length)
            from _ in SetResult(bs.SequenceEqual(datas))
            select datas;

        static public State<(Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size)> Prefixes
        {
            get
            {
                return
                    from data in Contains(0x2e, 0x26, 0x64, 0x64, 0x66, 0x67).Many0()
                    select (
                        data.Contains((byte)0x2e),
                        data.Contains((byte)0x26),
                        data.Contains((byte)0x64),
                        data.Contains((byte)0x65),
                        data.Contains((byte)0x66),
                        data.Contains((byte)0x67)
                    );
            }
        }

        static public State<(int type, byte db, ushort dw, uint dd)> Calc
            (int type, byte db1, ushort dw1, uint dd1, byte db2, ushort dw2, uint dd2, int kind) =>
            Choice(
                kind,
                Choice( // ADD
                    type,
                    from _ in update_eflags_add32((uint)db1, (uint)db2)
                    select ((byte)((uint)db1 + (uint)db2)).ToTypeData(),
                    from _ in update_eflags_add32((uint)dw1, (uint)dw2)
                    select ((ushort)((uint)dw1 + (uint)dw2)).ToTypeData(),
                    from _ in update_eflags_add32(dd1, dd2)
                    select (dd1 + dd2).ToTypeData()
                ),
                Choice( // OR
                    type,
                    ((byte)(db1 | db2)).ToTypeData().ToState(),
                    ((ushort)(dw1 | dw2)).ToTypeData().ToState(),
                    (dd1 | dd2).ToTypeData().ToState()
                ),
                Choice( // ADC
                    type,
                    from cpu in GetCpu
                    let _data2 = (db2 + (uint)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_add32((uint)db1, _data2)
                    select ((byte)((uint)db1 + _data2)).ToTypeData(),
                    from cpu in GetCpu
                    let _data2 = (dw2 + (uint)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_add32((uint)dw1, _data2)
                    select ((ushort)((uint)dw1 + _data2)).ToTypeData(),
                    from cpu in GetCpu
                    let _data2 = (dd2 + (uint)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_add32((uint)dd1, _data2)
                    select (dd1 + _data2).ToTypeData()
                ),
                Choice( // SBB
                    type,
                    from cpu in GetCpu
                    let _data2 = (db2 + (uint)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_sub32((uint)db1, _data2)
                    select ((byte)((uint)db1 - _data2)).ToTypeData(),
                    from cpu in GetCpu
                    let _data2 = (dw2 + (uint)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_sub32((uint)dw1, _data2)
                    select ((ushort)((uint)dw1 - _data2)).ToTypeData(),
                    from cpu in GetCpu
                    let _data2 = (dd2 + (uint)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_sub32((uint)dd1, _data2)
                    select (db1 - _data2).ToTypeData()
                ),
                Choice( // AND
                    type,
                    from _db in ((byte)(db1 & db2)).ToState()
                    from _ in update_eflags8(_db)
                    select _db.ToTypeData(),
                    from _dw in ((ushort)(dw1 & dw2)).ToState()
                    from _ in update_eflags16(_dw)
                    select _dw.ToTypeData(),
                    from _dd in (dd1 & dd2).ToState()
                    from _ in update_eflags32(_dd)
                    select _dd.ToTypeData()
                ),
                Choice( // SUB
                    type,
                    from _ in update_eflags_sub32((uint)db1, db2)
                    select ((byte)(db1 - db2)).ToTypeData(),
                    from _ in update_eflags_sub32((uint)dw1, dw2)
                    select ((ushort)(dw1 - dw2)).ToTypeData(),
                    from _ in update_eflags_sub32((uint)dd1, dd2)
                    select (dd1 - dd2).ToTypeData()
                ),
                Choice( // XOR
                    type,
                    ((byte)(db1 ^ db2)).ToTypeData().ToState(),
                    ((ushort)(dw1 ^ dw2)).ToTypeData().ToState(),
                    (dd1 ^ dd2).ToTypeData().ToState()
                ),
                Choice( // CMP
                    type,
                    from _ in update_eflags_sub32((uint)db1, db2)
                    select db1.ToTypeData(),
                    from _ in update_eflags_sub32((uint)dw1, dw2)
                    select dw1.ToTypeData(),
                    from _ in update_eflags_sub32((uint)dd1, dd2)
                    select dd1.ToTypeData()
                )
            );

        static public
           State<(int type, byte db, ushort dw, uint dd)> Calc(
           (int type, byte db, ushort dw, uint dd) data1,
           (int type, byte db, ushort dw, uint dd) data2,
           int kind)
        {
            (var type1, var db1, var dw1, var dd1) = data2;
            (var type2, var db2, var dw2, var dd2) = data2;
            if (type1 != type2)
                throw new Exception();
            return Calc(type1, db1, dw1, dd1, db2, dw2, dd2, kind);
        }

        static public State<byte> GetRegData8_(int reg) =>
            GetDataFromCpu(cpu => EmuEnvironment.GetRegData8(cpu, reg));

        static public State<bool> Jcc(int type) =>
            GetDataFromCpu(
                cpu => new[]
                {
                    cpu.of,                           // JO
                    !cpu.of,                          // JNO
                    cpu.cf,                           // JB
                    !cpu.cf,                          // JNB
                    cpu.zf,                           // JE
                    !cpu.zf,                          // JNE
                    cpu.cf || cpu.zf,                 // JBE
                    (!cpu.cf) && (!cpu.zf),           // JNBE
                    cpu.sf,                           // JS
                    !cpu.sf,                          // JNS
                    cpu.pf,                           // JP
                    !cpu.pf,                          // JNP
                    (cpu.sf != cpu.of) && (!cpu.zf),  // JL
                    cpu.sf == cpu.of,                 // JNL
                    cpu.sf != cpu.of,                 // JLE
                    (cpu.sf == cpu.of) && (!cpu.zf),  // JNLE

                }.ElementAt(type)
            );

        static public State<(bool isMem, uint addr)> GetMemoryAddr
            (
                ushort segment,
                ushort offset
            ) => (true, ((uint)segment) * 0x10 + offset).ToState();

        static public State<byte> GetRegData8(int reg) =>
            GetDataFromCpu(cpu => EmuEnvironment.GetRegData8(cpu, reg));

        static public State<ushort> GetRegData16(int reg) =>
            GetDataFromCpu(cpu => EmuEnvironment.GetRegData16(cpu, reg));

        static public State<uint> GetRegData32(int reg) =>
            GetDataFromCpu(cpu => EmuEnvironment.GetRegData32(cpu, reg));

        static public State<Unit> SetRegData8(int reg, byte db) =>
            SetCpu(cpu => EmuEnvironment.SetRegData8(cpu, reg, db));

        static public State<Unit> SetRegData16(int reg, ushort dw) =>
            SetCpu(cpu => EmuEnvironment.SetRegData16(cpu, reg, dw));

        static public State<Unit> SetRegData32(int reg, uint dd) =>
            SetCpu(cpu => EmuEnvironment.SetRegData32(cpu, reg, dd));

        static public State<byte> GetMemOrRegData8((bool isMem, uint addr) t) =>
            GetDataFromEnvCpu((env, cpu) => env.GetMemOrRegData8_(t.isMem, t.addr, cpu));
        static public State<ushort> GetMemOrRegData16((bool isMem, uint addr) t) =>
            GetDataFromEnvCpu((env, cpu) => env.GetMemOrRegData16_(t.isMem, t.addr, cpu));
        static public State<uint> GetMemOrRegData32((bool isMem, uint addr) t) =>
            GetDataFromEnvCpu((env, cpu) => env.GetMemOrRegData32_(t.isMem, t.addr, cpu));

        static public State<Unit> SetMemOrRegData8((bool isMem, uint addr) t, byte db) =>
            SetCpu((env, cpu) => env.SetMemOrRegData8_(t.isMem, t.addr, cpu, db));
        static public State<Unit> SetMemOrRegData16((bool isMem, uint addr) t, ushort dw) =>
            SetCpu((env, cpu) => env.SetMemOrRegData16_(t.isMem, t.addr, cpu, dw));
        static public State<Unit> SetMemOrRegData32((bool isMem, uint addr) t, uint dd) =>
            SetCpu((env, cpu) => env.SetMemOrRegData32_(t.isMem, t.addr, cpu, dd));

        static public State<Unit> IpInc(int inc) =>
            SetCpu(cpu => { cpu.ip = (ushort)(cpu.ip + inc); return cpu; });

        static public State<(bool isMem, uint addr)> GetMemOrRegAddr
            (
                int mod,
                int rm,
                (Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size) prefixes
            ) =>
            from data in GetDataFromEnvCpu((env, cpu) => env.GetMemOrRegAddr(cpu, mod, rm, cpu.cs, env.GetMemoryDatas(cpu.cs, cpu.ip), prefixes.address_size))
            from _ in IpInc(data.inc)
            select (data.isMem, data.addr);

        static public State<uint> GetCrReg(int reg) =>
            GetDataFromEnvCpu(
                (env, cpu) =>
                {
                    switch (reg)
                    {
                        case 0:
                            return cpu.cr0;
                        case 2:
                            return cpu.cr2;
                        case 3:
                            return cpu.cr3;
                        default:
                            throw new Exception();
                    }
                }
            );

        static public State<T> GetDataFromCpu<T>(Func<CPU, T> func) =>
            from cpu in GetCpu
            select func(cpu);

        static public State<Unit> SetCpu(Func<CPU, CPU> func) =>
            from cpu in GetCpu
            from _ in SetCpu(func(cpu))
            select _;

        static public State<Unit> SetCpu(Func<EmuEnvironment, CPU, CPU> func) =>
            from cpu in GetDataFromEnvCpu(func)
            from _ in SetCpu(cpu)
            select _;

        static public State<Unit> SetCpu<T>(params (Accessor<CPU, T> acc, T value)[] arr) =>
            arr.Select(
                item => SetCpu(cpu => item.acc.setter(cpu)(item.value))
            ).Sequence().Ignore();

        static public State<IEnumerable<byte>> GetMemoryDataIp(int length) =>
            from data in GetDataFromEnvCpu((env, cpu) => env.GetMemoryDatas(cpu.cs, cpu.ip, length))
            from _ in IpInc(data.Count())
            select data;

        static public State<byte> GetMemoryDataIp8
        {
            get
            {
                return
                    from data in GetDataFromEnvCpu((env, cpu) => env.GetMemoryData8(cpu.cs, cpu.ip))
                    from _ in IpInc(1)
                    select data;
            }
        }

        static public State<ushort> GetMemoryDataIp16
        {
            get
            {
                return
                    from data in GetDataFromEnvCpu((env, cpu) => env.GetMemoryData16(cpu.cs, cpu.ip))
                    from _ in IpInc(2)
                    select data;
            }
        }

        static public State<uint> GetMemoryDataIp32
        {
            get
            {
                return
                    from data in GetDataFromEnvCpu((env, cpu) => env.GetMemoryData32(cpu.cs, cpu.ip))
                    from _ in IpInc(4)
                    select data;
            }
        }

        static public State<Unit> SetSReg3(int reg, (int type, byte db, ushort dw, uint dd) data) =>
            SetCpu(
                cpu =>
                {
                    (var type, var db, var dw, var dd) = data;
                    switch (type)
                    {
                        case 0:
                        case 1:
                            cpu = EmuEnvironment.SetSReg3(cpu, reg, dw);
                            break;
                        case 2:
                            throw new NotImplementedException();
                        default:
                            throw new Exception();
                    }
                    return cpu;
                }
            );

        static public State<Unit> update_eflags8(byte v) =>
            SetCpu(
                (_cf, false),
                (_zf, (0 == v)),
                (_sf, (0 != (v & 0x80))),
                (_of, false)
            );

        static public State<Unit> update_eflags16(ushort v) =>
            SetCpu(
                (_cf, false),
                (_zf, (0 == v)),
                (_sf, (0 != (v & 0x8000))),
                (_of, false)
            );

        static public State<Unit> update_eflags32(uint v) =>
            SetCpu(
                (_cf, false),
                (_zf, (0 == v)),
                (_sf, (0 != (v & 0x80000000))),
                (_of, false)
            );

        static public State<Unit> update_eflags_sub32(uint v1, uint v2) =>
            SetCpu(
                (_cf, v1 < v2),
                (_zf, v1 == v2),
                (_sf, TopBit(v1 - v2)),
                (_of, (TopBit(v1) != TopBit(v2)) && (TopBit(v1) != TopBit(v1 - v2)))
            );

        static public State<Unit> update_eflags_add32(uint v1, uint v2) =>
            SetCpu(
                (_cf, (v1 + v2) < v1),
                (_zf, 0 == (v1 + v2)),
                (_sf, TopBit(v1 + v2)),
                (_of, (TopBit(v1) == TopBit(v2)) && (TopBit(v1) != TopBit(v1 + v2)))
            );

        /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        static public State<CPU> GetCpu { get { return (env, cpu) => (true, () => cpu, cpu, String.Empty); } }          //無理

        static public State<T> GetDataFromEnvCpu<T>(Func<EmuEnvironment, CPU, T> func) =>　                             //無理
            (env, cpu) => (true, () => func(env, cpu), cpu, String.Empty);

        static public State<Unit> SetCpu(CPU new_cpu) => (env, cpu) => (true, () => Unit.unit, new_cpu, String.Empty);　//無理
        static public State<Unit> SetResult(Boolean f) => (env, cpu) => (f, () => Unit.unit, cpu, String.Empty);        //無理
        static public State<Unit> SetLog(String log) => (env, cpu) => (true, () => Unit.unit, cpu, log);                //無理

        static public (int type, byte db, ushort dw, uint dd) ToTypeData(this byte db) => (0, db: db, dw: default(ushort), dd: default(uint));
        static public (int type, byte db, ushort dw, uint dd) ToTypeData(this ushort dw) => (1, db: default(byte), dw: dw, dd: default(uint));
        static public (int type, byte db, ushort dw, uint dd) ToTypeData(this uint dd) => (2, db: default(byte), dw: default(ushort), dd: dd);

        static bool TopBit(uint data) => (0 != (data & 0x80000000));
    }

    public class Accessor<O, P>
    {
        public Func<O, P> getter { get; set; }
        public Func<O, Func<P, O>> setter { get; set; }

        public Accessor(Func<O, P> g, Func<O, Func<P, O>> s)
        {
            this.getter = g;
            this.setter = s;
        }
    }

    public struct CPU
    {
        static public Accessor<CPU, ushort> _cs = new Accessor<CPU, ushort>(c => c.cs, c => v => { c.cs = v; return c; });
        static public Accessor<CPU, ushort> _ds = new Accessor<CPU, ushort>(c => c.ds, c => v => { c.ds = v; return c; });
        static public Accessor<CPU, ushort> _es = new Accessor<CPU, ushort>(c => c.es, c => v => { c.es = v; return c; });
        static public Accessor<CPU, ushort> _ss = new Accessor<CPU, ushort>(c => c.ss, c => v => { c.ss = v; return c; });
        static public Accessor<CPU, ushort> _fs = new Accessor<CPU, ushort>(c => c.fs, c => v => { c.fs = v; return c; });
        static public Accessor<CPU, ushort> _gs = new Accessor<CPU, ushort>(c => c.gs, c => v => { c.gs = v; return c; });

        public ushort cs { get; set; }
        public ushort ds { get; set; }
        public ushort es { get; set; }
        public ushort ss { get; set; }
        public ushort fs { get; set; }
        public ushort gs { get; set; }

        public uint eip { get; set; }
        public ushort ip { get { return (ushort)(eip & 0xFFFF); } set { this.eip = (this.eip & 0xFFFF0000) + value; } }

        public ushort idt_limit { get; set; }
        public uint idt_base { get; set; }
        public ushort gdt_limit { get; set; }
        public uint gdt_base { get; set; }

        public uint cr0 { get; set; }
        public uint cr2 { get; set; }
        public uint cr3 { get; set; }

        public ushort bp { get { return (ushort)(ebp & 0xFFFF); } set { this.ebp = (this.ebp & 0xFFFF0000) + value; } }
        public ushort sp { get { return (ushort)(esp & 0xFFFF); } set { this.esp = (this.esp & 0xFFFF0000) + value; } }

        public uint ebp { get; set; }
        public uint esp { get; set; }

        public uint eflags { get; set; }

        private const uint CF = 1;
        private const uint PF = 4;
        private const uint AF = 0x10;
        private const uint ZF = 0x40;
        private const uint SF = 0x80;
        private const uint TF = 0x100;
        private const uint JF = 0x200;
        private const uint DF = 0x400;
        private const uint OF = 0x800;
        private const uint NT = 0x4000;

        private void UpdateEflags(uint flag, bool f)
        {
            this.eflags = ((this.eflags & ~flag) | (f ? flag : 0));
        }
        private bool GetEflags(uint flag) => 0 != (this.eflags & flag);

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

        static public Accessor<CPU, bool> _cf = new Accessor<CPU, bool>(c => c.cf, c => v => { c.cf = v; return c; });
        static public Accessor<CPU, bool> _pf = new Accessor<CPU, bool>(c => c.pf, c => v => { c.pf = v; return c; });
        static public Accessor<CPU, bool> _af = new Accessor<CPU, bool>(c => c.af, c => v => { c.af = v; return c; });
        static public Accessor<CPU, bool> _zf = new Accessor<CPU, bool>(c => c.zf, c => v => { c.zf = v; return c; });
        static public Accessor<CPU, bool> _sf = new Accessor<CPU, bool>(c => c.sf, c => v => { c.sf = v; return c; });
        static public Accessor<CPU, bool> _tf = new Accessor<CPU, bool>(c => c.tf, c => v => { c.tf = v; return c; });
        static public Accessor<CPU, bool> _jf = new Accessor<CPU, bool>(c => c.jf, c => v => { c.jf = v; return c; });
        static public Accessor<CPU, bool> _df = new Accessor<CPU, bool>(c => c.df, c => v => { c.df = v; return c; });
        static public Accessor<CPU, bool> _of = new Accessor<CPU, bool>(c => c.of, c => v => { c.of = v; return c; });
        static public Accessor<CPU, bool> _nt = new Accessor<CPU, bool>(c => c.nt, c => v => { c.nt = v; return c; });

        public byte al { get { return (byte)(eax & 0xFF); } set { this.eax = (this.eax & 0xFFFFFF00) + value; } }
        public byte bl { get { return (byte)(ebx & 0xFF); } set { this.ebx = (this.ebx & 0xFFFFFF00) + value; } }
        public byte cl { get { return (byte)(ecx & 0xFF); } set { this.ecx = (this.ecx & 0xFFFFFF00) + value; } }
        public byte dl { get { return (byte)(edx & 0xFF); } set { this.edx = (this.edx & 0xFFFFFF00) + value; } }

        public byte ah { get { return (byte)((eax >> 8) & 0xFF); } set { this.eax = (this.eax & 0xFFFF00FF) + ((uint)value << 8); } }
        public byte bh { get { return (byte)((ebx >> 8) & 0xFF); } set { this.ebx = (this.ebx & 0xFFFF00FF) + ((uint)value << 8); } }
        public byte ch { get { return (byte)((ecx >> 8) & 0xFF); } set { this.ecx = (this.ecx & 0xFFFF00FF) + ((uint)value << 8); } }
        public byte dh { get { return (byte)((edx >> 8) & 0xFF); } set { this.edx = (this.edx & 0xFFFF00FF) + ((uint)value << 8); } }

        public ushort ax { get { return (ushort)(eax & 0xFFFF); } set { this.eax = (this.eax & 0xFFFF0000) + value; } }
        public ushort bx { get { return (ushort)(ebx & 0xFFFF); } set { this.ebx = (this.ebx & 0xFFFF0000) + value; } }
        public ushort cx { get { return (ushort)(ecx & 0xFFFF); } set { this.ecx = (this.ecx & 0xFFFF0000) + value; } }
        public ushort dx { get { return (ushort)(edx & 0xFFFF); } set { this.edx = (this.edx & 0xFFFF0000) + value; } }

        public uint eax { get; set; }
        public uint ebx { get; set; }
        public uint ecx { get; set; }
        public uint edx { get; set; }

        public ushort si { get { return (ushort)(esi & 0xFFFF); } set { this.esi = (this.esi & 0xFFFF0000) + value; } }
        public ushort di { get { return (ushort)(edi & 0xFFFF); } set { this.edi = (this.edi & 0xFFFF0000) + value; } }

        public uint esi { get; set; }
        public uint edi { get; set; }
    }
}
