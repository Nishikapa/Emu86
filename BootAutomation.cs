namespace Emu86;

// NTLDR の起動メニューをキー注入で操作し、boot.ini を変えずに「デバッグ モード」(/debug、XP の既定は
// COM2 19200bps)で Windows を起動する(--boot-debug)。手順は Script に上から順に書いてある:
//   1. ブートセクタへ制御が移ったら(VGA BIOS スタブの導入 = INT 19h 以降)、F8 を繰り返し押す。
//      NTLDR は起動直後の短い時間だけ F8 を見るが、BIOS のキーバッファに先に積んでおけば確実に拾われる。
//   2. テキスト VRAM に「Windows Advanced Options Menu」が出たら F8 をやめ、残っているキーを BDA のバッファから捨て、
//      ハイライト行を見ながら ↓ を押して「Debugging Mode」を選び Enter。
//   3. OS 選択メニュー(「Please select the operating system」)が出たら Enter。
// Script はイテレータ(コルーチン)で、yield で返した待ち条件(画面に文字列が出る、一定命令数が経つ)が
// 満たされるまで Step は先へ進まない。判定は VRAM のプレーン 0(文字)だけを見る。実行スレッドのタイマ周期で Step を呼ぶ。
public sealed class BootAutomation
{
    // 手順書の 1 ステップが待つ条件。真を返したら次のステップへ進む。
    delegate bool Wait();

    const long KeyInterval = 200_000; // 命令数(仮想時間で 0.2 秒)
    const int MaxDownPresses = 40;

    readonly IEnumerator<Wait> script;
    Wait pending;
    EmuEnvironment env;
    long count;
    TextWriter output;

    public BootAutomation() => script = Script().GetEnumerator();

    public bool Completed { get; private set; }
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
            if (!script.MoveNext())
            {
                Completed = true;
                return;
            }
            pending = script.Current;
        }
    }

    IEnumerable<Wait> Script()
    {
        yield return () => VgaBiosStub.IsInstalled(env);
        Status = "PressF8";
        Log("boot started, pressing F8");
        while (!ScreenContains(env, "Advanced Options Menu"))
        {
            env.Keyboard.Inject(0x42, 0xC2); // F8 make/break
            yield return Delay(KeyInterval);
        }
        ClearBiosKeyBuffer(env);
        Status = "SelectDebugging";
        Log("advanced options menu shown, selecting Debugging Mode");
        DumpScreen(env, output);
        yield return Delay(KeyInterval);

        // メニューが描き終わる(最後の項目が出る)のを待ち、ハイライト行を見ながら ↓ を送る。
        for (var attempts = 0; ; attempts++)
        {
            yield return Until(() => ScreenContains(env, "Return to OS Choices Menu"), poll: KeyInterval / 4);
            var highlighted = HighlightedLine(env);
            if (highlighted.Contains("Debugging Mode", StringComparison.OrdinalIgnoreCase))
            {
                DumpScreen(env, output);
                env.Keyboard.Inject(0x1C, 0x9C); // Enter
                Log("Debugging Mode selected");
                break;
            }
            if (attempts >= MaxDownPresses)
            {
                output.WriteLine($"[bootdebug] giving up: highlighted line is '{highlighted}'");
                DumpScreen(env, output);
                yield break;
            }
            env.Keyboard.Inject(0xE0, 0x50, 0xE0, 0xD0); // ↓
            yield return Delay(KeyInterval);
        }

        // OS 選択メニューが出たら Enter。出ないまま起動が進んだ(タイムアウト 0 の単一エントリは自動で選択される)、
        // またはデバッガ有効の表示が出た場合はそのまま終える。
        Status = "WaitOsMenu";
        var deadline = count + 41 * KeyInterval;
        yield return Delay(KeyInterval);
        yield return () => ScreenContains(env, "select the operating system") || DebuggerEnabled(env) || count > deadline;
        if (ScreenContains(env, "select the operating system"))
        {
            DumpScreen(env, output);
            env.Keyboard.Inject(0x1C, 0x9C);
            Log("operating system menu confirmed");
        }
        else if (!DebuggerEnabled(env))
            Log("no operating system menu, assuming automatic selection");
    }

    // 現在の命令数から interval 命令数だけ待つ。
    Wait Delay(long interval)
    {
        var due = count + interval;
        return () => count >= due;
    }

    // 条件が成り立つまで待つ。不成立を見たあとは poll 命令数おいてから再判定する(0 なら毎周期)。
    Wait Until(Func<bool> condition, long poll)
    {
        long due = 0;
        return () =>
        {
            if (count < due) return false;
            if (condition()) return true;
            due = count + poll;
            return false;
        };
    }

    void Log(string message) => output.WriteLine($"[bootdebug] {message} (instr {count})");

    static bool DebuggerEnabled(EmuEnvironment env) =>
        !ScreenContains(env, "Advanced Options Menu") && ScreenContains(env, "debugger enabled");

    /// 画面の読み取り ///////////////////////////////
    // テキストモードの画面(プレーン 0、80×25)を 1 行ずつ。text は 80 文字(非表示文字は空白)、
    // highlighted は空白以外の文字に背景色が付いているか。
    static IEnumerable<(string text, bool highlighted)> ScreenLines(EmuEnvironment env) =>
        from row in Enumerable.Range(0, 25)
        let units = Enumerable.Range(row * 80, 80)
        select (
            text: string.Concat(units.Select(unit => Printable(env.Vga.Vram[unit]))),
            highlighted: units.Any(unit => env.Vga.Vram[unit] != 0x20 && (env.Vga.Vram[0x10000 + unit] & 0x70) != 0));

    static char Printable(byte value) => value is >= 0x20 and < 0x7F ? (char)value : ' ';

    // 画面に文字列が含まれるか(行をまたぐ一致も可)。大文字小文字は区別しない。
    public static bool ScreenContains(EmuEnvironment env, string text) =>
        !env.Vga.GraphicsMode &&
        string.Concat(ScreenLines(env).Select(line => line.text)).Contains(text, StringComparison.OrdinalIgnoreCase);

    // ハイライト(背景色付き)されている最初の行の文字列。無ければ空。
    static string HighlightedLine(EmuEnvironment env) =>
        ScreenLines(env).Where(line => line.highlighted).Select(line => line.text.Trim()).FirstOrDefault() ?? "";

    // 画面の空でない行を出力する(自動操作の経過確認用)。選択行には印を付ける。
    static void DumpScreen(EmuEnvironment env, TextWriter output)
    {
        foreach (var (text, highlighted) in ScreenLines(env))
        {
            var trimmed = text.TrimEnd();
            if (trimmed.Length > 0) output.WriteLine($"[screen]{(highlighted ? ">" : " ")} {trimmed}");
        }
    }

    static void ClearBiosKeyBuffer(EmuEnvironment env)
    {
        var ram = env.OneMegaMemory_;
        var start = BitConverter.ToUInt16(ram, 0x480);
        if (start == 0) start = 0x1E;
        ram[0x41A] = (byte)start; ram[0x41B] = (byte)(start >> 8);
        ram[0x41C] = (byte)start; ram[0x41D] = (byte)(start >> 8);
        while (env.Keyboard.Injected.TryDequeue(out _)) { }
        env.KbdOut.Clear();
    }
}
