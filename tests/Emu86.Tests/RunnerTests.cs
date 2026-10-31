using Emu86;
using System.Reflection;
using System.Security.Cryptography;
using static Emu86.Conversions;
using static Emu86.Mmu;
using static Assert;

static class RunnerTests
{
    static void Options()
    {
        var defaults = RunOptions.Parse([]);
        Equal(500_000_000L, defaults.Limit, "default limit");
        Equal("snapshot.snap", defaults.SnapshotPath, "default snapshot");
        Equal(true, defaults.Diagnostics.Trace, "default trace");
        Equal(false, defaults.Resume, "default cold boot");
        string[] arguments = ["--resume", "--slow", "--noirq", "--no-translate", "--window", "--boot-debug", "--com1", "pipe-a", "--com2", "pipe-b", "--limit", "123", "--snapshot", "other.snap", "--rtc", "2026-10-28T12:34:56", "--screen", "shot.png",
            "--disk", "other.vhd", "--overlay", "other.avhdx", "--dumplba", "42", "2",
            "--trace-all", "--regtrace", "--notrace", "--brhist", "--esptrap", "--pftrap", "--dumpvram", "vram.bin",
            "--atalog", "--pcilog", "--piclog", "--vgalog", "--intlog", "--upflog", "321",
            "--breakeip", "0x80000000", "--breakeax", "FFFFFFFF", "--breakat", "44", "--watchval", "0",
            "--entrylog", "10", "20", "--breakrange", "30", "40", "--wlog", "50", "60", "--watch", "70", "80"];
        var parsed = RunOptions.Parse(arguments);
        Equal(true, parsed.Resume && parsed.Slow && parsed.NoIrq && parsed.Window && !parsed.Translate, "run flags");
        Equal(true, defaults.Translate, "translation is on by default");
        Equal(false, defaults.Window, "default headless");
        Equal(true, parsed.BootDebug && parsed.Com1Pipe == "pipe-a" && parsed.Com2Pipe == "pipe-b", "serial and boot-debug options");
        Equal(true, defaults.Com1Pipe == null && defaults.Com2Pipe == null && !defaults.BootDebug, "serial defaults");
        Equal(123L, parsed.Limit, "limit");
        Equal("other.snap", parsed.SnapshotPath, "snapshot path");
        Equal(new DateTime(2026, 10, 28, 12, 34, 56), parsed.Rtc, "real-time clock start");
        Equal("shot.png", parsed.ScreenPath, "screen capture path");
        Equal("vram.bin", parsed.VramDumpPath, "VRAM dump path");
        Equal(null, defaults.ScreenPath, "no screen capture by default");
        Equal(null, defaults.Rtc, "the clock defaults to the host's time");
        Equal("other.vhd", parsed.DiskPath, "base path");
        Equal("other.avhdx", parsed.OverlayPath, "overlay path");
        Equal((42L, 2), parsed.DumpLba.Value, "LBA decimal");
        Equal(true, parsed.Diagnostics.Trace && parsed.Diagnostics.RegTrace, "trace-all wins");
        Equal(true, parsed.Diagnostics.BranchHistory && parsed.Diagnostics.EspTrap && parsed.Diagnostics.PfTrap, "traps");
        Equal(true, parsed.Diagnostics.AtaLog && parsed.Diagnostics.PciLog && parsed.Diagnostics.PicLog && parsed.Diagnostics.VgaLog && parsed.Diagnostics.IntLog, "device logs");
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
        Equal(4, RunOptions.Parse(["--intlog", "--slow"]).Diagnostics.IntLogLast, "default interrupt log tail");
        Equal(60, RunOptions.Parse(["--intlog", "60"]).Diagnostics.IntLogLast, "interrupt log tail");
        Equal(false, RunOptions.Parse(["--notrace"]).Diagnostics.Trace, "trace disabled");
        Equal(true, RunOptions.Parse(["--help"]).Help, "help");
        string[][] invalid = [
            ["--bogus"], ["--limit"], ["--limit", "--slow"], ["--limit", "-1"], ["--limit", "abc"],
            ["--limit", "9223372036854775808"], ["--snapshot", ""], ["--breakeip", "100000000"], ["--rtc", "2026/10/28"],
            ["--breakeip", "-1"], ["--watch", "20"], ["--watch", "20", "10"], ["--watch", "10", "10"],
            ["--upflog", "oops"], ["--intlog", "0"], ["--intlog", "oops"], ["--com1"], ["--com2", "--slow"], ["--dumplba", "1"], ["--dumplba", "0", "0"],
            ["--dumplba", "9223372036854775807", "2"], ["--dumplba", "0", "2147483648"],
            ["--overlay", "overlay.avhdx"]
        ];
        foreach (var args in invalid) Throws<ArgumentException>(() => RunOptions.Parse(args), "invalid " + string.Join(' ', args));
        string Message(params string[] args)
        {
            try { RunOptions.Parse(args); return ""; }
            catch (ArgumentException exception) { return exception.Message; }
        }
        Equal("--limit: missing value", Message("--limit"), "missing value names the option");
        Equal("--limit: missing value", Message("--limit", "--slow"), "an option is not a value");
        Equal("--limit: expected a non-negative decimal integer", Message("--limit", "abc"), "decimal error");
        Equal("--breakeip: expected a 32-bit hexadecimal address", Message("--breakeip", "-1"), "hex error");
        Equal("--watch: expected lo < hi", Message("--watch", "20", "10"), "range order error");
        Equal("--dumplba: invalid sector range", Message("--dumplba", "0", "0"), "sector range error");
        Equal("--intlog: expected 1..100000", Message("--intlog", "0"), "interrupt log tail error");
        Equal("Unknown option: --bogus", Message("--bogus"), "unknown option");
        Equal("Unknown option: stray", Message("--slow", "stray"), "stray argument after valid options");
        Equal("--overlay requires --disk", Message("--overlay", "x.avhdx"), "post-parse validation");
        Equal(9_000_000L, RunOptions.Parse(["--limit", "1", "--limit", "9000000"]).Limit, "later option wins");
        Equal(long.MaxValue, RunOptions.Parse(["--limit", "max"]).Limit, "limit max");
        Equal(long.MaxValue, RunOptions.Parse(["--snapshot-interval", "0"]).SnapshotInterval, "interval 0 means at the end");
        // 使い方は同じ表から生成されるので、すべてのオプションが載っている。
        foreach (var name in arguments.Where(argument => argument.StartsWith("--")).Append("--help").Append("--snapshot-interval"))
            Equal(true, RunOptions.Usage.Contains("  " + name + " ") || RunOptions.Usage.Contains("  " + name + "\n") || RunOptions.Usage.Contains("  " + name + "\r"), "usage lists " + name);
        Equal(true, RunOptions.Usage.StartsWith("Usage: Emu86") && RunOptions.Usage.Contains("--dumplba start count"), "usage shows argument syntax");
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

    // plainGolden: 同じ golden ケースを --no-translate(1 命令ずつ)でも走らせ、既定の翻訳ブロックと同じ命令数・状態・診断・
    // トレースになることを確かめる(翻訳ブロックの予算が割り込み周期・上限・保存周期と一致し、分岐トレースが連結の途中も含めて
    // 再現できること)。golden の期待値は 1 命令ずつの実行で採取したもの。
    static bool plainGolden;

    static void Compare(string[] args, Action<CPU, EmuEnvironment> setup = null, long start = 0)
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = new CPU { eip = 0x100, code32 = true, stack32 = true, esp = 0x8000, eflags = 2 };
        byte[] code = [0x40, 0x83, 0xC3, 0x03, 0xEB, 0xFA];
        code.CopyTo(env.Ram, 0x100);
        setup?.Invoke(cpu, env);
        using var output = new StringWriter();
        using var trace = new StringWriter();
        var snapshots = new List<long>();
        var options = RunOptions.Parse(plainGolden ? [.. args, "--no-translate"] : args);
        var count = EmulationRunner.Run(options, env, cpu, start, trace, output, TextWriter.Null,
            (savedCount, savedCpu, savedEnv) => snapshots.Add(savedCount));
        var expected = GoldenCases[goldenIndex++];
        Equal(string.Join(" ", expected.Args), string.Join(" ", args), "golden arguments");
        Equal(expected.Start, start, "golden starting count");
        var label = $"{string.Join(" ", args)}{(plainGolden ? " --no-translate" : "")}";
        Equal(expected.Count, count, $"runner count for {label}");
        Equal(expected.State, StateHash(cpu, env), $"runner full state for {label}");
        Equal(expected.Output, HashText(output.ToString()), $"diagnostic output for {label}");
        Equal(expected.Trace, HashText(trace.ToString()), $"trace output for {label}");
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
            BitConverter.GetBytes(0x500u).CopyTo(env.Ram, 8 * 4);
            env.Ram[0x500] = 0xCF;
        });
        Compare(["--limit", "99"], (cpu, env) => { env.Ram[0x100] = 0x0F; env.Ram[0x101] = 0xFF; });
        Compare(["--limit", "99", "--pftrap", "--upflog"], (cpu, env) =>
        {
            CPU._cr0.setter(cpu)(0x80010001);
            CPU._cr3.setter(cpu)(0x9000);
        });
        using var environment = new EmuEnvironment(memory: new byte[0x10000]);
        var invalidStack = new CPU { eip = 0x100, esp = 0x8000 };
        CPU._cr0.setter(invalidStack)(0x80010001);
        environment.Paging.Sync(invalidStack);
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
            Equal(true, ReferenceEquals(memory, env.Ram), "RAM injected without copying");
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
        Equal(true, left.Ata.Log && left.Pci.Log && left.PicMaster.Log && left.PicSlave.Log, "diagnostic enabled on left");
        Equal(false, right.Ata.Log || right.Pci.Log || right.PicMaster.Log || right.PicSlave.Log, "no global diagnostic leakage");
    }

    // 中断要求はタイマ周期(1 万命令)の境界で受け付けられ、その時点の CPU のコピーが公開される。
    // 再開すると上限まで走り、停止時にも最終状態が公開される。
    static void PauseResume()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = new CPU { eip = 0x100, code32 = true, stack32 = true, esp = 0x8000, eflags = 2 };
        byte[] code = [0x40, 0xEB, 0xFD]; // inc eax; jmp $-3
        code.CopyTo(env.Ram, 0x100);
        var control = new EmulationControl();
        var changes = new List<(bool paused, bool stopped, long count)>();
        using var paused = new ManualResetEventSlim();
        control.StateChanged += () => { lock (changes) changes.Add((control.IsPaused, control.HasStopped, control.Count)); if (control.IsPaused) paused.Set(); };
        control.Pause();
        var worker = new Thread(() => EmulationRunner.Run(RunOptions.Parse(["--limit", "25000", "--noirq", "--notrace"]), env, cpu, 0, null, TextWriter.Null, TextWriter.Null, control: control));
        worker.Start();
        Equal(true, paused.Wait(TimeSpan.FromSeconds(30)), "pause reached");
        var snapshot = control.Snapshot();
        Equal(10000L, control.Count, "paused at the service tick");
        Equal(5000u, snapshot.eax, "published CPU reflects executed instructions");
        Equal(true, snapshot.eip is 0x100 or 0x101, "published EIP inside the loop");
        Equal(0u, new CPU().eax, "snapshot is a copy");
        Thread.Sleep(50);
        Equal(10000L, control.Count, "no progress while paused");
        control.Resume();
        Equal(true, worker.Join(TimeSpan.FromSeconds(30)), "resumed to the limit");
        Equal(true, control.HasStopped && !control.IsPaused, "stopped state");
        Equal(25000L, control.Count, "final count published");
        Equal(12500u, control.Snapshot().eax, "final CPU published");
        Equal("True,False,10000;False,False,10000;False,True,25000", string.Join(";", changes.Select(change => $"{change.paused},{change.stopped},{change.count}")), "state change sequence");
        using var cancelled = new EmuEnvironment(memory: new byte[0x10000]);
        code.CopyTo(cancelled.Ram, 0x100);
        var stop = new CancellationTokenSource();
        var cancelControl = new EmulationControl();
        cancelControl.Pause();
        using var output = new StringWriter();
        var cancelWorker = new Thread(() => EmulationRunner.Run(RunOptions.Parse(["--limit", "25000", "--noirq", "--notrace"]), cancelled,
            new CPU { eip = 0x100, code32 = true, stack32 = true, esp = 0x8000, eflags = 2 }, 0, null, output, TextWriter.Null, stop: stop.Token, control: cancelControl));
        cancelWorker.Start();
        while (!cancelControl.IsPaused) Thread.Sleep(1);
        stop.Cancel();
        Equal(true, cancelWorker.Join(TimeSpan.FromSeconds(30)), "cancellation wakes a paused run");
        Equal(true, output.ToString().Contains("stopped by request"), "cancelled run reports the stop");
        Equal(10000L, cancelControl.Count, "cancelled while paused keeps the pause count");
    }

    // 割り込み線の選択: IF・PIC のマスク・in-service・IDT の準備を見て、
    // 優先順(タイマ、キーボード、COM1、COM2、マウス、ATA)に 1 本だけ選ぶ。
    // Windows 診断のゲストメモリ読み取り(Reader の合成): フレームチェーンの戻り番地、入口の引数とデバッグ文字列、読めないときの既定値。
    static void GuestReaders()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var memory = env.Ram;
        void Dword(uint address, uint value) => BitConverter.GetBytes(value).CopyTo(memory, address);
        Dword(0x1000, 0x2000); Dword(0x1004, 0xAAAA1111); // フレーム 1: 次 0x2000、戻り番地
        Dword(0x2000, 0); Dword(0x2004, 0xBBBB2222);      // フレーム 2: 終端
        var cpu = new CPU { ebp = 0x1000, esp = 0x3000, eip = 0x401000 };
        Equal("aaaa1111 bbbb2222 ", WindowsDiagnostics.Caller(env, cpu), "return addresses along the frame chain");
        Dword(0x3000, 0x5000); Dword(0x3004, 1); Dword(0x3008, 0x40010006); // ret、a1、a2 = DbgPrint
        Dword(0x5014, 5); Dword(0x5018, 0x6000);                           // 長さ 5、文字列ポインタ
        "Hi!\u0001\0"u8.CopyTo(memory.AsSpan(0x6000));
        using var output = new StringWriter();
        WindowsDiagnostics.LogEntry(env, cpu, 7, output);
        Equal("ENTRY 7: 00401000 -> 00401000 cr3=00000000 ret=00005000 a1=00000001 a2=40010006 dbg=\"Hi!.\"", output.ToString().TrimEnd(), "entry with a debug string");
        Dword(0x3008, 0x12345678);
        output.GetStringBuilder().Clear();
        WindowsDiagnostics.LogEntry(env, cpu, 8, output);
        Equal("ENTRY 8: 00401000 -> 00401000 cr3=00000000 ret=00005000 a1=00000001 a2=12345678", output.ToString().TrimEnd(), "entry without decoding");
        Equal("fallback", GuestMemory.Dword(0).Select<uint, string>(_ => throw new PageFaultException(0, 0)).Or("fallback")(env), "Or replaces a faulting read");
    }

    static void InterruptSelection()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        var cpu = new CPU { eflags = 2 };
        Equal(-1, IrqDelivery.Select(env, cpu), "nothing pending");
        env.TimerIrqPending = true;
        Equal(-1, IrqDelivery.Select(env, cpu), "IF=0 holds the timer");
        cpu.jf = true;
        Equal(0, IrqDelivery.Select(env, cpu), "timer selected");
        Equal(0x08, IrqDelivery.Vector(env, 0), "timer vector at the default base");
        env.PicMasterMask = 0x01;
        Equal(-1, IrqDelivery.Select(env, cpu), "masked timer is held");
        env.PicMasterMask = 0;
        env.PicMasterIsr = 0x01;
        Equal(-1, IrqDelivery.Select(env, cpu), "in-service timer is held");
        env.PicMasterIsr = 0;
        env.Keyboard.CommandByte = 0x47; // キーボード・AUX の割り込みを許可
        env.Keyboard.Output.Enqueue(0x1C);
        Equal(0, IrqDelivery.Select(env, cpu), "timer precedes the keyboard");
        env.TimerIrqPending = false;
        Equal(1, IrqDelivery.Select(env, cpu), "keyboard selected");
        env.Com2.Ier = 1; env.Com2.Rx.Enqueue(0x41);
        env.Com1.Ier = 1; env.Com1.Rx.Enqueue(0x42);
        env.Keyboard.Mouse.Output.Enqueue(0x08);
        Equal(1, IrqDelivery.Select(env, cpu), "keyboard precedes serial");
        env.Keyboard.Output.Clear();
        Equal(4, IrqDelivery.Select(env, cpu), "COM1 precedes COM2");
        env.Com1.Rx.Clear();
        Equal(3, IrqDelivery.Select(env, cpu), "COM2 precedes the mouse");
        env.Com2.Rx.Clear();
        Equal(12, IrqDelivery.Select(env, cpu), "mouse selected");
        Equal(0x74, IrqDelivery.Vector(env, 12), "mouse vector at the default slave base");
        env.PicMasterMask = 0x04;
        Equal(-1, IrqDelivery.Select(env, cpu), "cascade mask holds slave lines");
        env.PicMasterMask = 0;
        env.PicSlaveMask = 0x10;
        Equal(-1, IrqDelivery.Select(env, cpu), "slave mask holds the mouse");
        env.PicSlaveMask = 0;
        env.PicSlaveIsr = 0x10;
        Equal(-1, IrqDelivery.Select(env, cpu), "in-service mouse is held");
        env.PicSlaveIsr = 0;
        env.Keyboard.Mouse.Output.Clear();
        env.Ata = new AtaDevice(null) { IrqPending = true };
        Equal(-1, IrqDelivery.Select(env, cpu), "ATA is not delivered in real mode");
        CPU._cr0.setter(cpu)(1);
        Equal(-1, IrqDelivery.Select(env, cpu), "protected mode waits for the PIC to be reprogrammed");
        env.PicMasterBase = 0x20; env.PicSlaveBase = 0x28;
        Equal(-1, IrqDelivery.Select(env, cpu), "protected mode waits for the IDT gate");
        cpu.idt_limit = (0x2E + 1) * 8 - 2;
        Equal(-1, IrqDelivery.Select(env, cpu), "IDT limit one byte short of the gate");
        cpu.idt_limit = (0x2E + 1) * 8 - 1;
        Equal(14, IrqDelivery.Select(env, cpu), "ATA selected");
        Equal(0x2E, IrqDelivery.Vector(env, 14), "ATA vector");
        env.PicSlaveIsr = 0x40;
        Equal(-1, IrqDelivery.Select(env, cpu), "in-service ATA is held");
        env.PicSlaveIsr = 0;
        cpu.jf = false;
        Equal(-1, IrqDelivery.Select(env, cpu), "IF=0 holds every line");
    }

    // 名前付きのケース(ランナーが順に実行し、失敗したケース名を報告する)。
    public static (string Name, Action Run)[] Cases =>
    [
        ("options", Options),
        ("golden", () => { goldenIndex = 0; Runners(); }),
        ("environments", Environments),
        ("command-line", CommandLine),
        ("pause-resume", PauseResume),
        ("interrupt-selection", InterruptSelection),
        ("guest-readers", GuestReaders),
        // 同じ golden ケースを --no-translate(1 命令ずつ)でも走らせ、既定の翻訳ブロックと同じ結果になることを確かめる。
        ("golden-plain", () => { goldenIndex = 0; plainGolden = true; try { Runners(); } finally { plainGolden = false; } }),
    ];

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
            env.Ram[0x100] = 0x40;
            SnapshotStore.Save("resume.snap", 10, new CPU { eip = 0x100, code32 = true, stack32 = true, esp = 0x8000 }, env);
            var resumeResult = Run("--resume", "--snapshot", "resume.snap", "--limit", "11", "--notrace");
            Equal(0, resumeResult, "CLI resume success: " + error);
            // ゲストのリセット要求で環境を作り直し、リセットベクタから続行して上限で終わる。
            using var resetEnv = new EmuEnvironment();
            byte[] reset = [0xB0, 0xFE, 0xE6, 0x64, 0xEB, 0xFE];
            reset.CopyTo(resetEnv.Ram, 0x100);
            SnapshotStore.Save("reset.snap", 20, new CPU { eip = 0x100, code32 = true, stack32 = true, esp = 0x8000 }, resetEnv);
            Equal(0, Run("--resume", "--snapshot", "reset.snap", "--limit", "20100", "--notrace", "--noirq"), "CLI reset loop exit code: " + error);
            Equal(true, error.ToString().Contains("[system] reset at instruction"), "CLI reports the reset");
            using var afterReset = new EmuEnvironment();
            var resetResult = SnapshotStore.Load("reset.snap", afterReset);
            Equal(20100L, resetResult.count, "count continues across the reset");
            Equal(true, resetResult.cpu.cs is 0xF000 or 0x0008, "restarted from the reset vector (SeaBIOS may have entered protected mode): " + resetResult.cpu.cs.ToString("x"));
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
