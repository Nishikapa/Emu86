using System.Buffers.Binary;
using static Emu86.Scripts;

namespace Emu86;

// 手順書モナド(コルーチン): 実行すると、値を返して終わるか、待ち条件と「その後の続き」を返して中断する。
// クエリ構文の from が手順を順につなぐので、待ちを含む部分手順(キーを打って間を置く、プロンプトを待つ)を、値を返す関数と
// 同じように合成できる。手順は実行されるまで何もしない(Delay の起点などは、その手順に来たときに決まる)。
internal delegate Progress<T> Script<T>();

// Ready が null なら完了(Value が結果)。そうでなければ、Ready が真になるのを待ってから Rest を実行する。
internal readonly record struct Progress<T>(T Value, Func<bool> Ready, Script<T> Rest)
{
    public bool Completed => Ready == null;
}

internal static class Scripts
{
    public static Script<T> Return<T>(T value) => () => new(value, null, null);

    // 何もしない手順。
    public static readonly Script<Unit> Nothing = Return(Unit.unit);

    public static Script<B> Select<A, B>(this Script<A> script, Func<A, B> selector) => () =>
    {
        var progress = script();
        return progress.Completed
            ? new(selector(progress.Value), null, null)
            : new(default, progress.Ready, progress.Rest.Select(selector));
    };

    // script が中断したら、その続きの後ろに next をつないだものを新しい続きにする。完了したら、その値で next を実行する。
    public static Script<C> SelectMany<A, B, C>(this Script<A> script, Func<A, Script<B>> next, Func<A, B, C> projector) => () =>
    {
        var progress = script();
        if (!progress.Completed) return new(default, progress.Ready, progress.Rest.SelectMany(next, projector));
        var value = progress.Value;
        return next(value).Select(result => projector(value, result))();
    };

    // first の後に second。first が完了したら second をそのまま実行するので、繰り返し(While/ForEach)が続きを積み重ねない。
    public static Script<T> Then<T>(this Script<Unit> first, Script<T> second) => () =>
    {
        var progress = first();
        return progress.Completed ? second() : new(default, progress.Ready, progress.Rest.Then(second));
    };

    // 待たずに動作だけ行う。
    public static Script<Unit> Do(Action action) => () =>
    {
        action();
        return new(Unit.unit, null, null);
    };

    // 待たずに値を得る(その手順に来た時点の値)。
    public static Script<T> Get<T>(Func<T> read) => () => new(read(), null, null);

    // 条件が真になるまで待つ(Step のたびに判定する)。
    public static Script<Unit> WaitFor(Func<bool> ready) => () => new(Unit.unit, ready, Nothing);

    // 条件が成り立つ間 body を繰り返す。
    public static Script<Unit> While(Func<bool> condition, Script<Unit> body) => () =>
        condition() ? body.Then(While(condition, body))() : Nothing();

    // 各要素について順に body を行う。
    public static Script<Unit> ForEach<T>(IEnumerable<T> items, Func<T, Script<Unit>> body) => () =>
    {
        var remaining = items.GetEnumerator();
        Script<Unit> next = null;
        next = () =>
        {
            if (remaining.MoveNext()) return body(remaining.Current).Then(next)();
            remaining.Dispose();
            return Nothing();
        };
        return next();
    };
}

// 画面(テキスト VRAM)を見ながらキーを注入する自動操作。手順書は Script モナドのクエリで書き、待ち条件
// (画面に文字列が出る、一定命令数が経つ)が満たされるまで Step は先へ進まない。判定は VRAM のプレーン 0(文字)だけを見る。
// 実行スレッドのタイマ周期で Step を呼ぶ。手順書は 2 種類:
//   NtldrDebug (--boot-debug): NTLDR の起動メニューを操作し、boot.ini を変えずに「デバッグ モード」(/debug、XP の既定は COM2 19200bps)で起動する。
//     1. ブートセクタへ制御が移ったら(VGA BIOS スタブの導入 = INT 19h 以降)、F8 を繰り返し押す。
//        NTLDR は起動直後の短い時間だけ F8 を見るが、BIOS のキーバッファに先に積んでおけば確実に拾われる。
//     2. テキスト VRAM に「Windows Advanced Options Menu」が出たら F8 をやめ、残っているキーを BDA のバッファから捨て、
//        ハイライト行を見ながら ↓ を押して「Debugging Mode」を選び Enter。
//     3. OS 選択メニュー(「Please select the operating system」)が出たら Enter。
//   Typing (--type): 「プロンプト=>キー列」の手順を順に。画面の最後の行がプロンプトで終わるのを待ってキー列を打ち(\n は Enter、
//     <down> <end> <c-x>(Ctrl+x)<a-f2>(Alt+F2)などの特殊キーは <名前>)、最後の手順のプロンプトが戻ったら画面を出力して完了する
//     (ランナーはそこで止まる)。プロンプトの代わりに <graphics>/<text>(表示モードの切り替わりを待つ)、<2s>(ゲスト時間で待つ)、
//     <idle>(ゲストが HLT で割り込みを待つだけになる = 入力待ちになるのを待つ)も書ける。キー列には PS/2 マウスの動作も書ける:
//     <mouse dx,dy>(画面座標で相対移動。右と下が正)、<click>/<rclick>(左/右ボタンを押して離す)。
//     ヘッドレスでゲストのシェルや GRUB のメニュー、グラフィック画面のインストーラを操作する用。キーの間隔はゲストの時計(GuestClock)で 0.2 秒。
public sealed class BootAutomation
{
    long KeyInterval => env.Clock.InstructionsPerSecond / 5; // 命令数(ゲスト時間で 0.2 秒)
    const int MaxDownPresses = 40;

    Script<Unit> rest;  // 残りの手順
    Func<bool> pending; // いま待っている条件(null ならまだ始めていない)
    EmuEnvironment env;
    long count;
    TextWriter output;

    BootAutomation(Func<BootAutomation, Script<Unit>> script, bool stopWhenCompleted)
    {
        rest = script(this);
        StopWhenCompleted = stopWhenCompleted;
    }

    public static BootAutomation NtldrDebug() => new(self => self.NtldrScript, stopWhenCompleted: false);

    public static BootAutomation Typing(IReadOnlyList<string> steps) => new(self => self.TypingScript(steps), stopWhenCompleted: true);

    public bool Completed { get; private set; }
    // 完了したらランナーが実行を止める(--type)。
    public bool StopWhenCompleted { get; }
    public string Status { get; private set; } = "WaitBoot";

    // 実行スレッドのタイマ周期から: 待ち条件が満たされていれば、次の満たされない待ちまで手順を進める。
    public void Step(EmuEnvironment env, long count, TextWriter output)
    {
        if (Completed) return;
        this.env = env;
        this.count = count;
        this.output = output;
        while (pending == null || pending())
        {
            var progress = rest();
            if (progress.Completed)
            {
                Completed = true;
                return;
            }
            (pending, rest) = (progress.Ready, progress.Rest);
        }
    }

    /// 手順書 ///////////////////////////////
    Script<Unit> NtldrScript =>
        from _1 in WaitFor(() => VgaBiosStub.IsInstalled(env))
        from _2 in Stage("PressF8", "boot started, pressing F8")
        from _3 in While(() => !ScreenContains(env, "Advanced Options Menu"), Press(0x42, 0xC2)) // F8 make/break
        from _4 in Do(() => ClearBiosKeyBuffer(env))
        from _5 in Stage("SelectDebugging", "advanced options menu shown, selecting Debugging Mode")
        from _6 in ShowScreen
        from _7 in Delay(KeyInterval)
        from selected in SelectDebuggingMode(presses: 0)
        from _8 in selected ? ConfirmOperatingSystem : Nothing
        select Unit.unit;

    // メニューが描き終わる(最後の項目が出る)のを待ち、ハイライト行を見ながら ↓ を送る。「Debugging Mode」がハイライトされたら
    // Enter を押して true。↓ を MaxDownPresses 回送っても来なければ諦めて false(手順はそこで終わる)。
    Script<bool> SelectDebuggingMode(int presses) =>
        from _ in Until(() => ScreenContains(env, "Return to OS Choices Menu"), poll: KeyInterval / 4)
        let highlighted = HighlightedLine(env)
        from selected in
            highlighted.Contains("Debugging Mode", StringComparison.OrdinalIgnoreCase) ? Confirm("Debugging Mode selected").Then(Return(true))
            : presses >= MaxDownPresses ? GiveUp(highlighted).Then(Return(false))
            : Press(0xE0, 0x50, 0xE0, 0xD0).Then(SelectDebuggingMode(presses + 1)) // ↓
        select selected;

    // OS 選択メニューが出たら Enter。出ないまま起動が進んだ(タイムアウト 0 の単一エントリは自動で選択される)、
    // またはデバッガ有効の表示が出た場合はそのまま終える。
    Script<Unit> ConfirmOperatingSystem =>
        from _1 in Do(() => Status = "WaitOsMenu")
        from deadline in Get(() => count + 41 * KeyInterval)
        from _2 in Delay(KeyInterval)
        from _3 in WaitFor(() => OperatingSystemMenuShown || DebuggerEnabled(env) || count > deadline)
        from _4 in OperatingSystemMenuShown ? Confirm("operating system menu confirmed")
            : DebuggerEnabled(env) ? Nothing
            : Do(() => Log("no operating system menu, assuming automatic selection"))
        select Unit.unit;

    bool OperatingSystemMenuShown => ScreenContains(env, "select the operating system");

    // --type の手順: 各手順は「プロンプト=>キー列」(プロンプト省略可)。プロンプトは画面の最後の空でない行の末尾と比べる
    // (シェルがコマンドを終えてプロンプトを出し直したことの判定)。打った直後はまだエコーが出ていないので、少し待ってから見る。
    // プロンプトの代わりに <graphics>/<text>(表示モードが切り替わるまで)、<2s>(ゲスト時間で 2 秒)、<idle>(ゲストが入力待ちになるまで)も書ける。
    // 最後の手順のプロンプトが戻ったら(プロンプトが無ければ少し待ってから)画面を出力して終える。
    Script<Unit> TypingScript(IReadOnlyList<string> steps) =>
        from _1 in Do(() => Status = "Type")
        let plan = ParseSteps(steps)
        from _2 in ForEach(plan, step =>
            from _a in WaitPrompt(step.prompt)
            from _b in ShowScreen
            from _c in Do(() => Log($"typing \"{step.keys.Replace("\n", "\\n")}\""))
            from _d in ForEach(Actions(step.keys), action => action)
            from _e in Delay(KeyInterval * 5)
            select Unit.unit)
        let lastPrompt = plan.Count > 0 ? plan[^1].prompt : ""
        from _3 in lastPrompt.Length > 0 ? WaitPrompt(lastPrompt) : Delay(KeyInterval * 10)
        from _4 in ShowScreen
        from _5 in Do(() => Log("typing completed"))
        select Unit.unit;

    // 手順を (プロンプト, キー列) に分ける。「=>」の無い手順は直前の手順のプロンプトを引き継ぐ(最初は空)。
    static List<(string prompt, string keys)> ParseSteps(IReadOnlyList<string> steps)
    {
        var plan = new List<(string prompt, string keys)>();
        var prompt = "";
        foreach (var step in steps)
        {
            var separator = step.IndexOf("=>", StringComparison.Ordinal);
            if (separator >= 0) prompt = step[..separator].Trim();
            plan.Add((prompt, Unescape(separator >= 0 ? step[(separator + 2)..] : step)));
        }
        return plan;
    }

    // キー列を動作の列に分ける: <mouse dx,dy> と <click>/<rclick> はマウスの動作、それ以外は Scancodes の 1 キーずつ
    // (キー名でもマウスでもない '<' は Scancodes がそのまま文字として打つ)。
    IEnumerable<Script<Unit>> Actions(string text)
    {
        for (var position = 0; position < text.Length;)
        {
            var close = text[position] == '<' ? text.IndexOf('>', position + 1) : -1;
            if (close > position && MouseAction(text[(position + 1)..close]) is { } mouse)
            {
                yield return mouse;
                position = close + 1;
                continue;
            }
            var next = close > position ? close + 1 : position + 1;
            foreach (var key in Scancodes(text[position..next])) yield return TypeKey(key);
            position = next;
        }
    }

    // "mouse dx,dy" は相対移動、"click"/"rclick" は左/右クリック。マウスの動作でなければ null。
    Script<Unit> MouseAction(string name)
    {
        if (name.Equals("click", StringComparison.OrdinalIgnoreCase)) return Click(1);
        if (name.Equals("rclick", StringComparison.OrdinalIgnoreCase)) return Click(2);
        var parts = name.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts[0].Equals("mouse", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out var dx) && int.TryParse(parts[2], out var dy) ? MouseMove(dx, dy) : null;
    }

    // マウスを画面座標で (dx, dy) 動かす。OS のポインタ加速(速い動きほど余計に進む)を抑えるため、1 パケット MouseStep カウント以内に分けて
    // 間隔を置いて送る(PS/2 は上が正なので dy は反転)。送り終えたら少し待つ。
    Script<Unit> MouseMove(int dx, int dy) =>
        ForEach(MousePackets(dx, dy), packet => Do(() => env.Mouse.Move(packet.dx, -packet.dy, 0, 0)).Then(Delay(KeyInterval / 25)))
            .Then(Delay(KeyInterval));

    const int MouseStep = 4;

    static IEnumerable<(int dx, int dy)> MousePackets(int dx, int dy)
    {
        while (dx != 0 || dy != 0)
        {
            var step = (dx: Math.Clamp(dx, -MouseStep, MouseStep), dy: Math.Clamp(dy, -MouseStep, MouseStep));
            yield return step;
            dx -= step.dx;
            dy -= step.dy;
        }
    }

    // その場でボタンを押して離す(bit0=左、bit1=右)。
    Script<Unit> Click(byte button) =>
        Do(() => env.Mouse.Move(0, 0, 0, button)).Then(Delay(KeyInterval / 4)).Then(Do(() => env.Mouse.Move(0, 0, 0, 0))).Then(Delay(KeyInterval));

    // 1 キーを打つ。BIOS 経由でキーを読むゲスト(GRUB など)は 1 キーの処理が遅く、BIOS のバッファ(16 キー)が溢れて取りこぼすので、
    // バッファが空くまで待ってから打つ。保護モードの OS は BDA を使わないので、上限(KeyInterval)で先へ進む。打った後は少し間を置く。
    Script<Unit> TypeKey(byte[] key) =>
        from deadline in Get(() => count + KeyInterval)
        from _1 in Until(() => BiosKeyBufferEmpty(env) || count >= deadline, poll: KeyInterval / 16)
        from _2 in Do(() => env.Keyboard.Inject(key))
        from _3 in Delay(KeyInterval / 4)
        select Unit.unit;

    /// 部分手順 ///////////////////////////////
    // 状態名を変えて経過を記録する。
    Script<Unit> Stage(string status, string message) => Do(() => { Status = status; Log(message); });

    Script<Unit> ShowScreen => Do(() => DumpScreen(env, output));

    // キーを打って(メイク/ブレークのスキャンコード列)、次のキーまでの間を置く。
    Script<Unit> Press(params byte[] scancodes) => Do(() => env.Keyboard.Inject(scancodes)).Then(Delay(KeyInterval));

    // 画面を出力してから Enter を押す(メニューの確定)。
    Script<Unit> Confirm(string message) => Do(() =>
    {
        DumpScreen(env, output);
        env.Keyboard.Inject(0x1C, 0x9C);
        Log(message);
    });

    Script<Unit> GiveUp(string highlighted) => Do(() =>
    {
        output.WriteLine($"[bootdebug] giving up: highlighted line is '{highlighted}'");
        DumpScreen(env, output);
    });

    // プロンプトを待つ(空なら待たない)。<graphics>/<text> は表示モードの切り替わり、<2s> はゲスト時間、
    // それ以外は画面の最後の行がその文字列で終わるまで。
    Script<Unit> WaitPrompt(string prompt)
    {
        if (prompt.Length == 0) return Nothing;
        if (prompt.Equals("<graphics>", StringComparison.OrdinalIgnoreCase)) return Until(() => env.Vga.GraphicsMode, poll: KeyInterval / 4);
        if (prompt.Equals("<text>", StringComparison.OrdinalIgnoreCase)) return Until(() => !env.Vga.GraphicsMode, poll: KeyInterval / 4);
        if (prompt.Equals("<idle>", StringComparison.OrdinalIgnoreCase)) return WaitIdle;
        if (prompt.Length > 3 && prompt[0] == '<' && prompt.EndsWith("s>", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(prompt[1..^2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return Delay((long)(seconds * env.Clock.InstructionsPerSecond));
        return Until(() => PromptShown(prompt), poll: KeyInterval / 4);
    }

    bool PromptShown(string prompt) =>
        !env.Vga.GraphicsMode && LastLine(env).EndsWith(prompt, StringComparison.OrdinalIgnoreCase);

    // ゲストが入力待ち(アイドル)になるまで待つ: ゲスト時間の 1 秒のうち 9 割以上を、HLT で次の割り込みを待って過ごしていること
    // (グラフィック画面のインストーラのように、画面の文字では入力待ちを見分けられない相手に使う)。
    Script<Unit> WaitIdle =>
        from start in Get(() => (count, idle: env.IdleInstructions))
        from _1 in Delay(env.Clock.InstructionsPerSecond)
        from _2 in (env.IdleInstructions - start.idle) * 10 >= (count - start.count) * 9 ? Nothing : WaitIdle
        select Unit.unit;

    // \n → Enter、\\ → \。
    static string Unescape(string text) => text.Replace("\\n", "\n").Replace("\\\\", "\\");

    /// スキャンコード(セット 1、US 配列) ///////////////////////////////
    const string Unshifted = "abcdefghijklmnopqrstuvwxyz1234567890-=[];'`\\,./ \n\t";
    const string Shifted = "ABCDEFGHIJKLMNOPQRSTUVWXYZ!@#$%^&*()_+{}:\"~|<>?";
    static readonly byte[] UnshiftedCodes =
    [
        0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
        0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x1A, 0x1B, 0x27, 0x28, 0x29, 0x2B, 0x33, 0x34, 0x35, 0x39, 0x1C, 0x0F,
    ];

    // 特殊キーのメイクコード(<名前> で書く。E0 プレフィックス付きはそのまま 2 バイト)。
    static readonly Dictionary<string, byte[]> SpecialKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = [0x01], ["bs"] = [0x0E], ["tab"] = [0x0F], ["enter"] = [0x1C], ["space"] = [0x39],
        ["caps"] = [0x3A], ["num"] = [0x45], ["scroll"] = [0x46],
        ["f1"] = [0x3B], ["f2"] = [0x3C], ["f3"] = [0x3D], ["f4"] = [0x3E], ["f5"] = [0x3F], ["f6"] = [0x40],
        ["f7"] = [0x41], ["f8"] = [0x42], ["f9"] = [0x43], ["f10"] = [0x44], ["f11"] = [0x57], ["f12"] = [0x58],
        ["home"] = [0xE0, 0x47], ["up"] = [0xE0, 0x48], ["pgup"] = [0xE0, 0x49], ["left"] = [0xE0, 0x4B], ["right"] = [0xE0, 0x4D],
        ["end"] = [0xE0, 0x4F], ["down"] = [0xE0, 0x50], ["pgdn"] = [0xE0, 0x51], ["ins"] = [0xE0, 0x52], ["del"] = [0xE0, 0x53],
    };

    // 1 キー分のスキャンコード列(メイク/ブレーク、シフトが要る文字はシフトで挟む)。配列に無い文字は飛ばす。
    // <名前> は特殊キー(<down>、<f2>)か修飾キー付き(<c-x> = Ctrl+x、<a-f2> = Alt+F2、<s-tab> = Shift+Tab、<w-r> = Windows+R)。名前が無ければ '<' は文字として打つ。
    public static IEnumerable<byte[]> Scancodes(string text)
    {
        for (var position = 0; position < text.Length; position++)
        {
            var character = text[position];
            if (character == '<')
            {
                var close = text.IndexOf('>', position + 1);
                if (close > position && TryParseKey(text[(position + 1)..close], out var sequence))
                {
                    yield return sequence;
                    position = close;
                    continue;
                }
            }
            var index = Unshifted.IndexOf(character);
            if (index >= 0) { var code = UnshiftedCodes[index]; yield return [code, (byte)(code | 0x80)]; continue; }
            index = Shifted.IndexOf(character);
            if (index >= 0) { var code = UnshiftedCodes[index]; yield return [0x2A, code, (byte)(code | 0x80), 0xAA]; }
        }
    }

    // 修飾キーのメイクコード(c = Ctrl、a = Alt、s = Shift、w = 左 Windows キー(E0 プレフィックス付き))。
    static readonly Dictionary<string, byte[]> ModifierKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c"] = [0x1D], ["ctrl"] = [0x1D], ["a"] = [0x38], ["alt"] = [0x38], ["s"] = [0x2A], ["shift"] = [0x2A], ["w"] = [0xE0, 0x5B], ["win"] = [0xE0, 0x5B],
    };

    // "c-a-del" のように修飾キーを '-' でつなぎ、最後がキー名か配列にある 1 文字。
    // 列は 修飾キーのメイク → キーのメイク → キーのブレーク(E0 プレフィックスはそのまま、最後のバイトに 0x80)→ 修飾キーのブレーク(逆順)。
    static bool TryParseKey(string name, out byte[] sequence)
    {
        sequence = null;
        var parts = name.Split('-');
        var modifiers = parts[..^1].Select(part => ModifierKeys.GetValueOrDefault(part)).ToArray();
        if (modifiers.Contains(null)) return false;
        var key = parts[^1];
        byte[] make;
        if (SpecialKeys.TryGetValue(key, out var special)) make = special;
        else if (key.Length == 1 && Unshifted.IndexOf(key[0]) is var index && index >= 0) make = [UnshiftedCodes[index]];
        else return false;
        sequence = [.. modifiers.SelectMany(modifier => modifier), .. make, .. Break(make), .. Enumerable.Reverse(modifiers).SelectMany(Break)];
        return true;
    }

    // ブレークコード: メイクコードの最後のバイトに 0x80(E0 プレフィックスはそのまま)。
    static IEnumerable<byte> Break(byte[] make) => [.. make[..^1], (byte)(make[^1] | 0x80)];

    // その手順に来たときの命令数から interval 命令数だけ待つ。
    Script<Unit> Delay(long interval) => () =>
    {
        var due = count + interval;
        return WaitFor(() => count >= due)();
    };

    // 条件が成り立つまで待つ。不成立を見たあとは poll 命令数おいてから再判定する(0 なら毎周期)。
    Script<Unit> Until(Func<bool> condition, long poll) => () =>
    {
        long due = 0;
        return WaitFor(() =>
        {
            if (count < due) return false;
            if (condition()) return true;
            due = count + poll;
            return false;
        })();
    };

    void Log(string message) => output.WriteLine($"[{(StopWhenCompleted ? "type" : "bootdebug")}] {message} (instr {count})");

    static bool DebuggerEnabled(EmuEnvironment env) =>
        !ScreenContains(env, "Advanced Options Menu") && ScreenContains(env, "debugger enabled");

    /// 画面の読み取り ///////////////////////////////
    // テキストモードの画面を 1 行ずつ(CRTC の開始アドレスとピッチを反映した、描画と同じ見え方)。text は 1 行分の文字(非表示文字は空白)、
    // highlighted は空白以外の文字に背景色が付いているか。
    static IEnumerable<(string text, bool highlighted)> ScreenLines(EmuEnvironment env) =>
        from row in Enumerable.Range(0, env.Vga.TextRows)
        let cells = Enumerable.Range(0, env.Vga.TextColumns).Select(column => env.Vga.TextCell(row, column)).ToList()
        select (
            text: string.Concat(cells.Select(cell => Printable(cell.Character))),
            highlighted: cells.Any(cell => cell.Character != 0x20 && (cell.Attribute & 0x70) != 0));

    static char Printable(byte value) => value is >= 0x20 and < 0x7F ? (char)value : ' ';

    // 画面に文字列が含まれるか(行をまたぐ一致も可)。大文字小文字は区別しない。
    public static bool ScreenContains(EmuEnvironment env, string text) =>
        !env.Vga.GraphicsMode &&
        string.Concat(ScreenLines(env).Select(line => line.text)).Contains(text, StringComparison.OrdinalIgnoreCase);

    // 空でない最後の行(前後の空白を除く)。無ければ空。
    public static string LastLine(EmuEnvironment env) =>
        ScreenLines(env).Select(line => line.text.Trim()).LastOrDefault(text => text.Length > 0) ?? "";

    // ハイライト(背景色付き)されている最初の行の文字列。無ければ空。
    static string HighlightedLine(EmuEnvironment env) =>
        ScreenLines(env).FirstOrDefault(line => line.highlighted).text?.Trim() ?? "";

    // 画面の空でない行を出力する(自動操作の経過確認用)。選択行には印を付ける。
    public static void DumpScreen(EmuEnvironment env, TextWriter output)
    {
        if (env.Vga.GraphicsMode) { output.WriteLine("[screen] (graphics mode)"); return; }
        foreach (var (text, highlighted) in ScreenLines(env))
        {
            var trimmed = text.TrimEnd();
            if (trimmed.Length > 0) output.WriteLine($"[screen]{(highlighted ? ">" : " ")} {trimmed}");
        }
    }

    // BIOS のキーバッファが空(読み出し位置と書き込み位置が同じ)。
    static bool BiosKeyBufferEmpty(EmuEnvironment env) =>
        BitConverter.ToUInt16(env.Ram, Bda.KeyBufferHead) == BitConverter.ToUInt16(env.Ram, Bda.KeyBufferTail);

    // BIOS のキーバッファを空にし(読み出し位置と書き込み位置をバッファの先頭へ)、注入待ちのキーも捨てる。
    static void ClearBiosKeyBuffer(EmuEnvironment env)
    {
        var ram = env.Ram;
        var start = BitConverter.ToUInt16(ram, Bda.KeyBufferStart);
        if (start == 0) start = 0x1E;
        BinaryPrimitives.WriteUInt16LittleEndian(ram.AsSpan(Bda.KeyBufferHead), start);
        BinaryPrimitives.WriteUInt16LittleEndian(ram.AsSpan(Bda.KeyBufferTail), start);
        while (env.Keyboard.Injected.TryDequeue(out _)) { }
        env.KbdOut.Clear();
    }
}
