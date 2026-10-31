namespace Emu86;

// 画面(テキスト VRAM)を見ながらキーを注入する自動操作。手順書はイテレータ(コルーチン)で、yield で返した待ち条件
// (画面に文字列が出る、一定命令数が経つ)が満たされるまで Step は先へ進まない。判定は VRAM のプレーン 0(文字)だけを見る。
// 実行スレッドのタイマ周期で Step を呼ぶ。手順書は 2 種類:
//   NtldrDebug (--boot-debug): NTLDR の起動メニューを操作し、boot.ini を変えずに「デバッグ モード」(/debug、XP の既定は COM2 19200bps)で起動する。
//     1. ブートセクタへ制御が移ったら(VGA BIOS スタブの導入 = INT 19h 以降)、F8 を繰り返し押す。
//        NTLDR は起動直後の短い時間だけ F8 を見るが、BIOS のキーバッファに先に積んでおけば確実に拾われる。
//     2. テキスト VRAM に「Windows Advanced Options Menu」が出たら F8 をやめ、残っているキーを BDA のバッファから捨て、
//        ハイライト行を見ながら ↓ を押して「Debugging Mode」を選び Enter。
//     3. OS 選択メニュー(「Please select the operating system」)が出たら Enter。
//   Typing (--type): 「プロンプト=>キー列」の手順を順に。画面の最後の行がプロンプトで終わるのを待ってキー列を打ち(\n は Enter)、
//     最後の手順のプロンプトが戻ったら画面を出力して完了する(ランナーはそこで止まる)。ヘッドレスでゲストのシェルを調べる用。
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

    BootAutomation(Func<BootAutomation, IEnumerable<Wait>> script, bool stopWhenCompleted)
    {
        this.script = script(this).GetEnumerator();
        StopWhenCompleted = stopWhenCompleted;
    }

    public static BootAutomation NtldrDebug() => new(self => self.NtldrScript(), stopWhenCompleted: false);

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
            if (!script.MoveNext())
            {
                Completed = true;
                return;
            }
            pending = script.Current;
        }
    }

    IEnumerable<Wait> NtldrScript()
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

    // --type の手順: 各手順は「プロンプト=>キー列」(プロンプト省略可)。プロンプトは画面の最後の空でない行の末尾と比べる
    // (シェルがコマンドを終えてプロンプトを出し直したことの判定)。打った直後はまだエコーが出ていないので、少し待ってから見る。
    IEnumerable<Wait> TypingScript(IReadOnlyList<string> steps)
    {
        Status = "Type";
        var prompt = "";
        foreach (var step in steps)
        {
            var separator = step.IndexOf("=>", StringComparison.Ordinal);
            if (separator >= 0) prompt = step[..separator].Trim();
            var keys = Unescape(separator >= 0 ? step[(separator + 2)..] : step);
            if (prompt.Length > 0) yield return Until(() => PromptShown(prompt), poll: KeyInterval / 4);
            DumpScreen(env, output);
            Log($"typing \"{keys.Replace("\n", "\\n")}\"");
            foreach (var key in Scancodes(keys))
            {
                env.Keyboard.Inject(key);
                yield return Delay(KeyInterval / 4);
            }
            yield return Delay(KeyInterval * 5);
        }
        if (prompt.Length > 0) yield return Until(() => PromptShown(prompt), poll: KeyInterval / 4);
        else yield return Delay(KeyInterval * 10);
        DumpScreen(env, output);
        Log("typing completed");
    }

    bool PromptShown(string prompt) =>
        !env.Vga.GraphicsMode && LastLine(env).EndsWith(prompt, StringComparison.OrdinalIgnoreCase);

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

    // 1 文字分のスキャンコード列(メイク/ブレーク、シフトが要る文字はシフトで挟む)。配列に無い文字は飛ばす。
    public static IEnumerable<byte[]> Scancodes(string text)
    {
        foreach (var character in text)
        {
            var index = Unshifted.IndexOf(character);
            if (index >= 0) { var code = UnshiftedCodes[index]; yield return [code, (byte)(code | 0x80)]; continue; }
            index = Shifted.IndexOf(character);
            if (index >= 0) { var code = UnshiftedCodes[index]; yield return [0x2A, code, (byte)(code | 0x80), 0xAA]; }
        }
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
        foreach (var (text, highlighted) in ScreenLines(env))
        {
            var trimmed = text.TrimEnd();
            if (trimmed.Length > 0) output.WriteLine($"[screen]{(highlighted ? ">" : " ")} {trimmed}");
        }
    }

    static void ClearBiosKeyBuffer(EmuEnvironment env)
    {
        var ram = env.Ram;
        var start = BitConverter.ToUInt16(ram, 0x480);
        if (start == 0) start = 0x1E;
        ram[0x41A] = (byte)start; ram[0x41B] = (byte)(start >> 8);
        ram[0x41C] = (byte)start; ram[0x41D] = (byte)(start >> 8);
        while (env.Keyboard.Injected.TryDequeue(out _)) { }
        env.KbdOut.Clear();
    }
}
