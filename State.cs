using System;
using System.Collections.Generic;
using System.Linq;
using static Emu86.Ext;

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

        static public State<B> Select<A, B>(this State<A> param, Func<A, B> selector)
        {
            if (default(State<A>) == param)
            {
                throw new Exception();
            }
            return (env, cpu1) =>
            {
                (var f, var value, var cpu2, var log) = param(env, cpu1);
                return (f, f ? () => selector(value()) : default(Func<B>), f ? cpu2 : cpu1, log);
            };
        }

        static public State<C> SelectMany<A, B, C>(this State<A> param, Func<A, State<B>> selector, Func<A, B, C> projector)
        {
            if (default(State<A>) == param)
            {
                throw new Exception();
            }
            return (env, cpu1) =>
            {
                (var f1, var reta, var cpu2, var log1) = param(env, cpu1);
                if (f1)
                {
                    (var f2, var retb, var cpu3, var log2) = selector(reta())(env, cpu2);
                    return (f2, f2 ? () => projector(reta(), retb()) : default(Func<C>), f2 ? cpu3 : cpu1, log1 + "\r\n" + log2);
                }
                else
                {
                    return (false, default(Func<C>), cpu1, log1);
                }
            };
        }

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

        static public State<IEnumerable<T>> Sequence<T>(params State<T>[] states) => Sequence((IEnumerable<State<T>>)states);
        static public State<IEnumerable<T>> Sequence<T>(this IEnumerable<State<T>> states) => (env, cpu) =>
        {
            var log_ = String.Empty;
            var ret = new List<T>();

            foreach (var state in states)
            {
                var f = default(Boolean);
                var value = default(Func<T>);
                var log = String.Empty;

                (f, value, cpu, log) = state(env, cpu);

                log_ = log_ + log;

                // envのロールバックはさぽーとされていないため、
                // すべて成功しなければならない。
                if (!f)
                    throw new Exception();

                ret.Add(value());
            }
            return (true, () => ret, cpu, log_);
        };

        static public State<Unit> Ignore<T>(this State<T> s) =>
            from data in s
            select Unit.unit;

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

        static public uint ToUint32(this IEnumerable<byte> data) =>
            data.Take(4).Reverse().Aggregate(((uint)0), (i, d) => d + 0x100 * i);

        static public byte[] ToByteArray(this byte db) =>
            new[] {
                db
            };
        static public byte[] ToByteArray(this ushort dw) =>
            new[] {
                (byte)(dw & 0xFF),
                (byte) ((dw >> 8) & 0xFF)
            };
        static public byte[] ToByteArray(this uint dd) =>
            new[] {
                (byte)(dd & 0xFF),
                (byte)((dd >> 8) & 0xFF),
                (byte)((dd >> 16) & 0xFF),
                (byte)((dd >> 24) & 0xFF)
            };
    }
}