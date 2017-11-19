using System;
using System.Collections.Generic;
using System.Linq;
using static Emu86.Ext;

namespace Emu86
{
    public delegate (Boolean IsSuccess, V value, CPU cpu, String log) State<V>(EmuEnvironment env, CPU param, byte[] opecodes);

    static public partial class Ext
    {
        static public State<V> ToState<V>(this V value) => (env, cpu, ope) => (true, value, cpu, String.Empty);

        static public State<B> Select<A, B>(this State<A> stateA, Func<A, B> selector)
        {
            if (default(State<A>) == stateA)
            {
                throw new Exception();
            }
            return (env, cpu1, ope) =>
            {
                var (f, value, cpu2, log) = stateA(env, cpu1, ope);
                return (f, f ? selector(value) : default(B), f ? cpu2 : cpu1, log);
            };
        }

        static public State<C> SelectMany<A, B, C>(this State<A> stateA, Func<A, State<B>> selector, Func<A, B, C> projector)
        {
            if (default(State<A>) == stateA)
            {
                throw new Exception();
            }
            return (env, cpu1, ope) =>
            {
                var (isSuccess1, valueA, cpu2, log1) = stateA(env, cpu1, ope);
                if (isSuccess1)
                {
                    var (isSuccess2, valueB, cpu3, log2) = selector(valueA)(env, cpu2, ope);
                    return (isSuccess2, isSuccess2 ? projector(valueA, valueB) : default(C), isSuccess2 ? cpu3 : cpu1, log1 + "\r\n" + log2);
                }
                else
                {
                    return (false, default(C), cpu1, log1);
                }
            };
        }

        static public State<T> Choice<T>(params (bool f, State<T> state)[] states) =>
            Choice(states.Where(s => s.f).Select(s => s.state).ToArray());

        static public State<T> Choice<T>(params State<T>[] states) => (env, cpu, ope) =>
        {
            var log = String.Empty;

            foreach (var state in states)
            {
                var f = default(Boolean);
                var value = default(T);

                (f, value, cpu, log) = state(env, cpu, ope);

                if (f)
                {
                    return (true, value, cpu, log);
                }
            }
            return (false, default(T), cpu, log);
        };

        static public State<IEnumerable<T>> Sequence<T>(params State<T>[] states) =>
            Sequence((IEnumerable<State<T>>)states);

        static public State<IEnumerable<T>> Sequence<T>(this IEnumerable<State<T>> states) => (env, cpu, ope) =>
        {
            var log_ = String.Empty;
            var ret = new List<T>();

            foreach (var state in states)
            {
                var f = default(Boolean);
                var value = default(T);
                var log = String.Empty;

                (f, value, cpu, log) = state(env, cpu, ope);

                log_ = log_ + log;

                // envのロールバックはさぽーとされていないため、
                // すべて成功しなければならない。
                if (!f)
                    throw new Exception();

                ret.Add(value);
            }
            return (true, ret, cpu, log_);
        };

        static public State<Unit> Ignore<T>(this State<T> s) =>
            from data in s
            select Unit.unit;

        static public State<IEnumerable<T>> Many0<T>(this State<T> next) => (env, cpu, ope) =>
        {
            var ret = new List<T>();
            var log_ = String.Empty;

            while (true)
            {
                var log = String.Empty;
                var t = default(T);
                var f = default(Boolean);
                (f, t, cpu, log) = next(env, cpu, ope);

                log_ = log_ + log;

                if (!f)
                    return (true, ret.AsEnumerable(), cpu, "Many0");

                ret.Add(t);
            }
        };
    }
}
