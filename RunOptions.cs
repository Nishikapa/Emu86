using System.Globalization;

namespace Emu86;

internal readonly record struct AddressRange(uint Lo, uint Hi)
{
    public bool Contains(uint address) => address >= Lo && address < Hi;
}

internal sealed class DiagnosticOptions
{
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
    public bool IntLog { get; set; }
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
    public long Limit { get; private set; } = InstructionLimit;
    public string SnapshotPath { get; private set; } = "snapshot.snap";
    public string DiskPath { get; private set; }
    public string OverlayPath { get; private set; }
    public (long Start, int Count)? DumpLba { get; private set; }
    public DiagnosticOptions Diagnostics { get; } = new();

    public static RunOptions Parse(string[] args)
    {
        var options = new RunOptions();
        var diagnostics = options.Diagnostics;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            string Value()
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--"))
                    throw new ArgumentException($"{option}: missing value");
                return args[index];
            }
            long Decimal()
            {
                if (!long.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    throw new ArgumentException($"{option}: expected a non-negative decimal integer");
                return value;
            }
            uint Hex()
            {
                var value = Value();
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
                if (!uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address))
                    throw new ArgumentException($"{option}: expected a 32-bit hexadecimal address");
                return address;
            }
            AddressRange Range()
            {
                var range = new AddressRange(Hex(), Hex());
                if (range.Lo >= range.Hi) throw new ArgumentException($"{option}: expected lo < hi");
                return range;
            }
            switch (option)
            {
                case "--help": options.Help = true; break;
                case "--resume": options.Resume = true; break;
                case "--slow": options.Slow = true; break;
                case "--noirq": options.NoIrq = true; break;
                case "--limit": options.Limit = Decimal(); break;
                case "--snapshot": options.SnapshotPath = Value(); break;
                case "--disk": options.DiskPath = Value(); break;
                case "--overlay": options.OverlayPath = Value(); break;
                case "--dumplba":
                    var start = Decimal();
                    var count = Decimal();
                    if (count is <= 0 or > int.MaxValue || start > long.MaxValue - count)
                        throw new ArgumentException("--dumplba: invalid sector range");
                    options.DumpLba = (start, (int)count);
                    break;
                case "--trace-all": diagnostics.TraceAll = true; break;
                case "--regtrace": diagnostics.RegTrace = true; break;
                case "--notrace": diagnostics.NoTrace = true; break;
                case "--brhist": diagnostics.BranchHistory = true; break;
                case "--esptrap": diagnostics.EspTrap = true; break;
                case "--pftrap": diagnostics.PfTrap = true; break;
                case "--atalog": diagnostics.AtaLog = true; break;
                case "--pcilog": diagnostics.PciLog = true; break;
                case "--piclog": diagnostics.PicLog = true; break;
                case "--intlog": diagnostics.IntLog = true; break;
                case "--watchval": diagnostics.WatchValue = Hex(); break;
                case "--breakeax": diagnostics.BreakEax = Hex(); break;
                case "--breakeip": diagnostics.BreakEip = Hex(); break;
                case "--breakat": diagnostics.BreakAt = Decimal(); break;
                case "--entrylog": diagnostics.EntryLog = Range(); break;
                case "--breakrange": diagnostics.BreakRange = Range(); break;
                case "--wlog": diagnostics.WriteLog = Range(); break;
                case "--watch": diagnostics.Watch = Range(); break;
                case "--upflog":
                    diagnostics.UserPageFaultMin = index + 1 < args.Length && !args[index + 1].StartsWith("--") ? Decimal() : 0;
                    break;
                default: throw new ArgumentException($"Unknown option: {option}");
            }
        }
        if (options.OverlayPath != null && options.DiskPath == null)
            throw new ArgumentException("--overlay requires --disk");
        return options;
    }

    public const string Usage = """
        Usage: Emu86 [--resume] [--snapshot path] [--limit count] [--slow] [--noirq]
          --disk path [--overlay path]   Select a base image and writable overlay
          --dumplba start count         Dump sectors from the existing overlay to lba_dump.bin
          --trace-all --regtrace --notrace --brhist --esptrap --pftrap
          --atalog --pcilog --piclog --intlog --upflog [minCount]
          --breakeip hex [--breakeax hex] --breakat count --watchval hex
          --entrylog lo hi --breakrange lo hi --wlog lo hi --watch lo hi
        Counts/LBAs are decimal; addresses and range bounds are hexadecimal (optional 0x).
        """;
}
