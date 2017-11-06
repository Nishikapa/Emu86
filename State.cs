using System;
using System.Collections.Generic;
using System.Linq;
using static Emu86.Ext;
using static System.Console;

namespace Emu86
{
    public delegate (Boolean IsSuccess, Func<V> value, CPU cpu, String log) State<V>(EmuEnvironment env, CPU param);

    public class Unit
    {
        public static Unit unit = default(Unit);
    }

    static public partial class Ext
    {
        static public State<V> ToState<V>(this V value) => (env, cpu) => (true, () => value, cpu, String.Empty);

        static public State<B> Select<A, B>(this State<A> param, Func<A, B> selector) => (env, cpu1) =>
        {
            (var f, var value, var cpu2, var log) = param(env, cpu1);
            return (f, () => f ? selector(value()) : default(B), f ? cpu2 : cpu1, log);
        };

        static public State<C> SelectMany<A, B, C>(this State<A> param, Func<A, State<B>> selector, Func<A, B, C> projector) => (env, cpu1) =>
        {
            (var f1, var reta, var cpu2, var log1) = param(env, cpu1);
            if (f1)
            {
                (var f2, var retb, var cpu3, var log2) = selector(reta())(env, cpu2);
                return (f2, () => f2 ? projector(reta(), retb()) : default(C), f2 ? cpu3 : cpu1, log1 + "\r\n" + log2);
            }
            else
            {
                return (false, () => default(C), cpu1, log1);
            }
        };

        static public State<T> Choice<T>(params (bool f, State<T> state)[] states) =>
            Choice(states.Where(s => s.f).Select(s => s.state).ToArray());

        static public State<T> Choice<T>(int index, params State<T>[] states) => states.ElementAt(index);

        static public State<T> Choice<T>(params State<T>[] states) => (env, cpu) =>
        {
            var log = String.Empty;

            foreach (var state in states)
            {
                var f = default(Boolean);
                var value = default(Func<T>);

                (f, value, cpu, log) = state(env, cpu);

                if (f)
                {
                    return (true, value, cpu, log);
                }
            }
            return (false, () => default(T), cpu, log);
        };

        static public State<IEnumerable<T>> Many0<T>(this State<T> next) => (env, cpu) =>
        {
            var ret = new List<T>();
            var log_ = String.Empty;
            while (true)
            {
                var log = String.Empty;
                var t = default(Func<T>);
                var f = default(Boolean);
                (f, t, cpu, log) = next(env, cpu);

                log_ = log_ + log;

                if (!f)
                    return (true, () => (IEnumerable<T>)ret, cpu, log_);

                ret.Add(t());
            }
        };

        static public UInt32 ToUint32(this IEnumerable<byte> data) =>
            data.Take(4).Reverse().Aggregate(((UInt32)0), (i, d) => d + 0x100 * i);

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

        static public State<UInt32> GetMemoryDataIp32 = (env, cpu) =>
        {
            var data = env.GetMemoryData32(cpu.cs, cpu.ip);
            ++cpu.ip;
            ++cpu.ip;
            ++cpu.ip;
            ++cpu.ip;
            return (true, () => data, cpu, String.Empty);
        };

        static public State<UInt16> GetMemoryDataIp16 = (env, cpu) =>
        {
            var data = env.GetMemoryData16(cpu.cs, cpu.ip);
            ++cpu.ip;
            ++cpu.ip;
            return (true, () => data, cpu, String.Empty);
        };

        static public State<byte> GetMemoryDataIp8 = (env, cpu) =>
        {
            var data = env.GetMemoryData8(cpu.cs, cpu.ip);
            ++cpu.ip;
            return (true, () => data, cpu, String.Empty);
        };

        static public State<IEnumerable<byte>> GetMemoryDataIp(int length) => (env, cpu) =>
        {
            var data = env.GetMemoryDatas(cpu.cs, cpu.ip, length);
            cpu.ip += (ushort)data.Count();
            return (true, () => data, cpu, String.Empty);
        };

        static public State<(int type, byte db, ushort dw, uint dd)> GetMemoryDataIp_(int type) =>
            from data in Choice(
                type,
                GetMemoryDataIp8.Select(db => (db: db, dw: default(ushort), dd: default(uint))),
                GetMemoryDataIp16.Select(dw => (db: default(byte), dw: dw, dd: default(uint))),
                GetMemoryDataIp32.Select(dd => (db: default(byte), dw: default(ushort), dd: dd))
            )
            select (type, data.db, data.dw, data.dd);

        static public State<Unit> SetCrReg(int reg, uint data) =>
            from _ in SetCpu(
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
            )
            select Unit.unit;

        static public State<uint> GetCrReg(int reg) => (env, cpu) =>
        (
            true,
            () =>
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
        ,
            cpu,
            String.Empty
        );

        static public State<CPU> GetCpu = (env, cpu) => (true, () => cpu, cpu, String.Empty);
        static public State<Unit> SetCpu(CPU new_cpu) => (env, cpu) => (true, () => Unit.unit, new_cpu, String.Empty);
        static public State<Unit> SetCpu(Func<CPU, CPU> func) => (env, cpu) => (true, () => Unit.unit, func(cpu), String.Empty);
        static public State<Unit> SetResult(Boolean f) => (env, cpu) => (f, () => Unit.unit, cpu, String.Empty);
        static public State<Unit> SetLog(String log) => (env, cpu) => (true, () => Unit.unit, cpu, log);

        static public State<(int mod, int reg, int rm)> ModRegRm =
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

        static public State<byte> GetMemoryData8() => (env, cpu) =>
        {
            var data = env.GetMemoryData8(cpu.cs, cpu.ip);
            ++cpu.ip;
            return (true, () => data, cpu, String.Empty);
        };

        static public State<(bool isMem, uint addr)> GetMemoryAddr
            (
                ushort segment,
                ushort offset
            ) => (env, cpu) =>
            (true, () => (true, ((uint)segment) * 0x10 + offset), cpu, String.Empty);

        static public State<(bool isMem, uint addr)> GetMemOrRegAddr
            (
                int mod,
                int rm,
                (Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size) prefixes
            ) => (env, cpu) =>
            {
                (var isMem, var addr, var inc) = env.GetMemoryAddr(cpu, mod, rm, cpu.cs, env.GetMemoryDatas(cpu.cs, cpu.ip), prefixes.address_size);
                cpu.ip += (ushort)inc;
                return (true, () => (isMem, addr), cpu, String.Empty);
            };

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

        static public State<byte> GetMemoryData8((bool isMem, uint addr) t) => (env, cpu) => (true, () => env.GetMemOrRegData8_(t.isMem, t.addr, cpu), cpu, String.Empty);
        static public State<ushort> GetMemoryData16((bool isMem, uint addr) t) => (env, cpu) => (true, () => env.GetMemOrRegData16_(t.isMem, t.addr, cpu), cpu, String.Empty);
        static public State<uint> GetMemoryData32((bool isMem, uint addr) t) => (env, cpu) => (true, () => env.GetMemOrRegData32_(t.isMem, t.addr, cpu), cpu, String.Empty);

        static public State<(int type, byte db, ushort dw, uint dd)> GetMemOrRegData(
            (bool isMem, uint addr) t,
            (Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size) prefixes,
            Boolean w) =>
            from _ in SetLog("GetMemOrRegData")
            let type = w ? (prefixes.operand_size ? 2 : 1) : 0
            from data in Choice(
                type,
                GetMemoryData8(t).Select(db => (type, db, default(ushort), default(uint))),
                GetMemoryData16(t).Select(dw => (type, default(byte), dw, default(uint))),
                GetMemoryData32(t).Select(dd => (type, default(byte), default(ushort), dd))
            )
            select data;


        static public State<Unit> SetMemOrRegData8((bool isMem, uint addr) t, byte db) => (env, cpu) => (true, () => Unit.unit, env.SetMemOrRegData8_(t.isMem, t.addr, cpu, db), String.Empty);
        static public State<Unit> SetMemOrRegData16((bool isMem, uint addr) t, ushort dw) => (env, cpu) => (true, () => Unit.unit, env.SetMemOrRegData16_(t.isMem, t.addr, cpu, dw), String.Empty);
        static public State<Unit> SetMemOrRegData32((bool isMem, uint addr) t, uint dd) => (env, cpu) => (true, () => Unit.unit, env.SetMemOrRegData32_(t.isMem, t.addr, cpu, dd), String.Empty);

        static public State<Unit> SetMemOrRegData(
            (bool isMem, uint addr) t,
            (int type, byte db, ushort dw, uint dd) data) =>
            from _ in Choice
            (
                data.type,
                SetMemOrRegData8(t, data.db),
                SetMemOrRegData16(t, data.dw),
                SetMemOrRegData32(t, data.dd)
            )
            select Unit.unit;

        static public State<byte> GetRegData8(int reg) => (env, cpu) => (true, () => EmuEnvironment.GetRegData8(cpu, reg), cpu, "GetRegData8");
        static public State<ushort> GetRegData16(int reg) => (env, cpu) => (true, () => EmuEnvironment.GetRegData16(cpu, reg), cpu, "GetRegData16");
        static public State<uint> GetRegData32(int reg) => (env, cpu) => (true, () => EmuEnvironment.GetRegData32(cpu, reg), cpu, "GetRegData32");

        static public State<Unit> SetRegData8(int reg, byte db) => (env, cpu) => (true, () => Unit.unit, EmuEnvironment.SetRegData8(cpu, reg, db), "SetRegData8");
        static public State<Unit> SetRegData16(int reg, ushort dw) => (env, cpu) => (true, () => Unit.unit, EmuEnvironment.SetRegData16(cpu, reg, dw), "SetRegData16");
        static public State<Unit> SetRegData32(int reg, uint dd) => (env, cpu) => (true, () => Unit.unit, EmuEnvironment.SetRegData32(cpu, reg, dd), "SetRegData32");

        static public State<Unit> SetRegData(int reg, (int type, byte db, ushort dw, uint dd) data) =>
            from _ in Choice
            (
                data.type,
                SetRegData8(reg, data.db),
                SetRegData16(reg, data.dw),
                SetRegData32(reg, data.dd)
            )
            select Unit.unit;

        static public State<Unit> SetSReg3(int reg, (int type, byte db, ushort dw, uint dd) data) => (env, cpu) =>
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
            return (true, () => Unit.unit, cpu, "SetRegData");
        };

        static public State<(int type, byte db, ushort dw, uint dd)> GetRegData(int reg, int type) =>
            from data in Choice(
                type,
                GetRegData8(reg).Select(db => (db: db, dw: default(ushort), dd: default(uint))),
                GetRegData16(reg).Select(dw => (db: default(byte), dw: dw, dd: default(uint))),
                GetRegData32(reg).Select(dd => (db: default(byte), dw: default(ushort), dd: dd))
            )
            select (type, data.db, data.dw, data.dd);

        static public State<IEnumerable<byte>> Opecode(params byte[] bs) =>
                        from datas in GetMemoryDataIp(bs.Length)
                        from _ in SetResult(bs.SequenceEqual(datas))
                        select datas;

        static public State<(Boolean cs, Boolean es, Boolean fs, Boolean gs, Boolean operand_size, Boolean address_size)> Prefixes =
            from data in Contains(0x2e, 0x26, 0x64, 0x64, 0x66, 0x67).Many0()
            select (
                data.Contains((byte)0x2e),
                data.Contains((byte)0x26),
                data.Contains((byte)0x64),
                data.Contains((byte)0x65),
                data.Contains((byte)0x66),
                data.Contains((byte)0x67)
            );

        static public
           State<(int type, byte db, ushort dw, uint dd)> Calc(
           (int type, byte db, ushort dw, uint dd) data1,
           (int type, byte db, ushort dw, uint dd) data2,
           int kind)
        {
            (var type2, var db2, var dw2, var dd2) = data2;
            return Calc(data1, db2, dw2, dd2, kind);
        }


        static public State<Unit> update_eflags8(byte v) => (env, cpu) =>
        {
            cpu = cpu.update_eflags8(v);
            return (true, () => Unit.unit, cpu, "update_eflags8");
        };
        static public State<Unit> update_eflags16(ushort v) => (env, cpu) =>
        {
            cpu = cpu.update_eflags16(v);
            return (true, () => Unit.unit, cpu, "update_eflags8");
        };
        static public State<Unit> update_eflags32(uint v) => (env, cpu) =>
        {
            cpu = cpu.update_eflags32(v);
            return (true, () => Unit.unit, cpu, "update_eflags8");
        };
        static public State<Unit> update_eflags_sub32(UInt32 v1, UInt32 v2) => (env, cpu) =>
        {
            cpu = cpu.update_eflags_sub32(v1, v2);
            return (true, () => Unit.unit, cpu, "update_eflags_sub32");
        };
        static public State<Unit> update_eflags_add32(UInt32 v1, UInt32 v2) => (env, cpu) =>
        {
            cpu = cpu.update_eflags_add32(v1, v2);
            return (true, () => Unit.unit, cpu, "update_eflags_add32");
        };

        static public State<(int type, byte db, ushort dw, uint dd)> Calc((int type, byte db, ushort dw, uint dd) data1, byte db2, ushort dw2, uint dd2, int kind) =>
            from ret1 in Choice(
                kind,
                Choice( // ADD
                    data1.type,
                    from _ in update_eflags_add32((UInt32)data1.db, (UInt32)db2)
                    select (db: (byte)((UInt32)data1.db + (UInt32)db2), dw: default(ushort), dd: default(uint)),
                    from _ in update_eflags_add32((UInt32)data1.dw, (UInt32)dw2)
                    select (db: default(byte), dw: (ushort)((UInt32)data1.dw + (UInt32)dw2), dd: default(uint)),
                    from _ in update_eflags_add32(data1.dd, dd2)
                    select (db: default(byte), dw: default(ushort), dd: data1.dd + dd2)
                ),
                Choice( // OR
                    data1.type,
                    (db: (byte)(data1.db | db2), dw: default(ushort), dd: default(uint)).ToState(),
                    (db: default(byte), dw: (ushort)(data1.dw | dw2), dd: default(uint)).ToState(),
                    (db: default(byte), dw: default(ushort), dd: (data1.dd | dd2)).ToState()
                ),
                Choice( // ADC
                    data1.type,
                    from cpu in GetCpu
                    let _data2 = (db2 + (UInt32)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_add32((UInt32)data1.db, _data2)
                    select (db: (byte)((UInt32)data1.db + _data2), dw: default(ushort), dd: default(uint)),
                    from cpu in GetCpu
                    let _data2 = (dw2 + (UInt32)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_add32((UInt32)data1.dw, _data2)
                    select (db: default(byte), dw: (ushort)((UInt32)data1.dw + _data2), dd: default(uint)),
                    from cpu in GetCpu
                    let _data2 = (dd2 + (UInt32)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_add32((UInt32)data1.dd, _data2)
                    select (db: (byte)((UInt32)data1.db + _data2), dw: default(ushort), dd: default(uint))
                ),
                Choice( // SBB
                    data1.type,
                    from cpu in GetCpu
                    let _data2 = (db2 + (UInt32)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_sub32((UInt32)data1.db, _data2)
                    select (db: (byte)((UInt32)data1.db - _data2), dw: default(ushort), dd: default(uint)),
                    from cpu in GetCpu
                    let _data2 = (dw2 + (UInt32)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_sub32((UInt32)data1.dw, _data2)
                    select (db: default(byte), dw: (ushort)((UInt32)data1.dw - _data2), dd: default(uint)),
                    from cpu in GetCpu
                    let _data2 = (dd2 + (UInt32)(cpu.cf ? 1 : 0))
                    from _ in update_eflags_sub32((UInt32)data1.dd, _data2)
                    select (db: default(byte), dw: default(ushort), dd: (data1.db - _data2))
                ),
                Choice( // AND
                    data1.type,
                    from _db in ((byte)(data1.db & db2)).ToState()
                    from _ in update_eflags8(_db)
                    select (db: _db, dw: default(ushort), dd: default(uint)),
                    from _dw in ((ushort)(data1.dw & dw2)).ToState()
                    from _ in update_eflags16(_dw)
                    select (db: default(byte), dw: _dw, dd: default(uint)),
                    from _dd in (data1.dd & dd2).ToState()
                    from _ in update_eflags32(_dd)
                    select (db: default(byte), dw: default(ushort), dd: _dd)
                ),
                Choice( // SUB
                    data1.type,
                    from _ in update_eflags_sub32((UInt32)data1.db, db2)
                    select (db: (byte)((UInt32)data1.db - (UInt32)db2), dw: default(ushort), dd: default(uint)),
                    from _ in update_eflags_sub32((UInt32)data1.dw, dw2)
                    select (db: default(byte), dw: (ushort)((UInt32)data1.dw - (UInt32)dw2), dd: default(uint)),
                    from _ in update_eflags_sub32((UInt32)data1.dd, dd2)
                    select (db: default(byte), dw: default(ushort), dd: (data1.dd - dd2))
                ),
                Choice( // XOR
                    data1.type,
                    (db: (byte)(data1.db ^ db2), dw: default(ushort), dd: default(uint)).ToState(),
                    (db: default(byte), dw: (ushort)(data1.dw ^ dw2), dd: default(uint)).ToState(),
                    (db: default(byte), dw: default(ushort), dd: (data1.dd ^ dd2)).ToState()
                ),
                Choice( // CMP
                    data1.type,
                    from _ in update_eflags_sub32((UInt32)data1.db, db2)
                    select (db: data1.db , dw: default(ushort), dd: default(uint)),
                    from _ in update_eflags_sub32((UInt32)data1.dw, dw2)
                    select (db: default(byte), dw: data1.dw , dd: default(uint)),
                    from _ in update_eflags_sub32((UInt32)data1.dd, dd2)
                    select (db: default(byte), dw: default(ushort), dd: data1.dd )
                )
            )
            select (data1.type, ret1.db, ret1.dw, ret1.dd);

        static public State<bool> Jcc(int type) => (env, cpu) =>
        {
            var array = new Func<bool>[]
            {
                () => cpu.of,                           // JO
                () => !cpu.of,                          // JNO
                () => cpu.cf,                           // JB
                () => !cpu.cf,                          // JNB
                () => cpu.zf,                           // JE
                () => !cpu.zf,                          // JNE
                () => cpu.cf || cpu.zf,                 // JBE
                () => (!cpu.cf) && (!cpu.zf),           // JNBE
                () => cpu.sf,                           // JS
                () => !cpu.sf,                          // JNS
                () => cpu.pf,                           // JP
                () => !cpu.pf,                          // JNP
                () => (cpu.sf != cpu.of) && (!cpu.zf),  // JL
                () => cpu.sf == cpu.of,                 // JNL
                () => cpu.sf != cpu.of,                 // JLE
                () => (cpu.sf == cpu.of) && (!cpu.zf),  // JNLE
            };
            return (true, array.ElementAt(type), cpu, "Jcc");
        };

        static public byte[] ToByteArray(this ushort data) =>
            new[] {
                (byte)(data & 0xFF),
                (byte) ((data >> 8) & 0xFF)
            };
        static public byte[] ToByteArray(this uint data) =>
            new[] {
                (byte)(data & 0xFF),
                (byte)((data >> 8) & 0xFF),
                (byte)((data >> 16) & 0xFF),
                (byte)((data >> 24) & 0xFF)
            };
    }
}