
using System;
using System.Collections.Generic;
using System.Linq;
using static Emu86.CPU;
using static Emu86.Ext;

namespace Emu86
{
    static public partial class Ext
    {
        static int[] arrLen = { 1, 2, 4 };

        static public State<((int type, byte db, ushort dw, uint dd) data, (bool isMem, uint addr) input)> GetMemoryDataIp(
            (Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size) prefixes,
            Boolean w) =>
            from cpu in GetCpu
            let addr = GetMemoryAddr(cpu.cs, cpu.ip)
            from data in GetMemOrRegData(addr, prefixes, w)
            from _ in IpInc(arrLen.ElementAt(data.type))
            select (data, (addr.isMem, addr.addr));

        static public State<(int type, byte db, ushort dw, uint dd)> GetMemoryDataIp_(int type) =>
            from data in GetMemoryDataIp(arrLen.ElementAt(type))
            select ToTypeData(data, type);

        static public State<Unit> SetCrReg(int reg, uint data) =>
            Choice(
                reg,
                (0, _cr0),
                (2, _cr2),
                (3, _cr3)
            ).SetCpu(data);

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
            from addr in GetMemoryAddr(segment, offset).ToState()
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
                GetMemOrRegData8(t),
                GetMemOrRegData16(t),
                GetMemOrRegData32(t)
            );

        static public State<Unit> SetMemOrRegData(
            (bool isMem, uint addr) t,
            (int type, byte db, ushort dw, uint dd) data) =>
            Choice_
            (
                data.type,
                SetMemOrRegData8(t, data.db),
                SetMemOrRegData16(t, data.dw),
                SetMemOrRegData32(t, data.dd)
            );

        static public State<Unit> SetRegData(int reg, (int type, byte db, ushort dw, uint dd) data) =>
            Choice_
            (
                data.type,
                SetRegData8(reg, data.db),
                SetRegData16(reg, data.dw),
                SetRegData32(reg, data.dd)
            );

        static public State<(int type, byte db, ushort dw, uint dd)> GetRegData(int reg, int type) =>
            Choice(
                type,
                GetRegData8(reg),
                GetRegData16(reg),
                GetRegData32(reg)
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

        static public State<(int type, byte db, ushort dw, uint dd)> Choice(
            int index, State<byte> dbState, State<ushort> dwState, State<uint> ddState) =>
            Choice_(
                index,
                dbState.Select(ToTypeData),
                dwState.Select(ToTypeData),
                ddState.Select(ToTypeData)
            );


        static public State<(int type, byte db, ushort dw, uint dd)> Calc
            (int type, byte db1, ushort dw1, uint dd1, byte db2, ushort dw2, uint dd2, int kind) =>
            Choice_(
                kind,
                Choice( // ADD
                    type,
                    from _ in update_eflags_add(db1, db2)
                    select ((byte)((uint)db1 + (uint)db2)),
                    from _ in update_eflags_add(dw1, dw2)
                    select ((ushort)((uint)dw1 + (uint)dw2)),
                    from _ in update_eflags_add(dd1, dd2)
                    select (dd1 + dd2)
                ),
                Choice( // OR
                    type,
                    from _db in ((byte)(db1 | db2)).ToState()
                    from _ in update_eflags(_db)
                    select _db,
                    from _dw in ((ushort)(dw1 | dw2)).ToState()
                    from _ in update_eflags(_dw)
                    select _dw,
                    from _dd in (dd1 | dd2).ToState()
                    from _ in update_eflags(_dd)
                    select _dd
                ),
                Choice( // ADC
                    type,
                    from cpu in GetCpu
                    let _data2 = (byte)(db2 + (cpu.cf ? 1 : 0))
                    from _ in update_eflags_add(db1, _data2)
                    select ((byte)((uint)db1 + _data2)),
                    from cpu in GetCpu
                    let _data2 = (ushort)(dw2 + (cpu.cf ? 1 : 0))
                    from _ in update_eflags_add(dw1, _data2)
                    select ((ushort)((uint)dw1 + _data2)),
                    from cpu in GetCpu
                    let _data2 = (uint)(dd2 + (cpu.cf ? 1 : 0))
                    from _ in update_eflags_add(dd1, _data2)
                    select (dd1 + _data2)
                ),
                Choice( // SBB
                    type,
                    from cpu in GetCpu
                    let _data2 = (byte)(db2 + (byte)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_sub(db1, _data2)
                    select ((byte)((uint)db1 - _data2)),
                    from cpu in GetCpu
                    let _data2 = (ushort)(dw2 + (cpu.cf ? 1 : 0))
                    from _ in update_eflags_sub(dw1, _data2)
                    select ((ushort)((uint)dw1 - _data2)),
                    from cpu in GetCpu
                    let _data2 = (uint)(dd2 + (cpu.cf ? 1 : 0))
                    from _ in update_eflags_sub(dd1, _data2)
                    select (db1 - _data2)
                ),
                Choice( // AND
                    type,
                    from _db in ((byte)(db1 & db2)).ToState()
                    from _ in update_eflags(_db)
                    select _db,
                    from _dw in ((ushort)(dw1 & dw2)).ToState()
                    from _ in update_eflags(_dw)
                    select _dw,
                    from _dd in (dd1 & dd2).ToState()
                    from _ in update_eflags(_dd)
                    select _dd
                ),
                Choice( // SUB
                    type,
                    from _ in update_eflags_sub(db1, db2)
                    select ((byte)(db1 - db2)),
                    from _ in update_eflags_sub(dw1, dw2)
                    select ((ushort)(dw1 - dw2)),
                    from _ in update_eflags_sub(dd1, dd2)
                    select (dd1 - dd2)
                ),
                Choice( // XOR
                    type,
                    from _db in ((byte)(db1 ^ db2)).ToState()
                    from _ in update_eflags(_db)
                    select _db,
                    from _dw in ((ushort)(dw1 ^ dw2)).ToState()
                    from _ in update_eflags(_dw)
                    select _dw,
                    from _dd in (dd1 ^ dd2).ToState()
                    from _ in update_eflags(_dd)
                    select _dd
                ),
                Choice( // CMP
                    type,
                    from _ in update_eflags_sub(db1, db2)
                    select db1,
                    from _ in update_eflags_sub(dw1, dw2)
                    select dw1,
                    from _ in update_eflags_sub(dd1, dd2)
                    select dd1
                )
            );

        static public
           State<(int type, byte db, ushort dw, uint dd)> Calc(
           (int type, byte db, ushort dw, uint dd) data1,
           (int type, byte db, ushort dw, uint dd) data2,
           int kind)
        {
            var (type1, db1, dw1, dd1) = data1;
            var (type2, db2, dw2, dd2) = data2;
            if (type1 != type2)
                throw new Exception();
            return Calc(type1, db1, dw1, dd1, db2, dw2, dd2, kind);
        }

        static public State<byte> GetRegData8_(int reg) =>
            GetDataFromCpu(cpu => EnvGetRegData8(cpu, reg));

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

        static public (bool isMem, uint addr) GetMemoryAddr
            (
                ushort segment,
                ushort offset
            ) => (true, ((uint)segment) * 0x10 + offset);

        static private Func<CPU, int, T> EnvGetDataFromCPU<T>(Accessor<CPU, T>[] array) => (cpu, reg) => array.ElementAt(reg).getter(cpu);

        static private Accessor<CPU, byte>[] ArrayReg8 = new Accessor<CPU, byte>[]
        {
            CPU._al,CPU._cl,CPU._dl,CPU._bl,
            CPU._ah,CPU._ch,CPU._dh,CPU._bh
        };
        static private Accessor<CPU, ushort>[] ArrayReg16 = new Accessor<CPU, ushort>[]
        {
            CPU._ax,CPU._cx,CPU._dx,CPU._bx,
            CPU._sp,CPU._bp,CPU._si,CPU._di
        };
        static private Accessor<CPU, uint>[] ArrayReg32 = new Accessor<CPU, uint>[]
        {
            CPU._eax,CPU._ecx,CPU._edx,CPU._ebx,
            CPU._esp,CPU._ebp,CPU._esi,CPU._edi
        };
        static private Accessor<CPU, ushort>[] ArraySreg = new Accessor<CPU, ushort>[]
        {
            CPU._es,CPU._cs,CPU._ss,CPU._ds,CPU._fs,CPU._gs
        };

        static private Func<CPU, int, ushort> GetSReg3 = EnvGetDataFromCPU(ArraySreg);

        static public Func<CPU, int, byte> EnvGetRegData8 = EnvGetDataFromCPU(ArrayReg8);
        static public Func<CPU, int, ushort> EnvGetRegData16 = EnvGetDataFromCPU(ArrayReg16);
        static public Func<CPU, int, uint> EnvGetRegData32 = EnvGetDataFromCPU(ArrayReg32);

        static public Func<CPU, int, byte, CPU> EnvSetRegData8 = EnvSetDataFromCPU(ArrayReg8);
        static public Func<CPU, int, ushort, CPU> EnvSetRegData16 = EnvSetDataFromCPU(ArrayReg16);
        static public Func<CPU, int, uint, CPU> EnvSetRegData32 = EnvSetDataFromCPU(ArrayReg32);

        static public Func<CPU, int, ushort, CPU> EnvSetSReg3 = EnvSetDataFromCPU(ArraySreg);

        static public State<byte> GetRegData8(int reg) =>
            GetDataFromCpu(cpu => EnvGetRegData8(cpu, reg));

        static public State<ushort> GetRegData16(int reg) =>
            GetDataFromCpu(cpu => EnvGetRegData16(cpu, reg));

        static public State<uint> GetRegData32(int reg) =>
            GetDataFromCpu(cpu => EnvGetRegData32(cpu, reg));

        static public State<Unit> SetRegData8(int reg, byte db) =>
            SetCpu(cpu => EnvSetRegData8(cpu, reg, db));

        static public State<Unit> SetRegData16(int reg, ushort dw) =>
            SetCpu(cpu => EnvSetRegData16(cpu, reg, dw));

        static public State<Unit> SetRegData32(int reg, uint dd) =>
            SetCpu(cpu => EnvSetRegData32(cpu, reg, dd));

        static public State<byte> GetMemOrRegData8((bool isMem, uint addr) t) =>
            GetDataFromEnvCpu((env, cpu) => EnvGetMemOrRegData8_(env, t.isMem, t.addr, cpu));
        static public State<ushort> GetMemOrRegData16((bool isMem, uint addr) t) =>
            GetDataFromEnvCpu((env, cpu) => EnvGetMemOrRegData16_(env, t.isMem, t.addr, cpu));
        static public State<uint> GetMemOrRegData32((bool isMem, uint addr) t) =>
            GetDataFromEnvCpu((env, cpu) => EnvGetMemOrRegData32_(env, t.isMem, t.addr, cpu));

        static public State<Unit> SetMemOrRegData8((bool isMem, uint addr) t, byte db) =>
            SetCpu((env, cpu) => EnvSetMemOrRegData8_(env, t.isMem, t.addr, cpu, db));
        static public State<Unit> SetMemOrRegData16((bool isMem, uint addr) t, ushort dw) =>
            SetCpu((env, cpu) => EnvSetMemOrRegData16_(env, t.isMem, t.addr, cpu, dw));
        static public State<Unit> SetMemOrRegData32((bool isMem, uint addr) t, uint dd) =>
            SetCpu((env, cpu) => EnvSetMemOrRegData32_(env, t.isMem, t.addr, cpu, dd));

        static public State<Unit> IpInc(int inc) =>
            SetCpu(cpu => { cpu.ip = (ushort)(cpu.ip + inc); return cpu; });

        static public State<(bool isMem, uint addr)> GetMemOrRegAddr
            (
                int mod,
                int rm,
                (Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size) prefixes
            ) =>
            from data in GetDataFromEnvCpu((env, cpu) => EnvGetMemOrRegAddr(env, cpu, mod, rm, cpu.cs, EnvGetMemoryDatas(env, cpu.cs, cpu.ip), prefixes.address_size))
            from _ in IpInc(data.inc)
            select (data.isMem, data.addr);

        static public State<uint> GetCrReg(int reg) =>
            Choice(
                reg,
                (0, GetDataFromCpu(_cr0)),
                (2, GetDataFromCpu(_cr2)),
                (3, GetDataFromCpu(_cr3))
            );

        static public State<Unit> SetCpu(Func<CPU, CPU> func) =>
            from cpu in GetCpu
            from _ in SetCpu(func(cpu))
            select _;

        static public State<T> GetDataFromCpu<T>(Func<CPU, T> func) =>
            from cpu in GetCpu
            select func(cpu);

        static public State<Unit> SetCpu(Func<EmuEnvironment, CPU, CPU> func) =>
            from cpu in GetDataFromEnvCpu(func)
            from _ in SetCpu(cpu)
            select _;

        static public State<Unit> SetCpu<T>(this Accessor<CPU, T> acc, T value) =>
            SetCpu(cpu => acc.setter(cpu)(value));

        static public State<T> GetDataFromCpu<T>(Accessor<CPU, T> acc) =>
            GetDataFromCpu(cpu => acc.getter(cpu));

        static public State<Unit> SetCpu<T>(params (Accessor<CPU, T> acc, T value)[] arr) =>
            arr.Select(
                item => SetCpu(cpu => item.acc.setter(cpu)(item.value))
            ).Sequence().Ignore();

        static public State<IEnumerable<byte>> GetMemoryDataIp(int length) =>
            from data in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryDatas(env, cpu.cs, cpu.ip, length))
            from _ in IpInc(data.Count())
            select data;

        static public State<byte> GetMemoryDataIp8
        {
            get
            {
                return
                    from data in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData8(env, cpu.cs, cpu.ip))
                    from _ in IpInc(1)
                    select data;
            }
        }

        static public State<ushort> GetMemoryDataIp16
        {
            get
            {
                return
                    from data in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData16(env, cpu.cs, cpu.ip))
                    from _ in IpInc(2)
                    select data;
            }
        }

        static public State<uint> GetMemoryDataIp32
        {
            get
            {
                return
                    from data in GetDataFromEnvCpu((env, cpu) => EnvGetMemoryData32(env, cpu.cs, cpu.ip))
                    from _ in IpInc(4)
                    select data;
            }
        }

        static public State<Unit> SetSReg3(int reg, (int type, byte db, ushort dw, uint dd) data) =>
            SetCpu(
                cpu =>
                {
                    var (type, db, dw, dd) = data;
                    switch (type)
                    {
                        case 0:
                        case 1:
                            cpu = EnvSetSReg3(cpu, reg, dw);
                            break;
                        case 2:
                            throw new NotImplementedException();
                        default:
                            throw new Exception();
                    }
                    return cpu;
                }
            );

        static public State<Unit> update_eflags(byte v) =>
            SetCpu(
                (_cf, false),
                (_zf, (0 == v)),
                (_sf, (0 != (v & 0x80))),
                (_of, false)
            );

        static public State<Unit> update_eflags(ushort v) =>
            SetCpu(
                (_cf, false),
                (_zf, (0 == v)),
                (_sf, (0 != (v & 0x8000))),
                (_of, false)
            );

        static public State<Unit> update_eflags(uint v) =>
            SetCpu(
                (_cf, false),
                (_zf, (0 == v)),
                (_sf, (0 != (v & 0x80000000))),
                (_of, false)
            );

        static public State<Unit> update_eflags_sub(byte v1, byte v2) =>
            SetCpu(
                (_cf, v1 < v2),
                (_zf, v1 == v2),
                (_sf, TopBit((byte)(v1 - v2))),
                (_of, (TopBit(v1) != TopBit(v2)) && (TopBit(v1) != TopBit((byte)(v1 - v2))))
            );
        static public State<Unit> update_eflags_sub(uint v1, uint v2) =>
            SetCpu(
                (_cf, v1 < v2),
                (_zf, v1 == v2),
                (_sf, TopBit(v1 - v2)),
                (_of, (TopBit(v1) != TopBit(v2)) && (TopBit(v1) != TopBit(v1 - v2)))
            );
        static public State<Unit> update_eflags_sub(ushort v1, ushort v2) =>
            SetCpu(
                (_cf, v1 < v2),
                (_zf, v1 == v2),
                (_sf, TopBit((ushort)(v1 - v2))),
                (_of, (TopBit(v1) != TopBit(v2)) && (TopBit(v1) != TopBit((ushort)(v1 - v2))))
            );

        static public State<Unit> update_eflags_add(byte v1, byte v2) =>
            SetCpu(
                (_cf, (v1 + v2) < v1),
                (_zf, 0 == (v1 + v2)),
                (_sf, TopBit((byte)(v1 + v2))),
                (_of, (TopBit(v1) == TopBit(v2)) && (TopBit(v1) != TopBit((byte)(v1 + v2))))
            );
        static public State<Unit> update_eflags_add(ushort v1, ushort v2) =>
            SetCpu(
                (_cf, (v1 + v2) < v1),
                (_zf, 0 == (v1 + v2)),
                (_sf, TopBit((ushort)(v1 + v2))),
                (_of, (TopBit(v1) == TopBit(v2)) && (TopBit(v1) != TopBit((ushort)(v1 + v2))))
            );
        static public State<Unit> update_eflags_add(uint v1, uint v2) =>
            SetCpu(
                (_cf, (v1 + v2) < v1),
                (_zf, 0 == (v1 + v2)),
                (_sf, TopBit(v1 + v2)),
                (_of, (TopBit(v1) == TopBit(v2)) && (TopBit(v1) != TopBit(v1 + v2)))
            );

        static public State<CPU> GetCpu { get { return GetDataFromEnvCpu((env, cpu) => cpu); } }

        /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        static public State<T> GetDataFromEnvCpu<T>(Func<EmuEnvironment, CPU, T> func) =>                              //無理
            (env, cpu) => (true, () => func(env, cpu), cpu, String.Empty);

        static public State<Unit> SetCpu(CPU new_cpu) => (env, cpu) => (true, () => Unit.unit, new_cpu, String.Empty); //無理
        static public State<Unit> SetResult(Boolean f) => (env, cpu) => (f, () => Unit.unit, cpu, String.Empty);        //無理
        static public State<Unit> SetLog(String log) => (env, cpu) => (true, () => Unit.unit, cpu, log);                //無理
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
        private ushort es { get; set; }
        public ushort ss { get; set; }
        private ushort fs { get; set; }
        private ushort gs { get; set; }

        public uint eip { get; set; }
        public ushort ip { get { return (ushort)(this.eip & 0xFFFF); } set { this.eip = (this.eip & 0xFFFF0000) + value; } }

        public ushort idt_limit { get; set; }
        public uint idt_base { get; set; }
        public ushort gdt_limit { get; set; }
        public uint gdt_base { get; set; }

        static public Accessor<CPU, uint> _cr0 = new Accessor<CPU, uint>(c => c.cr0, c => v => { c.cr0 = v; return c; });
        static public Accessor<CPU, uint> _cr2 = new Accessor<CPU, uint>(c => c.cr2, c => v => { c.cr2 = v; return c; });
        static public Accessor<CPU, uint> _cr3 = new Accessor<CPU, uint>(c => c.cr3, c => v => { c.cr3 = v; return c; });

        private uint cr0 { get; set; }
        private uint cr2 { get; set; }
        private uint cr3 { get; set; }

        static public Accessor<CPU, ushort> _sp = new Accessor<CPU, ushort>(c => c.sp, c => v => { c.sp = v; return c; });
        static public Accessor<CPU, ushort> _bp = new Accessor<CPU, ushort>(c => c.bp, c => v => { c.bp = v; return c; });

        public ushort bp { get { return (ushort)(this.ebp & 0xFFFF); } set { this.ebp = (this.ebp & 0xFFFF0000) + value; } }
        public ushort sp { get { return (ushort)(this.esp & 0xFFFF); } set { this.esp = (this.esp & 0xFFFF0000) + value; } }

        static public Accessor<CPU, uint> _esp = new Accessor<CPU, uint>(c => c.esp, c => v => { c.esp = v; return c; });
        static public Accessor<CPU, uint> _ebp = new Accessor<CPU, uint>(c => c.ebp, c => v => { c.ebp = v; return c; });

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

        static public Accessor<CPU, byte> _al = new Accessor<CPU, byte>(c => c.al, c => v => { c.al = v; return c; });
        static public Accessor<CPU, byte> _bl = new Accessor<CPU, byte>(c => c.bl, c => v => { c.bl = v; return c; });
        static public Accessor<CPU, byte> _cl = new Accessor<CPU, byte>(c => c.cl, c => v => { c.cl = v; return c; });
        static public Accessor<CPU, byte> _dl = new Accessor<CPU, byte>(c => c.dl, c => v => { c.dl = v; return c; });

        public byte al { get { return (byte)(this.eax & 0xFF); } set { this.eax = (this.eax & 0xFFFFFF00) + value; } }
        public byte bl { get { return (byte)(this.ebx & 0xFF); } set { this.ebx = (this.ebx & 0xFFFFFF00) + value; } }
        public byte cl { get { return (byte)(this.ecx & 0xFF); } set { this.ecx = (this.ecx & 0xFFFFFF00) + value; } }
        public byte dl { get { return (byte)(this.edx & 0xFF); } set { this.edx = (this.edx & 0xFFFFFF00) + value; } }

        static public Accessor<CPU, byte> _ah = new Accessor<CPU, byte>(c => c.ah, c => v => { c.ah = v; return c; });
        static public Accessor<CPU, byte> _bh = new Accessor<CPU, byte>(c => c.bh, c => v => { c.bh = v; return c; });
        static public Accessor<CPU, byte> _ch = new Accessor<CPU, byte>(c => c.ch, c => v => { c.ch = v; return c; });
        static public Accessor<CPU, byte> _dh = new Accessor<CPU, byte>(c => c.dh, c => v => { c.dh = v; return c; });

        public byte ah { get { return (byte)((this.eax >> 8) & 0xFF); } set { this.eax = (this.eax & 0xFFFF00FF) + ((uint)value << 8); } }
        public byte bh { get { return (byte)((this.ebx >> 8) & 0xFF); } set { this.ebx = (this.ebx & 0xFFFF00FF) + ((uint)value << 8); } }
        public byte ch { get { return (byte)((this.ecx >> 8) & 0xFF); } set { this.ecx = (this.ecx & 0xFFFF00FF) + ((uint)value << 8); } }
        public byte dh { get { return (byte)((this.edx >> 8) & 0xFF); } set { this.edx = (this.edx & 0xFFFF00FF) + ((uint)value << 8); } }

        static public Accessor<CPU, ushort> _ax = new Accessor<CPU, ushort>(c => c.ax, c => v => { c.ax = v; return c; });
        static public Accessor<CPU, ushort> _bx = new Accessor<CPU, ushort>(c => c.bx, c => v => { c.bx = v; return c; });
        static public Accessor<CPU, ushort> _cx = new Accessor<CPU, ushort>(c => c.cx, c => v => { c.cx = v; return c; });
        static public Accessor<CPU, ushort> _dx = new Accessor<CPU, ushort>(c => c.dx, c => v => { c.dx = v; return c; });

        public ushort ax { get { return (ushort)(this.eax & 0xFFFF); } set { this.eax = (this.eax & 0xFFFF0000) + value; } }
        public ushort bx { get { return (ushort)(this.ebx & 0xFFFF); } set { this.ebx = (this.ebx & 0xFFFF0000) + value; } }
        public ushort cx { get { return (ushort)(this.ecx & 0xFFFF); } set { this.ecx = (this.ecx & 0xFFFF0000) + value; } }
        public ushort dx { get { return (ushort)(this.edx & 0xFFFF); } set { this.edx = (this.edx & 0xFFFF0000) + value; } }

        static public Accessor<CPU, uint> _eax = new Accessor<CPU, uint>(c => c.eax, c => v => { c.eax = v; return c; });
        static public Accessor<CPU, uint> _ebx = new Accessor<CPU, uint>(c => c.ebx, c => v => { c.ebx = v; return c; });
        static public Accessor<CPU, uint> _ecx = new Accessor<CPU, uint>(c => c.ecx, c => v => { c.ecx = v; return c; });
        static public Accessor<CPU, uint> _edx = new Accessor<CPU, uint>(c => c.edx, c => v => { c.edx = v; return c; });

        public uint eax { get; set; }
        public uint ebx { get; set; }
        public uint ecx { get; set; }
        public uint edx { get; set; }

        static public Accessor<CPU, ushort> _si = new Accessor<CPU, ushort>(c => c.si, c => v => { c.si = v; return c; });
        static public Accessor<CPU, ushort> _di = new Accessor<CPU, ushort>(c => c.di, c => v => { c.di = v; return c; });

        public ushort si { get { return (ushort)(this.esi & 0xFFFF); } set { this.esi = (this.esi & 0xFFFF0000) + value; } }
        public ushort di { get { return (ushort)(this.edi & 0xFFFF); } set { this.edi = (this.edi & 0xFFFF0000) + value; } }

        static public Accessor<CPU, uint> _esi = new Accessor<CPU, uint>(c => c.esi, c => v => { c.esi = v; return c; });
        static public Accessor<CPU, uint> _edi = new Accessor<CPU, uint>(c => c.edi, c => v => { c.edi = v; return c; });

        public uint esi { get; set; }
        public uint edi { get; set; }
    }
}
