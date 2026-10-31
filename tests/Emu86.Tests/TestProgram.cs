using System.Globalization;

// コンソール型のテストランナー。引数はスイート名(core)かケース名(core/fpu)で、省略時は全部。
// --list は名前の一覧。ケースが失敗しても同じスイートの残りのケースは実行し、終了コードは
// 成功 0、失敗 1、不明な名前 2。
static class TestProgram
{
    static readonly (string Name, (string Name, Action Run)[] Cases)[] suites =
    [
        ("core", CoreTests.Cases),
        ("state", StateTests.Cases),
        ("devices", DeviceTests.Cases),
        ("vga", VgaTests.Cases),
        ("decode", DecodeTests.Cases),
        ("snapshot", SnapshotTests.Cases),
        ("runner", RunnerTests.Cases),
        ("completion", CompletionTests.Cases),
        ("disk", DiskTests.Cases)
    ];

    static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        if (args is ["--list"])
        {
            foreach (var suite in suites)
                foreach (var testCase in suite.Cases)
                    Console.WriteLine($"{suite.Name}/{testCase.Name}");
            return 0;
        }
        var names = suites.SelectMany(suite => suite.Cases.Select(testCase => $"{suite.Name}/{testCase.Name}").Prepend(suite.Name)).ToHashSet();
        if (args.Any(argument => !names.Contains(argument)))
        {
            Console.Error.WriteLine("Usage: dotnet run --project tests/Emu86.Tests -- [--list] [suite | suite/case ...]");
            Console.Error.WriteLine("Suites: " + string.Join(" ", suites.Select(suite => suite.Name)));
            return 2;
        }
        bool Selected(string suite, string testCase) => args.Length == 0 || args.Contains(suite) || args.Contains($"{suite}/{testCase}");

        var failed = 0;
        foreach (var suite in suites)
        {
            var selected = suite.Cases.Where(testCase => Selected(suite.Name, testCase.Name)).ToArray();
            if (selected.Length == 0) continue;
            using var directory = new TestDirectory();
            var before = Assert.Assertions;
            var suiteFailures = 0;
            foreach (var testCase in selected)
            {
                try { testCase.Run(); }
                catch (Exception exception)
                {
                    suiteFailures++;
                    Console.Error.WriteLine($"FAIL {suite.Name}/{testCase.Name}: {exception}");
                }
            }
            failed += suiteFailures;
            Console.WriteLine(suiteFailures == 0
                ? $"PASS {suite.Name} ({selected.Length} cases, {Assert.Assertions - before} assertions)"
                : $"FAIL {suite.Name} ({suiteFailures} of {selected.Length} cases failed)");
        }
        Console.WriteLine(failed == 0 ? "All selected suites passed." : $"{failed} case(s) failed.");
        return failed == 0 ? 0 : 1;
    }
}
