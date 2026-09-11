using System.Globalization;

static class TestProgram
{
    static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        (string Name, Action Run)[] suites =
        [
            ("core", CoreTests.Run),
            ("devices", DeviceTests.Run),
            ("decode", DecodeTests.Run),
            ("snapshot", SnapshotTests.Run),
            ("runner", RunnerTests.Run),
            ("completion", CompletionTests.Run),
            ("disk", DiskTests.Run)
        ];
        if (args.Any(argument => !suites.Any(suite => suite.Name == argument)))
        {
            Console.Error.WriteLine("Usage: dotnet run --project tests/Emu86.Tests -- [core devices decode snapshot runner completion disk]");
            return 2;
        }
        var failed = 0;
        foreach (var suite in suites.Where(suite => args.Length == 0 || args.Contains(suite.Name)))
        {
            try
            {
                using var directory = new TestDirectory();
                suite.Run();
                Console.WriteLine($"PASS {suite.Name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {suite.Name}: {exception}");
            }
        }
        Console.WriteLine(failed == 0 ? "All selected suites passed." : $"{failed} suite(s) failed.");
        return failed == 0 ? 0 : 1;
    }
}
