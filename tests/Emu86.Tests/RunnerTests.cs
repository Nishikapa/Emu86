using Emu86;
using System.Reflection;
using System.Security.Cryptography;
using static Emu86.Ext;

static class RunnerTests
{
    static int assertions;
    static void Equal<T>(T expected, T actual, string name)
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{name}: expected {expected}, got {actual}");
    }
    static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { assertions++; return; }
        throw new Exception(name + ": expected " + typeof(T).Name);
    }

    static void Options()
    {
        var defaults = RunOptions.Parse([]);
        Equal(500_000_000L, defaults.Limit, "default limit");
        Equal("snapshot.snap", defaults.SnapshotPath, "default snapshot");
        Equal(true, defaults.Diagnostics.Trace, "default trace");
        Equal(false, defaults.Resume, "default cold boot");
        string[] arguments = ["--resume", "--slow", "--noirq", "--limit", "123", "--snapshot", "other.snap",
            "--disk", "other.vhd", "--overlay", "other.avhdx", "--dumplba", "42", "2",
            "--trace-all", "--regtrace", "--notrace", "--brhist", "--esptrap", "--pftrap",
            "--atalog", "--pcilog", "--piclog", "--intlog", "--upflog", "321",
            "--breakeip", "0x80000000", "--breakeax", "FFFFFFFF", "--breakat", "44", "--watchval", "0",
            "--entrylog", "10", "20", "--breakrange", "30", "40", "--wlog", "50", "60", "--watch", "70", "80"];
        var parsed = RunOptions.Parse(arguments);
        Equal(true, parsed.Resume && parsed.Slow && parsed.NoIrq, "run flags");
        Equal(123L, parsed.Limit, "limit");
        Equal("other.snap", parsed.SnapshotPath, "snapshot path");
        Equal("other.vhd", parsed.DiskPath, "base path");
        Equal("other.avhdx", parsed.OverlayPath, "overlay path");
        Equal((42L, 2), parsed.DumpLba.Value, "LBA decimal");
        Equal(true, parsed.Diagnostics.Trace && parsed.Diagnostics.RegTrace, "trace-all wins");
        Equal(true, parsed.Diagnostics.BranchHistory && parsed.Diagnostics.EspTrap && parsed.Diagnostics.PfTrap, "traps");
        Equal(true, parsed.Diagnostics.AtaLog && parsed.Diagnostics.PciLog && parsed.Diagnostics.PicLog && parsed.Diagnostics.IntLog, "device logs");
        Equal(321L, parsed.Diagnostics.UserPageFaultMin.Value, "optional minimum");
        Equal(0x80000000u, parsed.Diagnostics.BreakEip, "hex prefix");
        Equal(uint.MaxValue, parsed.Diagnostics.BreakEax.Value, "hex maximum");
        Equal(0u, parsed.Diagnostics.WatchValue.Value, "zero watch value");
        Equal(44L, parsed.Diagnostics.BreakAt, "break count");
        Equal(new AddressRange(0x10, 0x20), parsed.Diagnostics.EntryLog, "entry range");
        Equal(new AddressRange(0x30, 0x40), parsed.Diagnostics.BreakRange, "break range");
        Equal(new AddressRange(0x50, 0x60), parsed.Diagnostics.WriteLog.Value, "write range");
        Equal(new AddressRange(0x70, 0x80), parsed.Diagnostics.Watch, "watch range");
        Equal(0L, RunOptions.Parse(["--upflog", "--slow"]).Diagnostics.UserPageFaultMin.Value, "optional minimum omitted");
        Equal(false, RunOptions.Parse(["--notrace"]).Diagnostics.Trace, "trace disabled");
        Equal(true, RunOptions.Parse(["--help"]).Help, "help");
        string[][] invalid = [
            ["--bogus"], ["--limit"], ["--limit", "--slow"], ["--limit", "-1"], ["--limit", "abc"],
            ["--limit", "9223372036854775808"], ["--snapshot", ""], ["--breakeip", "100000000"],
            ["--breakeip", "-1"], ["--watch", "20"], ["--watch", "20", "10"], ["--watch", "10", "10"],
            ["--upflog", "oops"], ["--dumplba", "1"], ["--dumplba", "0", "0"],
            ["--dumplba", "9223372036854775807", "2"], ["--dumplba", "0", "2147483648"],
            ["--overlay", "overlay.avhdx"]
        ];
        foreach (var args in invalid) Throws<ArgumentException>(() => RunOptions.Parse(args), "invalid " + string.Join(' ', args));
    }

    static string StateHash(CPU cpu, EmuEnvironment env)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        cpu.WriteTo(writer); cpu.SaveRuntimeState(writer);
        env.SaveState(writer); env.SaveRuntimeState(writer); env.SaveDeviceState(writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    sealed record GoldenCase(string[] Args, long Start, long Count, string State, string Output, string Trace);
    static readonly GoldenCase[] GoldenCases = System.Text.Json.JsonSerializer.Deserialize<GoldenCase[]>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "runner-golden.json")));
    static int goldenIndex;

    static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.Replace("\r\n", "\n"))));

    static void Compare(string[] args, Action<CPU, EmuEnvironment> setup = null, long start = 0)
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = new CPU { eip = 0x100, code32 = true, stack32 = true, esp = 0x8000, eflags = 2 };
        byte[] code = [0x40, 0x83, 0xC3, 0x03, 0xEB, 0xFA];
        code.CopyTo(env.OneMegaMemory_, 0x100);
        setup?.Invoke(cpu, env);
        using var output = new StringWriter();
        using var trace = new StringWriter();
        var snapshots = new List<long>();
        var options = RunOptions.Parse(args);
        var count = EmulationRunner.Run(options, env, cpu, start, trace, output, TextWriter.Null,
            (savedCount, savedCpu, savedEnv) => snapshots.Add(savedCount));
        var expected = GoldenCases[goldenIndex++];
        Equal(string.Join(" ", expected.Args), string.Join(" ", args), "golden arguments");
        Equal(expected.Start, start, "golden starting count");
        Equal(expected.Count, count, "runner count");
        Equal(expected.State, StateHash(cpu, env), "runner full state");
        Equal(expected.Output, HashText(output.ToString()), "diagnostic output");
        Equal(expected.Trace, HashText(trace.ToString()), "trace output");
        Equal(count, snapshots.Last(), "final snapshot callback");
    }

    static void Runners()
    {
        Compare(["--limit", "25000"]);
        Compare(["--limit", "123", "--trace-all", "--regtrace", "--brhist"]);
        Compare(["--limit", "123", "--slow", "--trace-all", "--regtrace", "--brhist"]);
        Compare(["--limit", "99", "--notrace", "--noirq"], start: 12);
        Compare(["--limit", "99", "--breakat", "8", "--brhist", "--intlog", "--wlog", "100", "200"]);
        Compare(["--limit", "99", "--breakeip", "101", "--breakeax", "2"]);
        Compare(["--limit", "99", "--watchval", "2", "--brhist"]);
        Compare(["--limit", "99", "--entrylog", "100", "101", "--trace-all"], (cpu, env) => CPU._cr0.setter(cpu)(1));
        Compare(["--limit", "99", "--breakrange", "101", "104"], (cpu, env) => CPU._cr0.setter(cpu)(1));
        Compare(["--limit", "25000", "--brhist"], (cpu, env) =>
        {
            cpu.code32 = false; cpu.stack32 = false; cpu.eflags = 0x202;
            env.PicMasterMask = 0;
            BitConverter.GetBytes(0x500u).CopyTo(env.OneMegaMemory_, 8 * 4);
            env.OneMegaMemory_[0x500] = 0xCF;
        });
        Compare(["--limit", "99"], (cpu, env) => { env.OneMegaMemory_[0x100] = 0x0F; env.OneMegaMemory_[0x101] = 0xFF; });
        Compare(["--limit", "99", "--pftrap", "--upflog"], (cpu, env) =>
        {
            CPU._cr0.setter(cpu)(0x80010001);
            CPU._cr3.setter(cpu)(0x9000);
        });
        using var environment = new EmuEnvironment(memory: new byte[0x10000]);
        var invalidStack = new CPU { eip = 0x100, esp = 0x8000 };
        CPU._cr0.setter(invalidStack)(0x80010001);
        EnvSyncPaging(environment, invalidStack);
        using var output = new StringWriter();
        var diagnostics = new RunDiagnostics(RunOptions.Parse(["--breakeip", "100"]).Diagnostics, environment, invalidStack, output, null, 0);
        Equal(true, diagnostics.BeforeInstruction(0), "unmapped stack still stops");
        Equal(true, output.ToString().Contains("stack unreadable"), "unmapped diagnostic is contained");
    }

    static void Exclusive(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Equal(true, file.CanWrite, "handle released: " + path);
    }

    static void Environments()
    {
        File.WriteAllBytes("sample.vhd", new byte[8192]);
        File.WriteAllBytes("sample.vhdx", new byte[8192]);
        var memory = new byte[2 * 1024 * 1024];
        using (var env = new EmuEnvironment(memory, [1, 2, 3]))
        {
            Equal(true, ReferenceEquals(memory, env.OneMegaMemory_), "RAM injected without copying");
            Equal("1,2,3", string.Join(',', memory.Skip(0xFFFFD).Take(3)), "BIOS placement");
            Equal(1024, env.Cmos[0x17] | env.Cmos[0x18] << 8, "CMOS follows injected RAM");
            Equal(true, env.Ata == null, "constructor does not discover disk");
            Equal(false, File.Exists("sample.avhdx"), "constructor does not create overlay");
        }
        Throws<ArgumentException>(() => new EmuEnvironment(new byte[65536], [1]), "small RAM with BIOS rejected");
        Throws<ArgumentException>(() => new EmuEnvironment(new byte[0x100000], new byte[0x100001]), "oversized BIOS rejected");
        var discovered = DiskConfiguration.Discover(Directory.GetCurrentDirectory());
        Equal(Path.GetFullPath("sample.vhd"), discovered.BaseImagePath, "VHD discovery priority");
        Throws<ArgumentException>(() => new DiskConfiguration("sample.vhd", "./sample.vhd"), "base cannot be overlay");
        DiskImage.EnsureOverlay("sample.avhdx", "sample.vhd");
        var disk = discovered.OpenOverlay();
        var ata = new AtaDevice(disk);
        var environment = new EmuEnvironment(new byte[65536], ata: ata);
        Throws<IOException>(() => Exclusive("sample.avhdx"), "overlay held while environment alive");
        environment.Dispose(); environment.Dispose(); ata.Dispose(); disk.Dispose();
        Exclusive("sample.avhdx"); Exclusive("sample.vhd");
        using (var cold = EnvironmentFactory.Create(discovered)) Equal(true, cold.Ata != null, "factory attaches selected disk");
        Exclusive("sample.avhdx"); Exclusive("sample.vhd");
        using (var fresh = EnvironmentFactory.Create(discovered, attachDisk: false)) Equal(true, fresh.Ata == null, "resume factory stays diskless");
        var directory = Directory.GetCurrentDirectory();
        Directory.CreateDirectory("elsewhere");
        Directory.SetCurrentDirectory("elsewhere");
        try
        {
            Equal(Path.Combine(directory, "sample.vhd"), discovered.BaseImagePath, "configuration stable after cwd changes");
            using var selected = discovered.OpenOverlay();
            Equal(Path.Combine(directory, "sample.avhdx"), selected.FilePath, "open uses captured absolute path");
            Equal(true, DiskConfiguration.Discover(Directory.GetCurrentDirectory()) == null, "diskless discovery");
        }
        finally { Directory.SetCurrentDirectory(directory); }
        var originalBase = SHA256.HashData(File.ReadAllBytes("sample.vhd"));
        Program.DumpSectors(discovered.OverlayPath, 1, 2);
        Equal(1024L, new FileInfo("lba_dump.bin").Length, "sector dump length");
        Exclusive("sample.avhdx"); Exclusive("sample.vhd");
        Throws<ArgumentException>(() => Program.DumpSectors(discovered.OverlayPath, long.MaxValue, 1), "dump bounds");
        Exclusive("sample.avhdx");
        Throws<DirectoryNotFoundException>(() => DiskImage.EnsureOverlay(Path.Combine("missing", "disk.avhdx"), "sample.vhd"), "overlay creation failure");
        Exclusive("sample.vhd");
        Equal(true, originalBase.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes("sample.vhd"))), "base bytes unchanged");
        using var left = new EmuEnvironment(new byte[65536], ata: new AtaDevice(null));
        using var right = new EmuEnvironment(new byte[65536], ata: new AtaDevice(null));
        _ = new RunDiagnostics(RunOptions.Parse(["--atalog", "--pcilog", "--piclog"]).Diagnostics, left, new CPU(), TextWriter.Null, null, 0);
        _ = new RunDiagnostics(RunOptions.Parse([]).Diagnostics, right, new CPU(), TextWriter.Null, null, 0);
        Equal(true, left.Ata.Log && left.Pci.Log && left.PicLog, "diagnostic enabled on left");
        Equal(false, right.Ata.Log || right.Pci.Log || right.PicLog, "no global diagnostic leakage");
    }

    public static void Run()
    {
        Options(); Runners(); Environments(); CommandLine();
        Console.WriteLine($"Runner/environment checks passed: {assertions} assertions");
    }

    static void CommandLine()
    {
        int Run(params string[] args) => (int)typeof(Program).GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, [args]);
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            Equal(0, Run("--help"), "CLI help success");
            Equal(2, Run("--limit", "bad"), "CLI validation exit code");
            Equal(true, error.ToString().Contains("--limit"), "CLI identifies bad argument");
            Equal(false, File.Exists("snapshot.snap"), "invalid CLI does not create snapshot");
            using var env = new EmuEnvironment();
            env.OneMegaMemory_[0x100] = 0x40;
            SnapshotStore.Save("resume.snap", 10, new CPU { eip = 0x100, code32 = true, stack32 = true, esp = 0x8000 }, env);
            var resumeResult = Run("--resume", "--snapshot", "resume.snap", "--limit", "11", "--notrace");
            Equal(0, resumeResult, "CLI resume success: " + error);
            using var restored = new EmuEnvironment();
            var result = SnapshotStore.Load("resume.snap", restored);
            Equal(11L, result.count, "CLI saves final count");
            Equal(1u, result.cpu.eax, "CLI executes resumed CPU");
            File.WriteAllBytes("invalid.snap", new byte[64]);
            Equal(1, Run("--resume", "--snapshot", "invalid.snap", "--notrace"), "CLI invalid snapshot exit code");
            Exclusive("sample.avhdx"); Exclusive("sample.vhd");
            Equal(1, Run("--disk", "missing.vhd", "--overlay", "sample.avhdx", "--notrace"), "missing base exit code");
            Equal(false, File.Exists("sample.avhdx.old"), "missing base does not retire overlay");
        }
        finally { Console.SetOut(originalOutput); Console.SetError(originalError); }
    }

}
