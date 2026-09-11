namespace Emu86;

internal sealed class PitDevice
{
    public ushort Counter = 0xFFFF;
    public ushort Latched = 0xFFFF;
    public int ReadPhase;
    public byte Port61;
    public byte Refresh;
    public ushort Channel2Counter = 0xFFFF;
    public int Channel2WritePhase;
    public int Channel2ReadPhase;
    public bool Channel2Armed;
    public int Channel2Wait;
    public bool StatusPending;

    public byte Read(int port)
    {
        switch (port)
        {
            case 0x61: // システム制御ポートB(NMI/PIT ch2 ゲート・スピーカ)
                // bit4 = リフレッシュタイマ(読むたびにトグルさせて DRAM リフレッシュ
                //         監視のディレイループを進める)。
                // bit5 = PIT ch2 の OUT。ゲート有効かつ ch2 がプログラム済みなら、
                //         数回読んだ後に High にして TSC 校正のウェイトを抜けさせる。
                Refresh ^= 0x10;
                byte out2 = 0;
                if ((Port61 & 1) != 0 && Channel2Armed)
                {
                    if (Channel2Wait > 0) Channel2Wait--;
                    else out2 = 0x20;
                }
                return (byte)((Port61 & 0x0F) | Refresh | out2);
            case 0x42: // PIT チャネル2 データ(読み出しはラッチ値を返す)
                byte c2 = (byte)(Channel2ReadPhase == 0 ? Channel2Counter : Channel2Counter >> 8);
                Channel2ReadPhase ^= 1;
                return c2;
            case 0x40: // PIT チャネル0
                // リードバックでステータスがラッチされていれば、まずそれを返す。
                //   0x36 = OUT=0, NULL COUNT=0(カウント有効), lo/hi アクセス, モード3, 二進
                // Linux の i8254 エントロピー読み(KASLR)は NULL COUNT ビットが
                // 落ちるまでポーリングするため、これがないと無限ループになる。
                if (StatusPending)
                {
                    StatusPending = false;
                    return 0x36;
                }
                byte b = (byte)(ReadPhase == 0 ? Latched : Latched >> 8);
                ReadPhase ^= 1;
                return b;
            default: throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    public void Write(int port, byte val)
    {
        switch (port)
        {
            case 0x43: // PIT コントロール: ラッチ/リードバックでカウンタを捕捉し時刻を進める
                //   カウンタラッチ  : bit5-4 = 00
                //   リードバック    : bit7-6 = 11。bit5=0 でカウント、bit4=0 でステータスをラッチ
                int channel = (val >> 6) & 3;
                if (channel == 2)
                {
                    // ch2 のプログラム開始。lo/hi 書き込みの位相をリセットする。
                    Channel2WritePhase = 0;
                    Channel2Armed = false;
                    break;
                }
                bool readback = (val & 0xC0) == 0xC0;
                bool latch = (val & 0x30) == 0 || (readback && (val & 0x20) == 0);
                if (latch)
                {
                    Latched = Counter;
                    ReadPhase = 0;
                    Counter -= 0x100; // 経過時間の代用として下向きに減算する
                }
                if (readback && (val & 0x10) == 0)
                    StatusPending = true;
                break;
            case 0x42: // PIT チャネル2 カウント(lo→hi)。全部書けたら「短時間で満了」として武装する。
                if (Channel2WritePhase == 0) { Channel2Counter = val; Channel2WritePhase = 1; }
                else
                {
                    Channel2Counter = (ushort)((Channel2Counter & 0xFF) | (val << 8));
                    Channel2WritePhase = 0;
                    Channel2Armed = true;
                    Channel2Wait = 2; // 数回 0x61 を読んだら OUT2 を立てる(ウェイトを抜けさせる)
                }
                break;
            case 0x61: // システム制御ポートB。低位ビット(ゲート/スピーカ)を保持する。
                Port61 = val;
                if ((val & 1) == 0) Channel2Armed = false; // ゲート断で武装解除
                break;
            default: throw new ArgumentOutOfRangeException(nameof(port));
        }
    }
}
