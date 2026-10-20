using System.Globalization;

namespace Emu86;

// コマンドライン引数のパーサコンビネータ。args の pos から読み、成功なら値と次の位置を返す。
// ok=false は「この位置の引数には当てはまらない」(別の選択肢を試す)。当てはまったうえで値が不正なら
// ArgumentException を投げる(オプション名は CommandOption が前置する)。
internal delegate (bool ok, T value, int next) ArgParser<T>(string[] args, int pos);

internal static class ArgParsers
{
    public static ArgParser<T> Return<T>(T value) => (args, pos) => (true, value, pos);

    public static ArgParser<B> Select<A, B>(this ArgParser<A> parser, Func<A, B> selector) => (args, pos) =>
    {
        var (ok, value, next) = parser(args, pos);
        return (ok, ok ? selector(value) : default, next);
    };

    public static ArgParser<C> SelectMany<A, B, C>(this ArgParser<A> parser, Func<A, ArgParser<B>> selector, Func<A, B, C> projector) => (args, pos) =>
    {
        var (ok, a, next) = parser(args, pos);
        if (!ok) return (false, default, pos);
        var (ok2, b, next2) = selector(a)(args, next);
        return ok2 ? (true, projector(a, b), next2) : (false, default, pos);
    };

    // 最初に当てはまった選択肢。どれも当てはまらなければ位置を進めずに失敗。
    public static ArgParser<T> Choice<T>(params ArgParser<T>[] alternatives) => (args, pos) =>
    {
        foreach (var alternative in alternatives)
        {
            var result = alternative(args, pos);
            if (result.ok) return result;
        }
        return (false, default, pos);
    };

    public static ArgParser<T> Or<T>(this ArgParser<T> first, ArgParser<T> second) => Choice(first, second);

    // 当てはまる限り繰り返す(0 回以上)。
    public static ArgParser<IReadOnlyList<T>> Many<T>(this ArgParser<T> parser) => (args, pos) =>
    {
        var values = new List<T>();
        while (true)
        {
            var (ok, value, next) = parser(args, pos);
            if (!ok) return (true, values, pos);
            values.Add(value);
            pos = next;
        }
    };

    // 位置の引数が word に一致すれば消費する。
    public static ArgParser<Unit> Word(string word) => (args, pos) =>
        pos < args.Length && args[pos] == word ? (true, Unit.unit, pos + 1) : (false, default, pos);

    // 省略可能な値: 次の引数が無いか別のオプション("--" 始まり)なら読まずに fallback。
    public static ArgParser<T> Optional<T>(this ArgParser<T> parser, T fallback) => (args, pos) =>
        pos >= args.Length || args[pos].StartsWith("--") ? (true, fallback, pos) : parser(args, pos);

    // 引数を消費せずに条件を検査する(不成立は値の不正として報告)。
    public static ArgParser<Unit> Require(bool condition, string message) => (args, pos) =>
        condition ? (true, Unit.unit, pos) : throw new ArgumentException(message);

    // 値を 1 つ取る。無い・空白・"--" 始まりなら欠落。
    public static readonly ArgParser<string> Value = (args, pos) =>
        pos >= args.Length || string.IsNullOrWhiteSpace(args[pos]) || args[pos].StartsWith("--")
            ? throw new ArgumentException("missing value")
            : (true, args[pos], pos + 1);

    // 符号なし十進整数。
    public static readonly ArgParser<long> Integer = Value.Select(text =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException("expected a non-negative decimal integer"));

    // 32 ビット十六進アドレス("0x" は省略可)。
    public static readonly ArgParser<uint> Hex = Value.Select(text =>
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address)
            ? address
            : throw new ArgumentException("expected a 32-bit hexadecimal address");
    });
}

// 1 つのコマンドラインオプション。Parser はオプション名に当てはまったときだけ値を読み、設定への適用を返す。
// 値の不正(ArgumentException)にはオプション名を前置する。Usage は同じ表から生成する。
internal sealed record CommandOption(string Group, string Name, string Arguments, string Description, ArgParser<Action<RunOptions>> Parser)
{
    public static CommandOption Flag(string group, string name, string description, Action<RunOptions> apply) =>
        Of(group, name, "", description, ArgParsers.Return(Unit.unit), (options, _) => apply(options));

    public static CommandOption Of<T>(string group, string name, string arguments, string description, ArgParser<T> value, Action<RunOptions, T> apply) =>
        new(group, name, arguments, description, (args, pos) =>
        {
            var (matched, _, next) = ArgParsers.Word(name)(args, pos);
            if (!matched) return (false, default, pos);
            try
            {
                var (ok, parsed, end) = value(args, next);
                if (!ok) throw new ArgumentException("invalid value");
                return (true, options => apply(options, parsed), end);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"{name}: {exception.Message}");
            }
        });

    public string Syntax => Arguments.Length == 0 ? Name : $"{Name} {Arguments}";
}
