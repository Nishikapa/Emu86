using Emu86;
using System.Security.Cryptography;
using static Emu86.Conversions;
using static Emu86.Ports;
using static Assert;

static class DeviceTests
{
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
        // PIT: 1 ティック = 1 命令にして数えやすくする。未設定は下位→上位・モード 3(65536)で、前半は OUT=1。
        env.Clock.InstructionsPerSecond = GuestClock.PitHz;
        env.Tsc = 0;
        EnvOutPort(env, 0x43, 0xE2);                               // リードバック: チャネル 0 の状態をラッチ
        Equal((byte)0xB6, EnvInPort(env, 0x40), "PIT status: OUT=1, lo/hi, mode 3");
        EnvOutPort(env, 0x43, 0x34);                               // チャネル 0 下位→上位 モード 2
        EnvOutPort(env, 0x40, 0xE8); EnvOutPort(env, 0x40, 0x03);  // 1000
        env.Tsc = 250;
        EnvOutPort(env, 0x43, 0x00);                               // ラッチ
        Equal((byte)0xEE, EnvInPort(env, 0x40), "PIT latched low (1000 - 250 = 0x2EE)");
        Equal((byte)0x02, EnvInPort(env, 0x40), "PIT latched high");
        Equal(1000L, env.Pit.NextTimerIrq(250), "IRQ0 at the end of the first period");
        Equal(2000L, env.Pit.NextTimerIrq(1000), "IRQ0 every reload ticks");
        env.Tsc = 1000;
        EnvOutPort(env, 0x43, 0x30);                               // チャネル 0 モード 0(1 回だけ)
        EnvOutPort(env, 0x40, 0x64); EnvOutPort(env, 0x40, 0x00);  // 100
        Equal(1100L, env.Pit.NextTimerIrq(1000), "one-shot IRQ0 at the terminal count");
        Equal(long.MaxValue, env.Pit.NextTimerIrq(1100), "one-shot fires once");
        Equal(0xFFCE, env.Pit.Count(env.Pit.Channel0, 1150), "one-shot keeps counting through zero");
        env.Tsc = 2000;
        EnvOutPort(env, 0x43, 0xB0);                               // チャネル 2 下位→上位 モード 0
        EnvOutPort(env, 0x42, 0x0A); EnvOutPort(env, 0x42, 0x00);  // 10
        EnvOutPort(env, 0x61, 1);                                  // ゲートを開く
        env.Tsc = 2005;
        Equal((byte)0x11, EnvInPort(env, 0x61), "channel 2 still counting (refresh bit set)");
        env.Tsc = 2010;
        Equal((byte)0x31, EnvInPort(env, 0x61), "channel 2 OUT high at the terminal count");
        env.Tsc = 2016;
        Equal((byte)0x21, EnvInPort(env, 0x61), "refresh bit toggles every 18 ticks");
        EnvOutPort(env, 0x61, 0);
        Equal((byte)0x00, EnvInPort(env, 0x61) & 0x20, "gate closed: a one-shot channel 2 reads OUT low");
        env.Tsc = 0;
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
        env.Clock.InstructionsPerSecond = 1_000_000;
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
            env.PicMaster.Log = env.PicSlave.Log = true;
            env.Clock.InstructionsPerSecond = 1_000_000;
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
            Equal("1B05117A94F5B305B4D0138D7A3ADBEA4D2CB8A576C7DE877E3A0155F0EF903C", hash, "device behavior (PCI VGA 2026-10-09, PS/2 mouse and 8042 A7/A8/FE 2026-10-10, RTC 2026-10-28, PIT counting as programmed 2026-11-01, VGA BAR0 for the Bochs VBE framebuffer and 8042 reset flush 2026-11-01, CD-ROM presence byte and the VGA BIOS call port in the device state 2026-11-06)");
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
        env.Keyboard.Inject(0x1E, 0x9E); // 注入済みで未送信のキーはリセットで捨てられる(GRUB が読み残したキーが次の OS に届かない)
        EnvOutPort(env, 0x60, 0xFF);
        Equal("FA,AA", string.Join(",", Enumerable.Range(0, 2).Select(_ => EnvInPort(env, 0x60).ToString("X2"))), "keyboard reset reply");
        Equal(false, env.Keyboard.Pump(), "reset flushed the injected scancodes");
        env.Keyboard.Inject(0x1E); env.Keyboard.Pump();
        EnvOutPort(env, 0x64, 0xAA);
        Equal((byte)0x55, EnvInPort(env, 0x60), "self test clears the output buffer first");
        Equal((byte)0x14, (byte)(EnvInPort(env, 0x64) & 0x01 | 0x14), "nothing left after the self test reply");
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
    // 状態 A の UIP は秒が変わる直前の更新周期の間だけ立つ。
    static void RealTimeClock()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        env.Clock.InstructionsPerSecond = 1_000_000; // この検査は 100 万命令 = 1 秒で書いてある
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
        Equal("2B", Clock(15_000_000, 0x0A), "status A keeps the rate bits; UIP cannot be written");
        Equal("2B,AB,AB,2B", string.Join(",", new ulong[] { 15_997_771, 15_997_772, 15_999_999, 16_000_000 }.Select(tsc => Clock(tsc, 0x0A))),
            "UIP is up for the 2228 microseconds before the second changes");
        Equal("40,41", $"{Clock(15_999_999, 0)},{Clock(16_000_000, 0)}", "the second changes when the update cycle ends");
        Write(0x0B, 0x82);
        Equal("2B", Clock(15_999_999, 0x0A), "no update cycle while SET holds the clock");
        Write(0x0B, 0x02);
    }

    // ATAPI の CD-ROM(第 2 IDE チャネル): リセット後のシグネチャ、IDENTIFY、パケットコマンドの受け付けと
    // 上限ごとに区切った応答、エラーとセンス、割り込み(nIEN で止まり、ステータスの読み出しで降りる)、媒体なし。
    static void Atapi()
    {
        // 100 セクタの媒体: 各セクタは先頭 4 バイトがセクタ番号、残りは番号の下位バイト。
        const int sectors = 100;
        var image = new byte[sectors * CdMedia.SectorSize];
        for (var sector = 0; sector < sectors; sector++)
        {
            Array.Fill(image, (byte)sector, sector * CdMedia.SectorSize, CdMedia.SectorSize);
            BitConverter.GetBytes(sector).CopyTo(image, sector * CdMedia.SectorSize);
        }
        File.WriteAllBytes("atapi.iso", image);
        using var env = new EmuEnvironment(memory: new byte[0x10000], cdrom: new AtapiDevice(new CdMedia("atapi.iso")));
        var drive = env.Cdrom;
        byte Status() => EnvInPort(env, 0x177);
        byte Alternate() => EnvInPort(env, 0x376);
        int ByteCount() => EnvInPort(env, 0x174) | (EnvInPort(env, 0x175) << 8);
        // PACKET を出して 12 バイトのコマンドを書く(limit = 1 回の DRQ で受け取る上限)。
        void Packet(int limit, params byte[] command)
        {
            EnvOutPort(env, 0x176, 0xA0); EnvOutPort(env, 0x171, 0);
            EnvOutPort(env, 0x174, (byte)limit); EnvOutPort(env, 0x175, (byte)(limit >> 8));
            EnvOutPort(env, 0x177, 0xA0);
            Equal(((byte)0x58, (byte)0x01, false), (Alternate(), EnvInPort(env, 0x172), drive.IrqPending), "PACKET asks for the command without an interrupt");
            var padded = command.Concat(new byte[12]).Take(12).ToArray();
            for (var index = 0; index < 12; index += 2) EnvOutPortN(env, 0x170, 2, (uint)(padded[index] | padded[index + 1] << 8));
        }
        // 応答を DRQ の区切りごとに読み、(全データ、区切りの長さの列)を返す。
        (byte[] Data, List<int> Chunks) Receive()
        {
            var data = new List<byte>(); var chunks = new List<int>();
            while ((Alternate() & 0x08) != 0)
            {
                Equal(((byte)0x02, true), (EnvInPort(env, 0x172), drive.IrqPending), "a data chunk: reason = data in, interrupt raised");
                Status();
                var count = ByteCount();
                chunks.Add(count);
                for (var index = 0; index < count; index += 2)
                {
                    var word = EnvInPortN(env, 0x170, 2);
                    data.Add((byte)word);
                    if (index + 1 < count) data.Add((byte)(word >> 8));
                }
            }
            return ([.. data], chunks);
        }

        Equal("01,01,14,eb", string.Join(",", new[] { 0x172, 0x173, 0x174, 0x175 }.Select(port => EnvInPort(env, port).ToString("x2"))), "ATAPI signature at power-on");
        EnvOutPort(env, 0x376, 0x04); EnvOutPort(env, 0x376, 0x00);
        Equal(((byte)0x00, (byte)0x14, (byte)0xEB), (Status(), EnvInPort(env, 0x174), EnvInPort(env, 0x175)), "software reset: signature, DRDY not set");
        EnvOutPort(env, 0x176, 0xB0);
        Equal(((byte)0xB0, (byte)0x00), (EnvInPort(env, 0x176), Status()), "the absent slave reads status 0");
        EnvOutPort(env, 0x176, 0xA0);
        EnvOutPort(env, 0x177, 0xEC);
        Equal(((byte)0x41, (byte)0x04, (byte)0x14, (byte)0xEB), (Status(), EnvInPort(env, 0x171), EnvInPort(env, 0x174), EnvInPort(env, 0x175)), "IDENTIFY DEVICE aborts and leaves the signature");
        EnvOutPort(env, 0x177, 0xA1);
        Equal((byte)0x58, Alternate(), "IDENTIFY PACKET DEVICE has data");
        var identify = Enumerable.Range(0, 256).Select(_ => (ushort)EnvInPortN(env, 0x170, 2)).ToArray();
        Equal(((ushort)0x85C0, (ushort)0x0200, (byte)0x50), (identify[0], identify[49], Status()), "identify: ATAPI CD-ROM with 12-byte packets, LBA without DMA; then idle");
        Equal("Emu86 Virtual DVD-ROM", string.Concat(identify.Skip(27).Take(20).SelectMany(word => new[] { (char)(word >> 8), (char)(word & 0xFF) })).TrimEnd(), "identify: model");

        Packet(0, 0x00);
        Equal(((byte)0x50, (byte)0x03, true), (Alternate(), EnvInPort(env, 0x172), drive.IrqPending), "TEST UNIT READY completes: status phase with an interrupt");
        Status();
        Equal(false, drive.IrqPending, "reading the status drops the interrupt");

        Packet(36, 0x12, 0, 0, 0, 36);
        var inquiry = Receive();
        Equal((36, (byte)0x05, (byte)0x80, "EMU86   Virtual DVD-ROM 1.0 "), (inquiry.Data.Length, inquiry.Data[0], inquiry.Data[1], System.Text.Encoding.ASCII.GetString(inquiry.Data, 8, 28)), "INQUIRY: removable CD/DVD device");
        Equal(((byte)0x50, (byte)0x03, true), (Alternate(), EnvInPort(env, 0x172), drive.IrqPending), "the status phase follows the data");
        Packet(36, 0x12, 0, 0, 0, 5);
        Equal(5, Receive().Data.Length, "the allocation length caps the reply");

        Packet(8, 0x25);
        Equal("00000063,00000800", string.Join(",", Receive().Data.Chunk(4).Select(Convert.ToHexString)), "READ CAPACITY: last sector 99, 2048-byte sectors");

        Packet(2048, 0x28, 0, 0, 0, 0, 3, 0, 0, 2);
        var read = Receive();
        Equal(("2048,2048", 3, 4, (byte)3, (byte)4), (string.Join(",", read.Chunks), BitConverter.ToInt32(read.Data, 0), BitConverter.ToInt32(read.Data, 2048), read.Data[2047], read.Data[4095]), "READ(10) of two sectors arrives one sector per DRQ");
        Packet(0xFFFF, 0xA8, 0, 0, 0, 0, 60, 0, 0, 0, 40);
        read = Receive();
        Equal(("65534,16386", 60, 99), (string.Join(",", read.Chunks), BitConverter.ToInt32(read.Data, 0), BitConverter.ToInt32(read.Data, 39 * 2048)), "READ(12) of 40 sectors is split at the largest even byte count");

        Packet(2048, 0x28, 0, 0, 0, 0, 99, 0, 0, 2);
        Equal(((byte)0x41, (byte)0x50, (byte)0x03, true), (Alternate(), EnvInPort(env, 0x171), EnvInPort(env, 0x172), drive.IrqPending), "a read past the end is a CHECK CONDITION (ILLEGAL REQUEST)");
        Packet(18, 0x03, 0, 0, 0, 18);
        var sense = Receive().Data;
        Equal(((byte)0x70, (byte)0x05, (byte)0x21), (sense[0], sense[2], sense[12]), "REQUEST SENSE: illegal request, LBA out of range");
        Packet(18, 0x03, 0, 0, 0, 18);
        Equal((byte)0x00, Receive().Data[2], "the sense is cleared once it has been read");
        Packet(0, 0xFF);
        Packet(18, 0x03, 0, 0, 0, 18);
        sense = Receive().Data;
        Equal(((byte)0x05, (byte)0x20), (sense[2], sense[12]), "an unknown command: illegal request, invalid command");

        Packet(64, 0x43, 0, 0, 0, 0, 0, 0, 0, 64);
        var toc = Receive().Data;
        Equal((20, (byte)1, (byte)1, (byte)0xAA, 100u), (toc.Length, toc[2], toc[3], toc[14], (uint)(toc[16] << 24 | toc[17] << 16 | toc[18] << 8 | toc[19])), "READ TOC: one data track and the lead-out at the end of the medium");
        Packet(64, 0x46, 0, 0, 0, 0, 0, 0, 0, 64);
        var configuration = Receive().Data;
        Equal((40, (byte)0x00, (byte)0x08), (configuration.Length, configuration[6], configuration[7]), "GET CONFIGURATION: three features, current profile CD-ROM for a small medium");
        Packet(64, 0x46, 2, 0, 3, 0, 0, 0, 0, 64);
        Equal(16, Receive().Data.Length, "GET CONFIGURATION with RT=2 returns the one requested feature");
        Packet(64, 0x5A, 0, 0x2A, 0, 0, 0, 0, 0, 64);
        var mode = Receive().Data;
        Equal((30, (byte)28, (byte)0x2A, (byte)20), (mode.Length, mode[1], mode[8], mode[9]), "MODE SENSE(10): capabilities page");
        Packet(64, 0x5A, 0, 0x3F, 0, 0, 0, 0, 0, 64);
        Equal((byte)0x41, Status(), "MODE SENSE for an unsupported page fails");
        Packet(8, 0x4A, 1, 0, 0, 0x10, 0, 0, 0, 8);
        Equal("0004041000020000", Convert.ToHexString(Receive().Data), "GET EVENT STATUS NOTIFICATION: media class, no change, medium present");
        Packet(8, 0xAD, 0, 0, 0, 0, 0, 0, 0, 0, 8);
        Equal((byte)0x41, Status(), "READ DVD STRUCTURE is refused for a CD-sized medium");

        EnvOutPort(env, 0x376, 0x02); // nIEN
        Packet(0, 0x00);
        Equal(((byte)0x50, false), (Alternate(), drive.IrqPending), "with nIEN set the interrupt stays off the line");
        EnvOutPort(env, 0x376, 0x00);
        Equal(true, drive.IrqPending, "and shows once nIEN is cleared");
        EnvOutPort(env, 0x177, 0x08);
        Equal(((byte)0x00, false), (Status(), drive.IrqPending), "DEVICE RESET: signature state, no interrupt");

        using var empty = new EmuEnvironment(memory: new byte[0x10000], cdrom: new AtapiDevice(null));
        EnvOutPort(empty, 0x176, 0xA0); EnvOutPort(empty, 0x177, 0xA0);
        for (var index = 0; index < 6; index++) EnvOutPortN(empty, 0x170, 2, 0);
        Equal(((byte)0x41, (byte)0x20), (EnvInPort(empty, 0x177), EnvInPort(empty, 0x171)), "without a medium TEST UNIT READY reports NOT READY");
        using var none = new EmuEnvironment(memory: new byte[0x10000]);
        Equal((0xFFFFu, (byte)0x00), (EnvInPortN(none, 0x170, 2), EnvInPort(none, 0x177)), "without a drive the channel floats for wide reads and falls through to the port array for bytes");
    }

    // ACPI テーブルの手直し: RSDT と XSDT から MADT("APIC")への項だけを取り除き、長さとチェックサムを直す。
    // ほかの表への項は順序どおり残る。MADT が無ければ何もしない。
    static void AcpiMadt()
    {
        using var env = new EmuEnvironment(memory: new byte[0x100000]);
        var ram = env.Ram;
        void Checksum(int address, int length, int at)
        {
            ram[address + at] = 0;
            ram[address + at] = (byte)-Enumerable.Range(0, length).Sum(index => ram[address + index]);
        }
        int Sum(int address, int length) => Enumerable.Range(0, length).Sum(index => ram[address + index]) & 0xFF;
        void Table(int address, string signature, int length)
        {
            System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(ram, address);
            BitConverter.GetBytes(length).CopyTo(ram, address + 4);
            Checksum(address, length, 9);
        }
        Equal(false, AcpiTables.HideMadt(env), "no RSDP, nothing to do");
        int[] tables = [0x81000, 0x82000, 0x83000];
        Table(0x81000, "FACP", 116); Table(0x82000, "APIC", 80); Table(0x83000, "HPET", 56);
        // RSDT(32 ビットの項)と XSDT(64 ビットの項)。
        for (var index = 0; index < 3; index++)
        {
            BitConverter.GetBytes(tables[index]).CopyTo(ram, 0x80000 + 36 + 4 * index);
            BitConverter.GetBytes((long)tables[index]).CopyTo(ram, 0x84000 + 36 + 8 * index);
        }
        Table(0x80000, "RSDT", 36 + 12); Table(0x84000, "XSDT", 36 + 24);
        // RSDP(版 2): RSDT は +16、XSDT は +24。先頭 20 バイトの和が 0。
        "RSD PTR "u8.CopyTo(ram.AsSpan(0xF1000));
        ram[0xF1000 + 15] = 2;
        BitConverter.GetBytes(0x80000).CopyTo(ram, 0xF1000 + 16);
        BitConverter.GetBytes(0x84000L).CopyTo(ram, 0xF1000 + 24);
        Checksum(0xF1000, 20, 8);
        Equal(true, AcpiTables.HideMadt(env), "the MADT entries are removed");
        Equal((36 + 8, 0x81000, 0x83000, 0), (BitConverter.ToInt32(ram, 0x80004), BitConverter.ToInt32(ram, 0x80024), BitConverter.ToInt32(ram, 0x80028), BitConverter.ToInt32(ram, 0x8002C)), "RSDT: two entries left in order, the freed slot cleared");
        Equal((36 + 16, 0x81000L, 0x83000L), (BitConverter.ToInt32(ram, 0x84004), BitConverter.ToInt64(ram, 0x84024), BitConverter.ToInt64(ram, 0x8402C)), "XSDT: two entries left in order");
        Equal((0, 0), (Sum(0x80000, 36 + 8), Sum(0x84000, 36 + 16)), "both checksums are valid again");
        Equal("APIC", System.Text.Encoding.ASCII.GetString(ram, 0x82000, 4), "the MADT itself is left in place (only unreferenced)");
        Equal(false, AcpiTables.HideMadt(env), "a second call finds nothing to remove");
    }

    // ACPI の電源断: PM1_CNT に SLP_EN と S5 の SLP_TYP(同梱の DSDT では 0)を書くと要求が立ち、ランナーが実行を終える。
    // S3 の要求は記録するだけで実行は続く。SLP_EN は読み戻せない。
    static void AcpiPowerOff()
    {
        var previousError = Console.Error;
        foreach (var (name, type, off) in new[] { ("S5", 0, true), ("S3", 1, false) })
        {
            using var env = new EmuEnvironment(memory: new byte[0x10000]);
            var value = 0x2000 | type << 10;
            byte[] code = [0x66, 0xB8, (byte)value, (byte)(value >> 8), 0x66, 0xBA, 0x04, 0xB0, 0x66, 0xEF, 0xEB, 0xFE]; // mov ax,value; mov dx,B004h; out dx,ax; jmp $
            code.CopyTo(env.Ram, 0x100);
            using var output = new StringWriter();
            using var diagnostics = new StringWriter();
            Console.SetError(diagnostics);
            long count;
            try
            {
                count = EmulationRunner.Run(RunOptions.Parse(["--limit", "30000", "--notrace", "--noirq"]), env,
                    new CPU { eip = 0x100, esp = 0x8000, eflags = 2, code32 = true, stack32 = true }, 0, null, output, TextWriter.Null);
            }
            finally { Console.SetError(previousError); }
            Equal(off, env.Acpi.PowerOffRequested, "power off flagged: " + name);
            Equal(off, count < 30000, "run left early: " + name);
            Equal(off, output.ToString().Contains("[system] power off requested"), "power off reported: " + name);
            Equal(true, diagnostics.ToString().Contains($"[acpi] sleep requested: SLP_TYP={type}"), "sleep request logged: " + name);
            Equal((ushort)(type << 10), env.Pm1Cnt, "SLP_TYP is kept and SLP_EN reads back as 0: " + name);
        }
    }

    // 名前付きのケース(ランナーが順に実行し、失敗したケース名を報告する)。
    public static (string Name, Action Run)[] Cases =>
    [
        ("port-sequences", PortSequences),
        ("controller-8042", Controller8042),
        ("rtc", RealTimeClock),
        ("atapi", Atapi),
        ("acpi-madt", AcpiMadt),
        ("acpi-power-off", AcpiPowerOff),
        ("compatibility", Compatibility),
    ];
}
