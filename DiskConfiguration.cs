namespace Emu86;

public sealed class DiskConfiguration
{
    public string BaseImagePath { get; }
    public string OverlayPath { get; }

    public DiskConfiguration(string baseImagePath, string overlayPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseImagePath);
        BaseImagePath = Path.GetFullPath(baseImagePath);
        OverlayPath = Path.GetFullPath(overlayPath ?? Path.ChangeExtension(BaseImagePath, ".avhdx"));
        if (string.Equals(BaseImagePath, OverlayPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Base image and overlay must have different paths", nameof(overlayPath));
    }

    public static DiskConfiguration Discover(string directory)
    {
        foreach (var name in new[] { "sample.vhd", "sample.vhdx" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return new DiskConfiguration(path);
        }
        return null;
    }

    public DiskImage OpenOverlay()
    {
        if (!File.Exists(BaseImagePath)) throw new FileNotFoundException("Base disk image not found", BaseImagePath);
        DiskImage.EnsureOverlay(OverlayPath, BaseImagePath);
        return new DiskImage(OverlayPath, writable: true);
    }
}
