namespace Emu86;

// 8254 PIT(docs/adr/0004)。チャネル 0(IRQ0)と 2(スピーカ、TSC 較正)を、再装填値とモードどおりにゲストの時計
// (命令数 → 1.193182 MHz のティック)で数える。カウントは「開始時刻からの経過ティック」から計算するだけで、周期ごとの更新はしない。
//   モード 0/1/4/5: 1 回だけ数える(終端で OUT が上がり、IRQ0 は 1 回。終端後も 16 ビットで回り続ける)。
//   モード 2/3: 周期的(3 は 2 ずつ減り、前半 OUT=1・後半 0)。
//   チャネル 0 の設定が変わると Changed が立ち、ランナーが次の IRQ0 の時刻(NextTimerIrq)を計算し直す。
//   ポート 0x61: bit0 = チャネル 2 のゲート(開いたところから数える)、bit1 = スピーカ、bit4 = DRAM リフレッシュ(15.085µs = 18 ティックごとに反転)、
//   bit5 = チャネル 2 の OUT。チャネル 1(DRAM リフレッシュ)は数えない。
internal sealed class PitDevice(GuestClock clock)
{
    public sealed class Channel
    {
        public ushort Reload;              // 0 = 65536
        public byte Mode = 3;
        public byte Access = 3;            // 1 下位、2 上位、3 下位→上位
        public byte WritePhase, ReadPhase;
        public ushort Pending;             // 下位→上位の書き込み途中の下位バイト
        public ulong Started;              // カウント開始(再装填値を書き終えた、またはゲートが開いた)時刻
        public bool Programmed;
        public int Latched = -1;           // ラッチしたカウント(-1 = 無し)
        public int StatusLatched = -1;     // ラッチした状態バイト(-1 = 無し)

        public uint Period => Reload == 0 ? 65536u : Reload;
        public bool OneShot => Mode is 0 or 1 or 4 or 5;

        internal static readonly Codec<Channel> Layout = new Codec<Channel>()
            .U16(c => c.Reload, (c, v) => c.Reload = v)
            .U8(c => c.Mode, (c, v) => c.Mode = v)
            .U8(c => c.Access, (c, v) => c.Access = v)
            .U8(c => c.WritePhase, (c, v) => c.WritePhase = v)
            .U8(c => c.ReadPhase, (c, v) => c.ReadPhase = v)
            .U16(c => c.Pending, (c, v) => c.Pending = v)
            .U64(c => c.Started, (c, v) => c.Started = v)
            .Bool(c => c.Programmed, (c, v) => c.Programmed = v)
            .I32(c => c.Latched, (c, v) => c.Latched = v)
            .I32(c => c.StatusLatched, (c, v) => c.StatusLatched = v);
    }

    public readonly Channel Channel0 = new(), Channel2 = new();
    public byte Port61;
    // チャネル 0 の設定が変わった(ランナーが NextTimerIrq を取り直す)。
    public bool Changed = true;

    ulong Elapsed(Channel channel, ulong tsc) => tsc > channel.Started ? clock.PitTicks(tsc - channel.Started) : 0;

    // 現在のカウント(周期モードは 1..Period。モード 3 は 2 ずつ減る)。
    public int Count(Channel channel, ulong tsc)
    {
        var elapsed = Elapsed(channel, tsc);
        var period = channel.Period;
        if (channel.OneShot) return (int)((period - elapsed) & 0xFFFF);
        if (channel.Mode == 3) return (int)(period - (2 * elapsed) % period);
        return (int)(period - elapsed % period);
    }

    // OUT: モード 0 系は終端で 1、モード 2 は終端の 1 ティックだけ 0、モード 3 は前半 1・後半 0。
    // ゲートが閉じたチャネル 2 は数えない(モード 2/3 は 1、1 回だけのモードは 0)。
    public bool Out(Channel channel, ulong tsc, bool gate = true)
    {
        if (!gate) return !channel.OneShot;
        var elapsed = Elapsed(channel, tsc);
        var period = channel.Period;
        return channel.Mode switch
        {
            0 or 1 or 4 or 5 => elapsed >= period,
            2 => elapsed % period != period - 1,
            _ => elapsed % period < (period + 1) / 2,
        };
    }

    // 次に IRQ0 が立つ命令数(tsc より後)。1 回だけのモードで終端を過ぎていれば long.MaxValue(次の設定まで立たない)。
    public long NextTimerIrq(ulong tsc)
    {
        var channel = Channel0;
        var period = channel.Period;
        var elapsed = Elapsed(channel, tsc);
        if (channel.OneShot)
            return elapsed >= period ? long.MaxValue : Math.Max((long)(channel.Started + clock.Instructions(period, GuestClock.PitHz)), (long)tsc + 1);
        var next = channel.Started + clock.Instructions((elapsed / period + 1) * period, GuestClock.PitHz);
        return Math.Max((long)next, (long)tsc + 1);
    }

    public byte Read(int port, ulong tsc) => port switch
    {
        0x40 => ReadCount(Channel0, tsc),
        0x42 => ReadCount(Channel2, tsc),
        0x61 => (byte)((Port61 & 0x0F)
            | (((clock.PitTicks(tsc) / 18) & 1) != 0 ? 0x10 : 0)
            | (Out(Channel2, tsc, (Port61 & 1) != 0) ? 0x20 : 0)),
        _ => throw new ArgumentOutOfRangeException(nameof(port)),
    };

    // ラッチした状態 → ラッチしたカウント → 現在のカウントの順。下位→上位の読み出しは 2 バイトを同じカウントから返す。
    byte ReadCount(Channel channel, ulong tsc)
    {
        if (channel.StatusLatched >= 0)
        {
            var status = (byte)channel.StatusLatched;
            channel.StatusLatched = -1;
            return status;
        }
        var count = channel.Latched >= 0 ? channel.Latched : Count(channel, tsc);
        switch (channel.Access)
        {
            case 1: channel.Latched = -1; return (byte)count;
            case 2: channel.Latched = -1; return (byte)(count >> 8);
            default:
                if (channel.ReadPhase == 0)
                {
                    channel.Latched = count;
                    channel.ReadPhase = 1;
                    return (byte)count;
                }
                channel.ReadPhase = 0;
                channel.Latched = -1;
                return (byte)(count >> 8);
        }
    }

    public void Write(int port, byte value, ulong tsc)
    {
        switch (port)
        {
            case 0x43: Control(value, tsc); break;
            case 0x40: Load(Channel0, value, tsc); Changed = true; break;
            case 0x42: Load(Channel2, value, tsc); break;
            case 0x61:
                if ((value & 1) != 0 && (Port61 & 1) == 0) Channel2.Started = tsc; // ゲートが開いたところから数える
                Port61 = (byte)(value & 0x0F);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    // 制御語: bit7-6 チャネル(3 = リードバック)、bit5-4 アクセス(0 = カウントのラッチ)、bit3-1 モード、bit0 BCD(無視)。
    void Control(byte value, ulong tsc)
    {
        var select = value >> 6;
        if (select == 3)
        {
            // リードバック: bit5=0 でカウント、bit4=0 で状態をラッチ。bit1 = チャネル 0、bit3 = チャネル 2。
            if ((value & 0x02) != 0) ReadBack(Channel0, value, tsc, gate: true);
            if ((value & 0x08) != 0) ReadBack(Channel2, value, tsc, gate: (Port61 & 1) != 0);
            return;
        }
        var channel = select == 0 ? Channel0 : select == 2 ? Channel2 : null;
        if (channel == null) return;
        var access = (value >> 4) & 3;
        if (access == 0) { Latch(channel, tsc); return; }
        channel.Access = (byte)access;
        var mode = (value >> 1) & 7;
        channel.Mode = (byte)(mode >= 6 ? mode - 4 : mode);
        channel.WritePhase = 0;
        channel.ReadPhase = 0;
        if (channel == Channel0) Changed = true;
    }

    void ReadBack(Channel channel, byte value, ulong tsc, bool gate)
    {
        if ((value & 0x20) == 0) Latch(channel, tsc);
        if ((value & 0x10) == 0 && channel.StatusLatched < 0)
            channel.StatusLatched = (Out(channel, tsc, gate) ? 0x80 : 0) | (channel.Access << 4) | (channel.Mode << 1);
    }

    void Latch(Channel channel, ulong tsc)
    {
        if (channel.Latched < 0) channel.Latched = Count(channel, tsc);
        channel.ReadPhase = 0;
    }

    // 再装填値の書き込み。書き終えたところからカウントを始める(1 回だけのモードの再設定もここで始め直す)。
    void Load(Channel channel, byte value, ulong tsc)
    {
        switch (channel.Access)
        {
            case 1: channel.Reload = value; break;
            case 2: channel.Reload = (ushort)(value << 8); break;
            default:
                if (channel.WritePhase == 0)
                {
                    channel.Pending = value;
                    channel.WritePhase = 1;
                    return;
                }
                channel.Reload = (ushort)(channel.Pending | (value << 8));
                channel.WritePhase = 0;
                break;
        }
        channel.Started = tsc;
        channel.Programmed = true;
        channel.Latched = -1;
    }
}
