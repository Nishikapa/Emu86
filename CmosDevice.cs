namespace Emu86;

// CMOS RAM(128 バイト)と RTC(MC146818 互換)。
// 時計はゲストの時刻(命令数。GuestClock が秒に換算する。ランナーが Tsc に入れる)で進む。Data の時刻レジスタ(0/2/4/7/8/9/0x32)は
// 「Tsc = 0 のときの時刻」を常に BCD・24 時間制で保持し(電池で動く時計の初期値に相当)、読むときに Tsc 秒を足して
// 状態 B の形式(BCD/二進、24/12 時間)で返し、書くときは Tsc 秒を引いて戻す。状態は CMOS RAM だけで持てるので、
// スナップショットの再開でも時刻は連続する。曜日(6)は日付から求める。状態 A の UIP(更新中)は秒が変わる直前に立つ
// (Windows 7 の HAL は UIP の立ち上がりから次の立ち上がりまでの TSC を数えて CPU の速さを測る)。
// アラーム・周期割り込み(IRQ8)は未実装。
internal sealed class CmosDevice(GuestClock clock)
{
    public byte[] Data = new byte[128];
    public int Index;

    const int StatusA = 0x0A, StatusB = 0x0B, StatusC = 0x0C, StatusD = 0x0D, Century = 0x32;
    // 更新周期: 予告の 244 マイクロ秒と更新の 1984 マイクロ秒。この間だけ UIP が立ち、終わりに秒が進む。
    const ulong UpdateMicroseconds = 244 + 1984, Microsecond = 1_000_000;
    static readonly DateTime Default = new(2000, 1, 1, 0, 0, 0);

    // 時刻レジスタの表(Lens): レジスタ番号と、DateTime から項目を読む/項目を置き換える関数。
    // 読み出し(Read)・書き込み(Write)・Data との相互変換(SetClock/Base)はすべてこの表から導く。
    // Base が項目を順に当てはめるので、日の丸め(月の日数)が効くよう年・月・日の順に並べる。
    readonly record struct ClockField(int Index, Func<DateTime, int> Get, Func<DateTime, int, DateTime> Set);

    static readonly ClockField[] ClockFields =
    [
        new(Century, time => time.Year / 100, (time, value) => Replace(time, year: value * 100 + time.Year % 100)),
        new(9, time => time.Year % 100, (time, value) => Replace(time, year: time.Year / 100 * 100 + value)),
        new(8, time => time.Month, (time, value) => Replace(time, month: value)),
        new(7, time => time.Day, (time, value) => Replace(time, day: value)),
        new(4, time => time.Hour, (time, value) => Replace(time, hour: value)),
        new(2, time => time.Minute, (time, value) => Replace(time, minute: value)),
        new(0, time => time.Second, (time, value) => Replace(time, second: value)),
    ];

    static ClockField? FieldAt(int index) => ClockFields.FirstOrDefault(field => field.Index == index) is { Get: not null } field ? field : null;

    public CmosDevice(int memorySize, GuestClock clock) : this(clock)
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

    public byte Read(ulong tsc) =>
        FieldAt(Index) is { } field ? EncodeField(field.Index, field.Get(Current(tsc)))
        : Index switch
        {
            6 => Encode((int)Current(tsc).DayOfWeek + 1),
            StatusA => (byte)(Data[StatusA] & 0x7F | (Updating(tsc) ? 0x80 : 0)),
            StatusC => (byte)0,                      // 割り込み要因なし
            StatusD => (byte)0x80,
            _ => Data[Index],
        };

    // UIP: 秒が変わる直前の更新周期の間。SET(状態 B の bit7)で更新を止めている間は立たない。
    bool Updating(ulong tsc) =>
        (Data[StatusB] & 0x80) == 0 && clock.Ticks(tsc, (long)Microsecond) % Microsecond >= Microsecond - UpdateMicroseconds;

    public void Write(byte value, ulong tsc)
    {
        if (FieldAt(Index) is { } field) SetClock(field.Set(Current(tsc), DecodeField(Index, value)), tsc);
        else if (Index is 6 or StatusC or StatusD) { }                  // 曜日は日付から、C/D は読み出し専用
        else if (Index == StatusA) Data[StatusA] = (byte)(value & 0x7F);
        else Data[Index] = value;
    }

    // 時計を合わせる: time を「tsc の時点の時刻」として、Tsc = 0 の時刻を Data に保持する。
    public void SetClock(DateTime time, ulong tsc)
    {
        var at0 = time.AddSeconds(-(double)clock.Seconds(tsc));
        foreach (var field in ClockFields) Data[field.Index] = Bcd(field.Get(at0));
        Data[6] = Bcd((int)at0.DayOfWeek + 1);
    }

    // 現在時刻 = Data の時刻(Tsc = 0)+ Tsc 秒。
    public DateTime Current(ulong tsc) => Base().AddSeconds(clock.Seconds(tsc));

    DateTime Base() => ClockFields.Aggregate(Default, (time, field) => field.Set(time, Bcd(Data[field.Index])));

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

    // 時(レジスタ 4)だけは 12 時間制の形式がある。
    byte EncodeField(int index, int value) => index == 4 ? EncodeHour(value) : Encode(value);
    int DecodeField(int index, byte value) => index == 4 ? DecodeHour(value) : Decode(value);

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
