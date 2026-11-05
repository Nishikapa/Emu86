namespace Emu86;

public delegate (bool IsSuccess, V value, CPU cpu, string log) State<V>(EmuEnvironment env, CPU param, byte[] opecodes);

static public class States
{
    static public State<V> ToState<V>(this V value) => (env, cpu, ope) => (true, value, cpu, string.Empty);

    // 何もせず成功する State(When/Unless の「実行しない側」)。
    static public readonly State<Unit> Pass = Unit.unit.ToState();

    // 失敗する State(未定義・未実装の命令)。CPU は変えない。実行器が #UD として扱う。
    static public readonly State<Unit> Undefined = (env, cpu, ope) => (false, default, cpu, string.Empty);

    // グループ命令(ModRM の reg フィールドで選ぶ)の表。表に無い値は Undefined。
    // 辞書を作らず順に比べる(8 項以下)。params ReadOnlySpan なので呼び出しごとの配列割り当てもない。
    static public State<Unit> Group(int key, params ReadOnlySpan<(int key, State<Unit> state)> cases)
    {
        foreach (var (candidate, state) in cases)
            if (candidate == key) return state;
        return Undefined;
    }

    // クエリ構文の where 句。述語が偽なら失敗にする(CPU は変更しない)。
    // 失敗を返す State は CPU を変更する前に置くこと(失敗しても、それまでの句が加えた CPU の変更は巻き戻されない)。
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

    // 2 つの State を順に実行する(値は捨てる)。クエリ構文の複数 from と違い、評価のたびに中間オブジェクトを作らない。
    static public State<Unit> Then(this State<Unit> first, State<Unit> second) =>
        first.SelectMany(_ => second, (_, _) => Unit.unit);

    // 条件が成り立つ間 body を繰り返す(0 回以上)。body が失敗したらその時点の CPU で失敗を返す(ロールバックはしない)。
    static public State<Unit> While(Func<CPU, bool> condition, State<Unit> body) => (env, cpu, ope) =>
    {
        var log = string.Empty;
        while (condition(cpu))
        {
            var (ok, _, next, bodyLog) = body(env, cpu, ope);
            if (!ok) return (false, default, next, bodyLog);
            cpu = next;
            log = bodyLog;
        }
        return (true, Unit.unit, cpu, log);
    };

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

    // 複数の State を順に実行して値を集める(PUSHA/POPA、ENTER のネスト、IRET のセレクタのクリア)。
    // 失敗はロールバックできないので、すべて成功しなければならない。
    static public State<IEnumerable<T>> Sequence<T>(this IEnumerable<State<T>> states) => (env, cpu, ope) =>
    {
        var values = new List<T>();
        var log = string.Empty;
        foreach (var state in states)
        {
            var (ok, value, next, stateLog) = state(env, cpu, ope);
            if (!ok) throw new Exception();
            cpu = next;
            log += stateLog;
            values.Add(value);
        }
        return (true, values, cpu, log);
    };

    static public State<Unit> Ignore<T>(this State<T> s) =>
        from data in s
        select Unit.unit;
}
