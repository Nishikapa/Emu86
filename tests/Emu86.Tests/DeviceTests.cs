using Emu86;
using System.Security.Cryptography;
using static Emu86.Ext;

static class DeviceTests
{
    static int checks;

    static void Equal<T>(T expected, T actual, string label)
    {
        checks++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label}: expected {expected}, got {actual}");
    }

    static void PortSequences()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        Equal((byte)64, env.Cmos[0x15], "CMOS RAM size");
        EnvOutPort(env, 0x70, 0x95);
        Equal((byte)64, EnvInPort(env, 0x71), "CMOS NMI index mask");
        EnvOutPort(env, 0x71, 0xAB);
        Equal((byte)0xAB, env.Cmos[0x15], "CMOS write");
        foreach (var port in new[] { 0x20, 0xA0 })
        {
            EnvOutPort(env, port, 0x11);
            EnvOutPort(env, port + 1, 0x30);
            EnvOutPort(env, port + 1, 4);
            EnvOutPort(env, port + 1, 1);
            EnvOutPort(env, port + 1, 0x5A);
            Equal((byte)0x5A, EnvInPort(env, port + 1), "PIC mask");
            env.PicMasterIsr = env.PicSlaveIsr = 0xFF;
            EnvOutPort(env, port, 0x63);
            Equal((byte)0xF7, EnvInPort(env, port), "PIC specific EOI");
            EnvOutPort(env, port, 0x20);
            Equal((byte)0, EnvInPort(env, port), "PIC nonspecific EOI");
        }
        EnvOutPort(env, 0x43, 0xC0);
        Equal((byte)0x36, EnvInPort(env, 0x40), "PIT status first");
        Equal((byte)0xFF, EnvInPort(env, 0x40), "PIT latched low");
        Equal((byte)0xFF, EnvInPort(env, 0x40), "PIT latched high");
        Equal((ushort)0xFEFF, env.PitCounter, "PIT counter progress");
        EnvOutPort(env, 0x43, 0xB6);
        EnvOutPort(env, 0x42, 0x34);
        EnvOutPort(env, 0x42, 0x12);
        EnvOutPort(env, 0x61, 1);
        Equal((byte)0x11, EnvInPort(env, 0x61), "PIT wait 1");
        Equal((byte)0x01, EnvInPort(env, 0x61), "PIT wait 2");
        Equal((byte)0x31, EnvInPort(env, 0x61), "PIT OUT2");
        EnvOutPort(env, 0x61, 0);
        Equal(false, env.Pit2Armed, "PIT gate disabled");
        EnvOutPort(env, 0x64, 0x60);
        EnvOutPort(env, 0x60, 0x47);
        EnvOutPort(env, 0x64, 0x20);
        Equal((byte)0x15, EnvInPort(env, 0x64), "keyboard OBF");
        Equal((byte)0x47, EnvInPort(env, 0x60), "keyboard command byte");
        Equal((byte)0x14, EnvInPort(env, 0x64), "keyboard empty");
        EnvOutPort(env, 0x60, 0xF2);
        foreach (var value in new byte[] { 0xFA, 0xAB, 0x83, 0x83 })
            Equal(value, EnvInPort(env, 0x60), "keyboard ID and empty read");
        env.Pm1Sts = env.Gpe0Sts = 0xFFFF;
        EnvOutPortN(env, 0xB000, 4, 0x12340F80);
        Equal(0x1234F07Fu, EnvInPortN(env, 0xB000, 4), "PM W1C and enable");
        EnvOutPortN(env, 0xAFE0, 4, 0x5678F00F);
        Equal(0x56780FF0u, EnvInPortN(env, 0xAFE0, 4), "GPE W1C and enable");
        env.Tsc = 1_000_000;
        Equal(3579545u, EnvInPortN(env, 0xB008, 4), "PM timer");
        EnvOutPortN(env, 0x1FFFF, 2, 0x1234);
        Equal(0x1234u, EnvInPortN(env, 0xFFFF, 2), "wrapped fallback ports");
        Equal(0xFFFFFFFFu, EnvInPortN(env, 0x170, 4), "absent secondary IDE");
        using var other = new EmuEnvironment(memory: new byte[0x10000]);
        Equal((byte)0, other.PicMasterMask, "independent PIC state");
        Equal(0, other.KbdOut.Count, "independent keyboard state");
        Equal((ushort)0, other.Pm1En, "independent ACPI state");
    }

    static void Compatibility()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        using var diagnostics = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(diagnostics);
        try
        {
            env.PicLog = true;
            int[] ports = [0x20, 0x21, 0xA0, 0xA1, 0x40, 0x42, 0x43, 0x60, 0x61, 0x64, 0x70, 0x71, 0xB2,
                0xAFE0, 0xAFE1, 0xAFE2, 0xAFE3, 0xB000, 0xB001, 0xB002, 0xB003, 0xB004, 0xB005, 0xB008, 0xB100, 0xCF8, 0xCFC, 0xFFFF];
            for (var value = 0; value < 256; value++)
            {
                env.Tsc = (ulong)value * 123456789;
                env.PicMasterIsr = env.PicSlaveIsr = (byte)value;
                env.Pm1Sts = env.Gpe0Sts = (ushort)(value * 257);
                foreach (var port in ports)
                {
                    EnvOutPort(env, port, (byte)value);
                    writer.Write(EnvInPort(env, port));
                    foreach (var size in new[] { 1, 2, 4 })
                    {
                        EnvOutPortN(env, port, size, unchecked((uint)value * 0x01020304));
                        writer.Write(EnvInPortN(env, port, size));
                    }
                }
                env.SaveRuntimeState(writer);
                env.SaveDeviceState(writer);
            }
            env.SaveState(writer);
            writer.Write(diagnostics.ToString().Replace("\r\n", "\n"));
            writer.Flush();
            var hash = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
            Equal("C0102B8392C2C42742FFF4F69EAA0922E726A81344AD54130E7FD1E1D27018D2", hash, "device behavior (PCI VGA 2026-10-09, PS/2 mouse and 8042 A7/A8/FE 2026-10-10, RTC 2026-10-28)");
            Console.WriteLine($"Device compatibility SHA256={hash}");
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    // 8042 のコマンド表: 自己診断、コマンドバイトの読み書き、出力ポート読み、キーボードコマンドの応答、D4 経由のマウス、
    // 出力ポートとパルスによるリセット要求、表にないコマンドの無視。
    static void Controller8042()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        EnvOutPort(env, 0x64, 0xAA);
        Equal((byte)0x55, EnvInPort(env, 0x60), "self test");
        EnvOutPort(env, 0x64, 0x20);
        Equal((byte)0x45, EnvInPort(env, 0x60), "command byte read");
        EnvOutPort(env, 0x64, 0x60); EnvOutPort(env, 0x60, 0x47);
        Equal((byte)0x47, env.Keyboard.CommandByte, "command byte written through the argument");
        EnvOutPort(env, 0x64, 0xD0);
        Equal((byte)0x03, EnvInPort(env, 0x60), "output port read");
        EnvOutPort(env, 0x60, 0xF2);
        Equal("FA,AB,83", string.Join(",", Enumerable.Range(0, 3).Select(_ => EnvInPort(env, 0x60).ToString("X2"))), "keyboard ID reply");
        EnvOutPort(env, 0x60, 0xFF);
        Equal("FA,AA", string.Join(",", Enumerable.Range(0, 2).Select(_ => EnvInPort(env, 0x60).ToString("X2"))), "keyboard reset reply");
        EnvOutPort(env, 0x60, 0xED);
        Equal("FA", EnvInPort(env, 0x60).ToString("X2"), "other keyboard commands are acknowledged only");
        EnvOutPort(env, 0x64, 0xD4); EnvOutPort(env, 0x60, 0xF4);
        Equal((byte)0x21, (byte)(EnvInPort(env, 0x64) & 0x21), "mouse data waiting (OBF and AUX)");
        Equal((byte)0xFA, EnvInPort(env, 0x60), "mouse acknowledges through the AUX path");
        Equal(true, env.Keyboard.Mouse.Enabled, "mouse enabled by F4");
        EnvOutPort(env, 0x64, 0xAD);
        Equal((byte)0x14, (byte)(EnvInPort(env, 0x64) & 0x35), "an unknown controller command produces no output");
        EnvOutPort(env, 0x64, 0xD1); EnvOutPort(env, 0x60, 0xFF);
        Equal(false, env.Keyboard.ResetRequested, "output port write with bit0 set does not reset");
        EnvOutPort(env, 0x64, 0xFE);
        Equal(true, env.Keyboard.ResetRequested, "pulse with bit0 clear requests a reset");
    }

    // RTC: 時刻はゲストの時刻(Tsc、100 万命令 = 1 秒)で進み、BCD/二進と 24/12 時間制は状態 B に従う。
    // 時刻レジスタへの書き込みは「その時点の時刻」として合わせ、以後も Tsc で進む。曜日は日付から、C は 0、D は電池有効。
    static void RealTimeClock()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var start = new DateTime(2026, 10, 28, 12, 34, 56);
        env.CmosRtc.SetClock(start, 0);
        string Clock(ulong tsc, params int[] registers)
        {
            env.Tsc = tsc;
            return string.Join(",", registers.Select(register => { EnvOutPort(env, 0x70, (byte)register); return EnvInPort(env, 0x71).ToString("X2"); }));
        }
        void Write(int register, byte value) { EnvOutPort(env, 0x70, (byte)register); EnvOutPort(env, 0x71, value); }
        static string Weekday(DateTime date) => ((int)date.DayOfWeek + 1).ToString("X2");
        Equal($"56,34,12,{Weekday(start)},28,10,26,20", Clock(0, 0, 2, 4, 6, 7, 8, 9, 0x32), "BCD time, weekday, date and century");
        Equal("26,36,12", Clock(90_000_000, 0, 2, 4), "90 seconds of guest time later");
        var end = new DateTime(2099, 12, 31, 23, 59, 59);
        Equal($"59,59,23,{Weekday(end)},31,12,99,20", Clock((ulong)(end - start).TotalSeconds * 1_000_000, 0, 2, 4, 6, 7, 8, 9, 0x32), "end of the century");
        Equal("00,80,26", Clock(5_000_000, 0x0C, 0x0D, 0x0A), "status C clear, D battery valid, A default");
        Write(0x0B, 0x06); // 二進、24 時間制
        Equal("01,23,0C", Clock(5_000_000, 0, 2, 4), "binary encoding: 12:35:01 at +5 s");
        Write(0x0B, 0x04); // 二進、12 時間制
        Equal("8C", Clock(5_000_000, 4), "12-hour encoding: 12:35 is 12 PM");
        Equal("0C", Clock(5_000_000 + (11 * 3600 + 30 * 60) * 1_000_000UL, 4), "12-hour encoding: 00:05 is 12 AM");
        Equal("81", Clock(5_000_000 + 30 * 60 * 1_000_000UL, 4), "12-hour encoding: 13:05 is 1 PM");
        Write(0x0B, 0x02); // BCD、24 時間制に戻す
        env.Tsc = 5_000_000;
        Write(0, 0x30); // 5 秒の時点の秒を 30 に合わせる
        Equal("30,35,12", Clock(5_000_000, 0, 2, 4), "writing the seconds sets the clock at that moment");
        Equal("40,35,12", Clock(15_000_000, 0, 2, 4), "and it keeps running from there");
        Write(9, 0x30); Write(0x32, 0x19);
        Equal($"19,30,{Weekday(new DateTime(1930, 10, 28))}", Clock(15_000_000, 0x32, 9, 6), "writing the year and century; the weekday follows the date");
        Write(6, 0x07);
        Equal(Weekday(new DateTime(1930, 10, 28)), Clock(15_000_000, 6), "the weekday cannot be written");
        Write(8, 0xFF);
        Equal("12", Clock(15_000_000, 8), "an invalid month is clamped");
        Write(0x0A, 0xAB);
        Equal("2B", Clock(15_000_000, 0x0A), "status A keeps the rate bits; UIP reads as 0");
    }

    public static void Run()
    {
        PortSequences();
        Controller8042();
        RealTimeClock();
        Compatibility();
        Console.WriteLine($"Device assertions passed: {checks}");
    }
}
