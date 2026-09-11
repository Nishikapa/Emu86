using static Emu86.CPU;

namespace Emu86;

static partial class Program
{
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
            EmulationRunner.Run(options, env, cpu, count, trace,
                saveSnapshot: (savedCount, savedCpu, savedEnv) => SnapshotStore.Save(options.SnapshotPath, savedCount, savedCpu, savedEnv));
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
