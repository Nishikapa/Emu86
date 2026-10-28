using static Emu86.CPU;

namespace Emu86;

static partial class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var options = RunOptions.Parse(args);
            if (options.Help)
            {
                Console.WriteLine(RunOptions.Usage);
                return 0;
            }
            var disk = options.DiskPath != null
                ? new DiskConfiguration(options.DiskPath, options.OverlayPath)
                : DiskConfiguration.Discover(Directory.GetCurrentDirectory());
            if (options.DumpLba is { } dump)
            {
                DumpSectors(disk?.OverlayPath ?? "sample.avhdx", dump.Start, dump.Count);
                return 0;
            }
            var env = EnvironmentFactory.Create(disk, attachDisk: !options.Resume);
            try
            {
                AttachSerial(options, env);
                long count = 0;
                CPU cpu;
                if (options.Resume)
                {
                    (count, cpu) = SnapshotStore.Load(options.SnapshotPath, env, disk);
                    Console.WriteLine($"[snapshot] resumed from {options.SnapshotPath} at instruction {count}");
                }
                else
                {
                    cpu = ResetVector();
                    env.CmosRtc.SetClock(options.Rtc ?? DateTime.Now, 0); // 冷起動: RTC をホスト(または --rtc)の時刻に合わせる(再開はスナップショットの時刻が続く)
                }
                using var trace = options.Diagnostics.Trace
                    ? new StreamWriter(new FileStream("trace.log", count > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.Read, 1 << 20))
                    : null;
                void Save(long savedCount, CPU savedCpu, EmuEnvironment savedEnv) =>
                    SnapshotStore.Save(options.SnapshotPath, savedCount, savedCpu, savedEnv);
                env = options.Window
                    ? RunWithWindow(options, disk, env, cpu, count, trace, Save)
                    : RunUntilStopped(options, disk, env, cpu, count, trace, Save, default, null, null);
                return 0;
            }
            finally
            {
                env.Dispose();
            }
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Use --help for available options.");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    static CPU ResetVector() => _cs.setter(_ip.setter(new CPU())(0xFFF0))(0xF000);

    // --com1/--com2 で指定された名前付きパイプを UART につなぐ(環境を作り直すときは前の接続を引き継ぐ)。
    static void AttachSerial(RunOptions options, EmuEnvironment env, EmuEnvironment previous = null)
    {
        if (previous != null)
        {
            env.Com1.Host = previous.Com1.Host; previous.Com1.Host = null;
            env.Com2.Host = previous.Com2.Host; previous.Com2.Host = null;
            return;
        }
        if (options.Com1Pipe != null) env.Com1.Host = new NamedPipeSerialHost(options.Com1Pipe);
        if (options.Com2Pipe != null) env.Com2.Host = new NamedPipeSerialHost(options.Com2Pipe);
        if (options.Com1Pipe != null || options.Com2Pipe != null)
            Console.Error.WriteLine($"[serial] COM1={(options.Com1Pipe != null ? @"\\.\pipe\" + options.Com1Pipe : "none")} COM2={(options.Com2Pipe != null ? @"\\.\pipe\" + options.Com2Pipe : "none")}");
    }

    // 実行→(ゲストのシステムリセット)→環境を作り直してリセットベクタから再実行、を停止まで繰り返す。
    // ディスクの差分は同じオーバーレイに積まれているので、再起動後も内容は引き継がれる。
    // 戻り値は最後に使っていた環境(呼び出し側が破棄する)。
    static EmuEnvironment RunUntilStopped(RunOptions options, DiskConfiguration disk, EmuEnvironment env, CPU cpu, long count,
        TextWriter trace, Action<long, CPU, EmuEnvironment> save, CancellationToken stop, EmulationControl control,
        Action<EmuEnvironment> rebind)
    {
        var bootAutomation = options.BootDebug ? new BootAutomation() : null;
        while (true)
        {
            count = EmulationRunner.Run(options, env, cpu, count, trace, saveSnapshot: save,
                snapshotInterval: options.SnapshotInterval, stop: stop, control: control, bootAutomation: bootAutomation);
            if (!env.ResetRequested || stop.IsCancellationRequested || count >= options.Limit) return env;
            Console.Error.WriteLine($"[system] reset at instruction {count}");
            var font = env.Vga.FallbackFont;
            var hadDisk = env.Ata != null;
            var fresh = EnvironmentFactory.Create(disk, attachDisk: hadDisk);
            fresh.CmosRtc.SetClock(env.CmosRtc.Current((ulong)count), (ulong)count); // リセットをまたいでも時計は進み続ける
            AttachSerial(options, fresh, previous: env);
            env.Dispose();
            env = fresh;
            env.Vga.FallbackFont = font;
            if (options.BootDebug) bootAutomation = new BootAutomation();
            cpu = ResetVector();
            control?.Restart();
            rebind?.Invoke(env);
        }
    }

    // 表示ウィンドウを UI スレッド(このスレッド)で開き、エミュレーションは別スレッドで回す。
    // ウィンドウを閉じると実行ループへ停止を要求し、通常終了と同じくスナップショットを保存してから戻る。
    // 実行が先に終わった場合(命令数上限・停止診断)はウィンドウを残し、閉じられるまで最後の画面を表示する。
    static EmuEnvironment RunWithWindow(RunOptions options, DiskConfiguration disk, EmuEnvironment env, CPU cpu, long count,
        TextWriter trace, Action<long, CPU, EmuEnvironment> saveSnapshot)
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        using var stop = new CancellationTokenSource();
        var control = new EmulationControl();
        using var window = new VgaWindow(env, control);
        Exception failure = null;
        var current = env;
        var worker = new Thread(() =>
        {
            try
            {
                current = RunUntilStopped(options, disk, env, cpu, count, trace, saveSnapshot, stop.Token, control,
                    fresh => { current = fresh; window.Rebind(fresh); });
                window.EmulationStopped("stopped");
            }
            catch (Exception exception)
            {
                failure = exception;
                window.EmulationStopped("error: " + exception.Message);
            }
        }) { IsBackground = true, Name = "emulation" };
        window.FormClosing += (_, _) => { stop.Cancel(); control.Resume(); };
        worker.Start();
        System.Windows.Forms.Application.Run(window);
        stop.Cancel();
        control.Resume();
        worker.Join();
        if (failure != null) throw failure;
        return current;
    }

    internal static void DumpSectors(string path, long start, int count)
    {
        using var disk = new DiskImage(path);
        if (start < 0 || count <= 0 || start > disk.TotalSectors - count)
            throw new ArgumentException("Requested sectors are outside the disk");
        var buffer = new byte[DiskImage.SectorSize];
        using var output = new FileStream("lba_dump.bin", FileMode.Create, FileAccess.Write);
        for (var sector = 0; sector < count; sector++)
        {
            disk.ReadSector(start + sector, buffer, 0);
            output.Write(buffer);
        }
        Console.WriteLine($"dumped LBA {start}..{start + count - 1} to lba_dump.bin");
    }
}
