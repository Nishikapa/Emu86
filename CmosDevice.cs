namespace Emu86;

// CMOS RAM(128 バイト)と RTC(MC146818 互換)。
// 時計はゲストの時刻(命令数: 100 万命令 = 1 秒、ランナーが Tsc に入れる)で進む。Data の時刻レジスタ(0/2/4/7/8/9/0x32)は
// 「Tsc = 0 のときの時刻」を常に BCD・24 時間制で保持し(電池で動く時計の初期値に相当)、読むときに Tsc 秒を足して
// 状態 B の形式(BCD/二進、24/12 時間)で返し、書くときは Tsc 秒を引いて戻す。状態は CMOS RAM だけで持てるので、
// スナップショットの再開でも時刻は連続する。曜日(6)は日付から求める。アラーム・周期割り込み(IRQ8)は未実装。
internal sealed class CmosDevice
{
    public byte[] Data = new byte[128];
    public int Index;

    const int StatusA = 0x0A, StatusB = 0x0B, StatusC = 0x0C, StatusD = 0x0D, Century = 0x32;
    const uint InstructionsPerSecond = 1_000_000;
    static readonly DateTime Default = new(2000, 1, 1, 0, 0, 0);

    public CmosDevice(int memorySize)
    {
        int baseKB = Math.Min(memorySize / 1024, 640);
        int extKB = Math.Clamp((memorySize - 0x100000) / 1024, 0, 15 * 1024);
        int ext64 = memorySize > 0x1000000 ? (memorySize - 0x1000000) / (64 * 1024) : 0;

        Data[0x15] = (byte)baseKB; Data[0x16] = (byte)(baseKB >> 8);
        Data[0x17] = (byte)extKB; Data[0x18] = (byte)(extKB >> 8);
        Data[0x30] = (byte)extKB; Data[0x31] = (byte)(extKB >> 8);
        Data[0x34] = (byte)ext64; Data[0x35] = (byte)(ext64 >> 8);
        Data[StatusA] = 0x26; // 32.768kHz、周期 1024Hz(標準の初期値)
        Data[StatusB] = 0x02; // BCD、24 時間制
        Data[StatusD] = 0x80; // 電池有効
        SetClock(Default, 0);
    }

    public void Select(byte value) => Index = value & 0x7F;

    public byte Read(ulong tsc) => Index switch
    {
        0 => Encode(Current(tsc).Second),
        2 => Encode(Current(tsc).Minute),
        4 => EncodeHour(Current(tsc).Hour),
        6 => Encode((int)Current(tsc).DayOfWeek + 1),
        7 => Encode(Current(tsc).Day),
        8 => Encode(Current(tsc).Month),
        9 => Encode(Current(tsc).Year % 100),
        Century => Encode(Current(tsc).Year / 100),
        StatusA => (byte)(Data[StatusA] & 0x7F), // UIP: 更新中になることはない
        StatusC => 0,                            // 割り込み要因なし
        StatusD => 0x80,
        _ => Data[Index],
    };

    public void Write(byte value, ulong tsc)
    {
        switch (Index)
        {
            case 0 or 2 or 4 or 7 or 8 or 9 or Century:
                var current = Current(tsc);
                var field = Index == 4 ? DecodeHour(value) : Decode(value);
                SetClock(Index switch
                {
                    0 => Replace(current, second: field),
                    2 => Replace(current, minute: field),
                    4 => Replace(current, hour: field),
                    7 => Replace(current, day: field),
                    8 => Replace(current, month: field),
                    9 => Replace(current, year: current.Year / 100 * 100 + field),
                    _ => Replace(current, year: field * 100 + current.Year % 100),
                }, tsc);
                break;
            case 6 or StatusC or StatusD: break;                  // 曜日は日付から、C/D は読み出し専用
            case StatusA: Data[StatusA] = (byte)(value & 0x7F); break;
            default: Data[Index] = value; break;
        }
    }

    // 時計を合わせる: time を「tsc の時点の時刻」として、Tsc = 0 の時刻を Data に保持する。
    public void SetClock(DateTime time, ulong tsc)
    {
        var at0 = time.AddSeconds(-(double)(tsc / InstructionsPerSecond));
        Data[0] = Bcd(at0.Second); Data[2] = Bcd(at0.Minute); Data[4] = Bcd(at0.Hour);
        Data[6] = Bcd((int)at0.DayOfWeek + 1);
        Data[7] = Bcd(at0.Day); Data[8] = Bcd(at0.Month); Data[9] = Bcd(at0.Year % 100); Data[Century] = Bcd(at0.Year / 100);
    }

    // 現在時刻 = Data の時刻(Tsc = 0)+ Tsc 秒。
    public DateTime Current(ulong tsc) => Base().AddSeconds(tsc / InstructionsPerSecond);

    DateTime Base() => Replace(Default, Bcd(Data[Century]) * 100 + Bcd(Data[9]), Bcd(Data[8]), Bcd(Data[7]), Bcd(Data[4]), Bcd(Data[2]), Bcd(Data[0]));

    // 範囲外の値(ゲストが変な値を書いた、BCD でない)は有効な範囲に丸める。
    static DateTime Replace(DateTime time, int? year = null, int? month = null, int? day = null, int? hour = null, int? minute = null, int? second = null)
    {
        var y = Math.Clamp(year ?? time.Year, 1, 9999);
        var mo = Math.Clamp(month ?? time.Month, 1, 12);
        var d = Math.Clamp(day ?? time.Day, 1, DateTime.DaysInMonth(y, mo));
        return new DateTime(y, mo, d, Math.Clamp(hour ?? time.Hour, 0, 23), Math.Clamp(minute ?? time.Minute, 0, 59), Math.Clamp(second ?? time.Second, 0, 59));
    }

    bool Binary => (Data[StatusB] & 0x04) != 0;
    bool TwentyFourHour => (Data[StatusB] & 0x02) != 0;

    static byte Bcd(int value) => (byte)(value / 10 % 10 << 4 | value % 10);
    static int Bcd(byte value) => (value >> 4) * 10 + (value & 0x0F);

    byte Encode(int value) => Binary ? (byte)value : Bcd(value);
    int Decode(byte value) => Binary ? value : Bcd(value);

    // 12 時間制: 1-12 に bit7 が PM。0 時は 12 AM。
    byte EncodeHour(int hour)
    {
        if (TwentyFourHour) return Encode(hour);
        var twelve = hour % 12 == 0 ? 12 : hour % 12;
        return (byte)(Encode(twelve) | (hour >= 12 ? 0x80 : 0));
    }

    int DecodeHour(byte value)
    {
        if (TwentyFourHour) return Decode(value);
        var hour = Decode((byte)(value & 0x7F)) % 12;
        return (value & 0x80) != 0 ? hour + 12 : hour;
    }
}
