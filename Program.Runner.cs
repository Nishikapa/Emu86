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
            using var env = EnvironmentFactory.Create(disk, attachDisk: !options.Resume);
            long count = 0;
            CPU cpu;
            if (options.Resume)
            {
                (count, cpu) = SnapshotStore.Load(options.SnapshotPath, env, disk);
                Console.WriteLine($"[snapshot] resumed from {options.SnapshotPath} at instruction {count}");
            }
            else
                cpu = _cs.setter(_ip.setter(new CPU())(0xFFF0))(0xF000);
            using var trace = options.Diagnostics.Trace
                ? new StreamWriter(new FileStream("trace.log", count > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.Read, 1 << 20))
                : null;
            void Save(long savedCount, CPU savedCpu, EmuEnvironment savedEnv) =>
                SnapshotStore.Save(options.SnapshotPath, savedCount, savedCpu, savedEnv);
            if (options.Window)
                RunWithWindow(options, env, cpu, count, trace, Save);
            else
                EmulationRunner.Run(options, env, cpu, count, trace, saveSnapshot: Save);
            return 0;
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

    // 表示ウィンドウを UI スレッド(このスレッド)で開き、エミュレーションは別スレッドで回す。
    // ウィンドウを閉じると実行ループへ停止を要求し、通常終了と同じくスナップショットを保存してから戻る。
    // 実行が先に終わった場合(命令数上限・停止診断)はウィンドウを残し、閉じられるまで最後の画面を表示する。
    static void RunWithWindow(RunOptions options, EmuEnvironment env, CPU cpu, long count, TextWriter trace,
        Action<long, CPU, EmuEnvironment> saveSnapshot)
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        using var stop = new CancellationTokenSource();
        var control = new EmulationControl();
        using var window = new VgaWindow(env, control);
        Exception failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                EmulationRunner.Run(options, env, cpu, count, trace, saveSnapshot: saveSnapshot, stop: stop.Token, control: control);
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
