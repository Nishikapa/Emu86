using System.Globalization;
using static Emu86.ArgParsers;
using static Emu86.CommandOption;

namespace Emu86;

internal readonly record struct AddressRange(uint Lo, uint Hi)
{
    public bool Contains(uint address) => address >= Lo && address < Hi;
}

internal sealed class DiagnosticOptions
{
    // 命令ごとに CPU を見る診断が有効か(翻訳ブロックは複数命令をまとめて実行するので、これらのときは使わない)。
    public bool PerInstruction => TraceAll || RegTrace || BranchHistory || EspTrap || WatchValue != null || BreakEax != null
        || BreakEip != 0 || BreakAt != 0 || EntryLog.Hi != 0 || BreakRange.Hi != 0 || WriteLog != null || Watch.Hi != 0;
    public bool TraceAll { get; set; }
    public bool RegTrace { get; set; }
    public bool NoTrace { get; set; }
    public bool Trace => TraceAll || !NoTrace;
    public bool BranchHistory { get; set; }
    public bool EspTrap { get; set; }
    public bool PfTrap { get; set; }
    public bool AtaLog { get; set; }
    public bool PciLog { get; set; }
    public bool PicLog { get; set; }
    public bool VgaLog { get; set; }
    public bool IntLog { get; set; }
    public int IntLogLast { get; set; } = 4;
    public uint? WatchValue { get; set; }
    public uint? BreakEax { get; set; }
    public uint BreakEip { get; set; }
    public long BreakAt { get; set; }
    public long? UserPageFaultMin { get; set; }
    public AddressRange EntryLog { get; set; }
    public AddressRange BreakRange { get; set; }
    public AddressRange? WriteLog { get; set; }
    public AddressRange Watch { get; set; }
}

internal sealed class RunOptions
{
    public const long InstructionLimit = 500_000_000;
    public bool Help { get; private set; }
    public bool Resume { get; private set; }
    public bool Slow { get; private set; }
    public bool NoIrq { get; private set; }
    public bool Translate { get; private set; } = true;
    public bool Window { get; private set; }
    public string Com1Pipe { get; private set; }
    public string Com2Pipe { get; private set; }
    public bool BootDebug { get; private set; }
    public long Limit { get; private set; } = InstructionLimit;
    public long SnapshotInterval { get; private set; } = 100_000_000;
    public string SnapshotPath { get; private set; } = "snapshot.snap";
    public DateTime? Rtc { get; private set; }
    public string ScreenPath { get; private set; }
    public string VramDumpPath { get; private set; }
    public string DiskPath { get; private set; }
    public string OverlayPath { get; private set; }
    public (long Start, int Count)? DumpLba { get; private set; }
    public DiagnosticOptions Diagnostics { get; } = new();

    // 値の形。アドレス範囲は lo hi の 2 つの十六進数で lo < hi。
    static readonly ArgParser<AddressRange> Range =
        from lo in Hex
        from hi in Hex
        from _ in Require(lo < hi, "expected lo < hi")
        select new AddressRange(lo, hi);

    static readonly ArgParser<(long Start, int Count)> SectorRange =
        from start in Integer
        from count in Integer
        from _ in Require(count is > 0 and <= int.MaxValue && start <= long.MaxValue - count, "invalid sector range")
        select (start, (int)count);

    static readonly ArgParser<DateTime> Timestamp = Value.Select(text =>
        DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new ArgumentException("expected yyyy-MM-ddTHH:mm:ss"));

    static readonly ArgParser<int> IntLogTail =
        (from last in Integer
         from _ in Require(last is >= 1 and <= 100000, "expected 1..100000")
         select (int)last).Optional(4);

    // オプションの表。解析(Parse)と使い方(Usage)はこの表から作る。
    static readonly CommandOption[] Table =
    [
        Flag("Run", "--resume", "Resume from the snapshot instead of booting from the reset vector", o => o.Resume = true),
        Of("Run", "--snapshot", "path", "Snapshot file to save to and resume from (default snapshot.snap)", Value, (o, v) => o.SnapshotPath = v),
        Of("Run", "--rtc", "yyyy-MM-ddTHH:mm:ss", "Start the real-time clock at this guest time on a cold boot (default: the host's local time)", Timestamp, (o, v) => o.Rtc = v),
        Of("Run", "--screen", "path", "Save the VGA display as a PNG at each snapshot and when the run stops", Value, (o, v) => o.ScreenPath = v),
        Of("Run", "--limit", "count|max", "Stop after count instructions (max = run until the window closes)",
            Word("max").Select(_ => long.MaxValue).Or(Integer), (o, v) => o.Limit = v),
        Of("Run", "--snapshot-interval", "count", "Save a snapshot every count instructions (default 100000000, 0 = only at the end)",
            Integer, (o, v) => o.SnapshotInterval = v == 0 ? long.MaxValue : v),
        Flag("Run", "--slow", "Run every instruction on the LINQ core instead of the fast core", o => o.Slow = true),
        Flag("Run", "--noirq", "Do not deliver hardware interrupts", o => o.NoIrq = true),
        Flag("Run", "--no-translate", "Run one instruction per step through the IR instead of translating hot basic blocks and chaining them (the default is 40-50% faster; per-instruction diagnostics also run one instruction per step; see docs/adr/0002 and 0003)", o => o.Translate = false),
        Of("Run", "--compile-after", "count", $"Compile a translated block to a delegate after count executions (default {TranslationCache.CompileAfter})",
            Integer, (o, v) => TranslationCache.CompileAfter = v > int.MaxValue ? int.MaxValue : (int)v),
        Flag("Run", "--window", "Show the VGA display in a 640x480 window (keyboard and mouse go to the guest)", o => o.Window = true),
        Of("Run", "--com1", "name", @"Serve COM1 on \\.\pipe\name (WinDbg: com:pipe,port=\\.\pipe\name,resets=0,reconnect)", Value, (o, v) => o.Com1Pipe = v),
        Of("Run", "--com2", "name", @"Serve COM2 on \\.\pipe\name", Value, (o, v) => o.Com2Pipe = v),
        Flag("Run", "--boot-debug", "Drive NTLDR's F8 menu to boot Windows in Debugging Mode (COM2, 19200 bps)", o => o.BootDebug = true),
        Flag("Run", "--help", "Show this help and exit", o => o.Help = true),
        Of("Disk", "--disk", "path", "Base disk image (default: sample.vhd or sample.vhdx in the current directory)", Value, (o, v) => o.DiskPath = v),
        Of("Disk", "--overlay", "path", "Writable overlay for --disk (default: the base path with .avhdx)", Value, (o, v) => o.OverlayPath = v),
        Of("Disk", "--dumplba", "start count", "Dump sectors from the existing overlay to lba_dump.bin", SectorRange, (o, v) => o.DumpLba = v),
        Of("Diagnostics", "--dumpvram", "path", "Write the VGA registers and the four VRAM planes to the file when the run stops", Value, (o, v) => o.VramDumpPath = v),
        Flag("Diagnostics", "--trace-all", "Trace every instruction to trace.log, not only control transfers", o => o.Diagnostics.TraceAll = true),
        Flag("Diagnostics", "--regtrace", "Include the general registers and EFLAGS in each trace line", o => o.Diagnostics.RegTrace = true),
        Flag("Diagnostics", "--notrace", "Do not write trace.log", o => o.Diagnostics.NoTrace = true),
        Flag("Diagnostics", "--brhist", "Report the last 64 branches on stop and stop when CS becomes null", o => o.Diagnostics.BranchHistory = true),
        Flag("Diagnostics", "--esptrap", "Stop when ESP enters the kernel entry stack", o => o.Diagnostics.EspTrap = true),
        Flag("Diagnostics", "--pftrap", "Stop on a protection page fault", o => o.Diagnostics.PfTrap = true),
        Flag("Diagnostics", "--atalog", "Log ATA commands to stderr", o => o.Diagnostics.AtaLog = true),
        Flag("Diagnostics", "--pcilog", "Log PCI configuration accesses to stderr", o => o.Diagnostics.PciLog = true),
        Flag("Diagnostics", "--piclog", "Log PIC mask changes to stderr", o => o.Diagnostics.PicLog = true),
        Flag("Diagnostics", "--vgalog", "Log VGA register writes to stderr", o => o.Diagnostics.VgaLog = true),
        Of("Diagnostics", "--intlog", "[lastCount]", "Log protected-mode interrupt deliveries; report the first 30 and the last lastCount (default 4)",
            IntLogTail, (o, v) => { o.Diagnostics.IntLog = true; o.Diagnostics.IntLogLast = v; }),
        Of("Diagnostics", "--upflog", "[minCount]", "Log user-mode page faults from instruction minCount on (default 0)",
            Integer.Optional(0), (o, v) => o.Diagnostics.UserPageFaultMin = v),
        Of("Diagnostics", "--breakeip", "hex", "Stop when EIP reaches the address", Hex, (o, v) => o.Diagnostics.BreakEip = v),
        Of("Diagnostics", "--breakeax", "hex", "With --breakeip: stop only when EAX also matches", Hex, (o, v) => o.Diagnostics.BreakEax = v),
        Of("Diagnostics", "--breakat", "count", "Stop at the instruction count", Integer, (o, v) => o.Diagnostics.BreakAt = v),
        Of("Diagnostics", "--watchval", "hex", "Stop when any general register holds the value", Hex, (o, v) => o.Diagnostics.WatchValue = v),
        Of("Diagnostics", "--entrylog", "lo hi", "Log the first 400 entries into the EIP range (with Windows IRP/debug-string decoding)", Range, (o, v) => o.Diagnostics.EntryLog = v),
        Of("Diagnostics", "--breakrange", "lo hi", "Stop when EIP enters the range (protected mode)", Range, (o, v) => o.Diagnostics.BreakRange = v),
        Of("Diagnostics", "--wlog", "lo hi", "Record writes to the physical range and show the last 30 on stop", Range, (o, v) => o.Diagnostics.WriteLog = v),
        Of("Diagnostics", "--watch", "lo hi", "Stop on a write to the physical range (paging on)", Range, (o, v) => o.Diagnostics.Watch = v),
    ];

    // コマンドライン全体: 表のどれかに当てはまるオプションの 0 回以上の繰り返し。
    static readonly ArgParser<IReadOnlyList<Action<RunOptions>>> CommandLine =
        Choice([.. Table.Select(option => option.Parser)]).Many();

    public static RunOptions Parse(string[] args)
    {
        var (_, actions, end) = CommandLine(args, 0);
        if (end < args.Length) throw new ArgumentException($"Unknown option: {args[end]}");
        var options = new RunOptions();
        foreach (var action in actions) action(options);
        if (options.OverlayPath != null && options.DiskPath == null)
            throw new ArgumentException("--overlay requires --disk");
        return options;
    }

    public static readonly string Usage = BuildUsage();

    // グループごとに見出し行と、構文を揃えた説明行を並べる。
    static string BuildUsage()
    {
        var width = Table.Max(option => option.Syntax.Length) + 2;
        var lines = Table
            .GroupBy(option => option.Group)
            .SelectMany(group => group
                .Select(option => $"  {option.Syntax.PadRight(width)}{option.Description}")
                .Prepend($"{group.Key}:"))
            .Prepend("Usage: Emu86 [options]")
            .Append("Counts/LBAs are decimal; addresses and range bounds are hexadecimal (optional 0x).");
        return string.Join(Environment.NewLine, lines);
    }
}
