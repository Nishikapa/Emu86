namespace Emu86;

// ゲストの時計(docs/adr/0004): 1 秒 = InstructionsPerSecond 命令。実行済み命令数(env.Tsc)だけが時間の源で、
// PIT(1.193182 MHz)・ACPI PM タイマ(3.579545 MHz)・RTC(1 Hz)・ポート 0x61 のリフレッシュはここで命令数と換算する。
// 決定性(同じ入力なら同じ実行)は保ったまま、ゲストから見た CPU の速さを決める。既定の 5,000 万命令/秒は翻訳ありの実行速度に近く、
// ゲスト時間がおおむね実時間で進む。--ips で変えられ、スナップショットには環境ごとの値を保存する。
public sealed class GuestClock
{
    public const long PitHz = 1_193_182;
    public const long PmTimerHz = 3_579_545;

    // 新しい環境の既定(--ips)。
    public static long DefaultInstructionsPerSecond = 50_000_000;

    public long InstructionsPerSecond = DefaultInstructionsPerSecond;

    // 命令数 → hz のティック数。秒の部分と端数を分けて計算し、長い走行でも溢れない。
    public ulong Ticks(ulong instructions, long hz)
    {
        var ips = (ulong)InstructionsPerSecond;
        return instructions / ips * (ulong)hz + instructions % ips * (ulong)hz / ips;
    }

    // hz のティック数 → 命令数(切り捨て)。
    public ulong Instructions(ulong ticks, long hz)
    {
        var ips = (ulong)InstructionsPerSecond;
        return ticks / (ulong)hz * ips + ticks % (ulong)hz * ips / (ulong)hz;
    }

    public ulong PitTicks(ulong instructions) => Ticks(instructions, PitHz);

    public ulong PmTicks(ulong instructions) => Ticks(instructions, PmTimerHz);

    public ulong Seconds(ulong instructions) => instructions / (ulong)InstructionsPerSecond;
}
