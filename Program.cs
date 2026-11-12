namespace Emu86;

static class Program
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
            if (options.CreateDisk is { } created)
            {
                DiskImage.CreateVhdx(created.Path, created.Bytes);
                Console.WriteLine($"created {Path.GetFullPath(created.Path)}: empty dynamic VHDX, {created.Bytes >> 30} GB");
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
            if (options.MergeDiskPath is { } merged)
            {
                MergeDisk(disk ?? throw new ArgumentException("--merge-disk needs a disk image (--disk)"), merged);
                return 0;
            }
            using var session = new EmulationSession(options, disk);
            using var trace = options.Diagnostics.Trace
                ? new StreamWriter(new FileStream("trace.log", session.Count > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.Read, 1 << 20))
                : null;
            void Save(long savedCount, CPU savedCpu, EmuEnvironment savedEnv)
            {
                SnapshotStore.Save(options.SnapshotPath, savedCount, savedCpu, savedEnv);
                if (options.ScreenPath != null) ScreenCapture.Save(savedEnv.Vga, options.ScreenPath);
                if (options.VramDumpPath != null) savedEnv.Vga.Dump(options.VramDumpPath);
            }
            if (options.Window) RunWithWindow(session, trace, Save);
            else session.Run(trace, Save);
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
    static void RunWithWindow(EmulationSession session, TextWriter trace, Action<long, CPU, EmuEnvironment> saveSnapshot)
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        using var stop = new CancellationTokenSource();
        var control = new EmulationControl();
        using var window = new VgaWindow(session.Environment, control);
        Exception failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                session.Run(trace, saveSnapshot, stop.Token, control, window.Rebind);
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
        try { System.Windows.Forms.Application.Run(window); }
        finally
        {
            stop.Cancel();
            control.Resume();
            worker.Join();
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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

    // --merge-disk: ディスク(差分があればそれを重ねた中身)を、親を持たない新しい VHDX に書き出す。元のイメージと差分は変えない。
    internal static void MergeDisk(DiskConfiguration disk, string path)
    {
        using var source = new DiskImage(File.Exists(disk.OverlayPath) ? disk.OverlayPath : disk.BaseImagePath);
        var blocks = source.MergeInto(path);
        Console.WriteLine($"merged {source.FilePath} into {Path.GetFullPath(path)}: {blocks} blocks written, {new FileInfo(path).Length >> 20} MB");
    }
}
