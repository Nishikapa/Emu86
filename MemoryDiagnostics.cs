namespace Emu86;

// デバッグ用の記録: 物理アドレスのウォッチポイント、物理アドレス範囲への書き込みログ、
// プロテクトモード(ページング有効)割り込み配送のログ。EmuEnvironment.Diagnostics は何も有効でなければ null で、
// 実行経路は null 検査 1 回で素通りする。
public sealed class MemoryDiagnostics
{
    // WatchLo <= phys < WatchHi への書き込みで WatchTriggered を立て、Runner が原因命令の EIP を出力する。
    // ページング有効時のみ(ブート初期の BIOS による書き込みは対象外)。
    public uint WatchLo, WatchHi;
    public bool WatchTriggered;

    // [WriteLogLo, WriteLogHi) への書き込みを値付きで記録する(null で無効)。
    public uint WriteLogLo, WriteLogHi;
    public List<string> WriteLog;
    public uint CurrentEip; // Runner が命令実行前にセットする(ログの帰属用)

    // プロテクトモード割り込み配送のログ(null で無効)。V86 モードからの配送は件数が少ないので全件を別に残す。
    public List<string> InterruptLog;
    public List<string> InterruptLogV86 = [];

    public void OnWriteByte(uint physical, byte value, bool paging)
    {
        if (paging && physical >= WatchLo && physical < WatchHi) WatchTriggered = true;
        if (WriteLog != null && physical >= WriteLogLo && physical < WriteLogHi)
            WriteLog.Add($"{CurrentEip:x8}: [{physical:x8}]={value:x2}");
    }

    // 複数バイトの書き込み(書き込みログが有効なときは呼ばれず、1 バイトずつ OnWriteByte を通る)。
    public void OnWrite(uint physical, int size, bool paging)
    {
        if (paging && (ulong)physical + (uint)size > WatchLo && physical < WatchHi) WatchTriggered = true;
    }

    public void LogInterrupt(string entry, bool fromV86, ulong tsc, uint cr3)
    {
        InterruptLog.Add(entry);
        if (fromV86) InterruptLogV86.Add($"{entry} (instr {tsc}, cr3={cr3:x8})");
    }
}
