using System.Text;

namespace Emu86;

// NTLDR の起動メニューをキー注入で操作し、boot.ini を変えずに「デバッグ モード」(/debug、XP の既定は
// COM2 19200bps)で Windows を起動する(--boot-debug)。手順:
//   1. ブートセクタへ制御が移ったら(VGA BIOS スタブの導入 = INT 19h 以降)、F8 を繰り返し押す。
//      NTLDR は起動直後の短い時間だけ F8 を見るが、BIOS のキーバッファに先に積んでおけば確実に拾われる。
//   2. テキスト VRAM に「Windows Advanced Options Menu」が出たら F8 をやめ、残っているキーを BDA のバッファから捨て、
//      ↓ を 7 回押して「Debugging Mode」を選び Enter。
//   3. OS 選択メニュー(「Please select the operating system」)が出たら Enter。
// 判定は VRAM のプレーン 0(文字)だけを見る。実行スレッドのタイマ周期で Step を呼ぶ。
public sealed class BootAutomation
{
    enum Phase { WaitBoot, PressF8, SelectDebugging, WaitOsMenu, Done }

    Phase phase = Phase.WaitBoot;
    long nextAction;
    int attempts;
    public bool Completed => phase == Phase.Done;
    public string Status => phase.ToString();

    const long KeyInterval = 200_000; // 命令数(仮想時間で 0.2 秒)

    public void Step(EmuEnvironment env, long count, TextWriter output)
    {
        if (phase == Phase.Done || count < nextAction) return;
        switch (phase)
        {
            case Phase.WaitBoot:
                if (!VgaBiosStub.IsInstalled(env)) return;
                phase = Phase.PressF8;
                output.WriteLine($"[bootdebug] boot started, pressing F8 (instr {count})");
                goto case Phase.PressF8;
            case Phase.PressF8:
                if (ScreenContains(env, "Advanced Options Menu"))
                {
                    ClearBiosKeyBuffer(env);
                    phase = Phase.SelectDebugging;
                    output.WriteLine($"[bootdebug] advanced options menu shown, selecting Debugging Mode (instr {count})");
                    DumpScreen(env, output);
                    nextAction = count + KeyInterval;
                    return;
                }
                env.Keyboard.Inject(0x42, 0xC2); // F8 make/break
                nextAction = count + KeyInterval;
                return;
            case Phase.SelectDebugging:
                // メニューが描き終わる(最後の項目が出る)のを待ち、ハイライト行を見ながら ↓ を送る。
                if (!ScreenContains(env, "Return to OS Choices Menu")) { nextAction = count + KeyInterval / 4; return; }
                var highlighted = HighlightedLine(env);
                if (highlighted.Contains("Debugging Mode", StringComparison.OrdinalIgnoreCase))
                {
                    DumpScreen(env, output);
                    env.Keyboard.Inject(0x1C, 0x9C); // Enter
                    phase = Phase.WaitOsMenu;
                    output.WriteLine($"[bootdebug] Debugging Mode selected (instr {count})");
                }
                else if (++attempts > 40)
                {
                    output.WriteLine($"[bootdebug] giving up: highlighted line is '{highlighted}'");
                    DumpScreen(env, output);
                    phase = Phase.Done;
                }
                else
                    env.Keyboard.Inject(0xE0, 0x50, 0xE0, 0xD0); // ↓
                nextAction = count + KeyInterval;
                return;
            case Phase.WaitOsMenu:
                if (ScreenContains(env, "select the operating system"))
                {
                    DumpScreen(env, output);
                    env.Keyboard.Inject(0x1C, 0x9C);
                    output.WriteLine($"[bootdebug] operating system menu confirmed (instr {count})");
                    phase = Phase.Done;
                }
                else if (!ScreenContains(env, "Advanced Options Menu") && ScreenContains(env, "debugger enabled"))
                    phase = Phase.Done;
                else if (count > nextAction + 40 * KeyInterval)
                {
                    // メニューが出ないまま起動が進んだ(タイムアウト 0 の単一エントリは自動で選択される)。
                    output.WriteLine($"[bootdebug] no operating system menu, assuming automatic selection (instr {count})");
                    phase = Phase.Done;
                }
                return;
        }
    }

    // テキストモードの画面(プレーン 0、80×25)に文字列が含まれるか。大文字小文字は区別しない。
    public static bool ScreenContains(EmuEnvironment env, string text)
    {
        if (env.Vga.GraphicsMode) return false;
        var chars = new StringBuilder(80 * 25);
        for (var unit = 0; unit < 80 * 25; unit++)
        {
            var value = env.Vga.Vram[unit];
            chars.Append(value is >= 0x20 and < 0x7F ? (char)value : ' ');
        }
        return chars.ToString().Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    // ハイライト(背景色付き)されている最初の行の文字列。無ければ空。
    static string HighlightedLine(EmuEnvironment env)
    {
        for (var row = 0; row < 25; row++)
        {
            var line = new StringBuilder();
            var highlighted = false;
            for (var column = 0; column < 80; column++)
            {
                var unit = row * 80 + column;
                var value = env.Vga.Vram[unit];
                line.Append(value is >= 0x20 and < 0x7F ? (char)value : ' ');
                if (value != 0x20 && (env.Vga.Vram[0x10000 + unit] & 0x70) != 0) highlighted = true;
            }
            if (highlighted) return line.ToString().Trim();
        }
        return "";
    }

    // 画面の空でない行を出力する(自動操作の経過確認用)。属性(反転表示)も見て選択行に印を付ける。
    static void DumpScreen(EmuEnvironment env, TextWriter output)
    {
        for (var row = 0; row < 25; row++)
        {
            var line = new StringBuilder();
            var highlighted = false;
            for (var column = 0; column < 80; column++)
            {
                var unit = row * 80 + column;
                var value = env.Vga.Vram[unit];
                line.Append(value is >= 0x20 and < 0x7F ? (char)value : ' ');
                if (value != 0x20 && (env.Vga.Vram[0x10000 + unit] & 0x70) != 0) highlighted = true;
            }
            var text = line.ToString().TrimEnd();
            if (text.Length > 0) output.WriteLine($"[screen]{(highlighted ? ">" : " ")} {text}");
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
