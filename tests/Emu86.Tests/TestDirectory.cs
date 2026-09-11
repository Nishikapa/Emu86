sealed class TestDirectory : IDisposable
{
    readonly string previous = Directory.GetCurrentDirectory();
    readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-runs")) + Path.DirectorySeparatorChar;
    readonly string path;

    public TestDirectory()
    {
        path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Directory.SetCurrentDirectory(path);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(previous);
        var target = Path.GetFullPath(path);
        if (!target.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Test cleanup target is outside the test directory");
        Directory.Delete(target, recursive: true);
    }
}
