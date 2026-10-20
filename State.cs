using static Emu86.Ext;

namespace Emu86;

public delegate (bool IsSuccess, V value, CPU cpu, string log) State<V>(EmuEnvironment env, CPU param, byte[] opecodes);

static public partial class Ext
{
    static public State<V> ToState<V>(this V value) => (env, cpu, ope) => (true, value, cpu, string.Empty);

    // 何もせず成功する State(When/Unless の「実行しない側」)。
    static public readonly State<Unit> Pass = Unit.unit.ToState();

    // クエリ構文の where 句。述語が偽なら失敗にする(CPU は変更しない)。
    // 失敗を返す State は CPU を変更する前に置くこと(Choice/Many0 は失敗した枝の変更を巻き戻せない)。
    static public State<T> Where<T>(this State<T> state, Func<T, bool> predicate)
    {
        if (default == state)
        {
            throw new Exception();
        }
        return (env, cpu, ope) =>
        {
            var (f, value, cpu2, log) = state(env, cpu, ope);
            var ok = f && predicate(value);
            return (ok, ok ? value : default, cpu2, log);
        };
    }

    // 条件が真のときだけ state を実行する(偽なら何もせず成功)。
    static public State<Unit> When(bool condition, State<Unit> state) => condition ? state : Pass;

    static public State<Unit> Unless(bool condition, State<Unit> state) => When(!condition, state);

    static public State<B> Select<A, B>(this State<A> stateA, Func<A, B> selector)
    {
        if (default == stateA)
        {
            throw new Exception();
        }
        return (env, cpu1, ope) =>
        {
            var (f, value, cpu2, log) = stateA(env, cpu1, ope);
            return (f, f ? selector(value) : default, cpu2, log);
        };
    }

    static public State<C> SelectMany<A, B, C>(this State<A> stateA, Func<A, State<B>> selector, Func<A, B, C> projector)
    {
        if (default == stateA)
        {
            throw new Exception();
        }
        return (env, cpu1, ope) =>
        {
            var (isSuccess1, valueA, cpu2, log1) = stateA(env, cpu1, ope);
            if (isSuccess1)
            {
                var (isSuccess2, valueB, cpu3, log2) = selector(valueA)(env, cpu2, ope);
                // ログは捨てられることが多いので、片方が空なら連結を省いてアロケーションを避ける。
                var log = log1.Length == 0 ? log2 : log2.Length == 0 ? log1 : log1 + "\r\n" + log2;
                return (isSuccess2, isSuccess2 ? projector(valueA, valueB) : default, cpu3, log);
            }
            else
            {
                return (false, default, cpu2, log1);
            }
        };
    }

    static public State<T> Choice<T>(params (bool f, State<T> state)[] states) =>
        Choice(states.Where(s => s.f).Select(s => s.state).ToArray());

    static public State<T> Choice<T>(params State<T>[] states) => (env, cpu, ope) =>
    {
        var log = string.Empty;

        foreach (var state in states)
        {
            var f = default(bool);
            var value = default(T);

            (f, value, cpu, log) = state(env, cpu, ope);

            if (f)
            {
                return (true, value, cpu, log);
            }
        }
        return (false, default, cpu, log);
    };

    static public State<IEnumerable<T>> Sequence<T>(params State<T>[] states) =>
        Sequence((IEnumerable<State<T>>)states);

    static public State<IEnumerable<T>> Sequence<T>(this IEnumerable<State<T>> states) => (env, cpu, ope) =>
    {
        var log_ = string.Empty;
        var ret = new List<T>();

        foreach (var state in states)
        {
            var f = default(bool);
            var value = default(T);
            var log = string.Empty;

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

        while (true)
        {
            var t = default(T);
            var f = default(bool);
            (f, t, cpu, _) = next(env, cpu, ope);

            if (!f)
                return (true, ret.AsEnumerable(), cpu, "Many0");

            ret.Add(t);
        }
    };
}
