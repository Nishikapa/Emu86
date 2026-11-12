using System.Reflection;

namespace Emu86;

public static class EnvironmentFactory
{
    // 通常起動用の環境: 埋め込み BIOS、指定されたディスク(attachDisk が偽なら後からスナップショットがつなぐ)、
    // CD/DVD-ROM の媒体(cdromPath。ISO ファイルか "D:" のようなドライブ)、RAM の大きさ(ramBytes。0 なら既定)。
    public static EmuEnvironment Create(DiskConfiguration disk = null, bool attachDisk = true, string cdromPath = null, long ramBytes = 0,
        long instructionsPerSecond = GuestClock.DefaultInstructionsPerSecond, int compileAfter = TranslationCache.DefaultCompileAfter)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instructionsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(compileAfter);
        var env = new EmuEnvironment(memory: ramBytes > 0 ? new byte[ramBytes] : null, bios: LoadEmbeddedBios());
        try
        {
            env.Clock.InstructionsPerSecond = instructionsPerSecond;
            env.Translations.CompileAfter = compileAfter;
            if (attachDisk && disk != null) env.Ata = new AtaDevice(disk.OpenOverlay());
            if (cdromPath != null)
            {
                var media = new CdMedia(cdromPath);
                env.Cdrom = new AtapiDevice(media);
                Console.Error.WriteLine($"[cdrom] {cdromPath}: {media.TotalSectors} sectors ({media.TotalSectors * CdMedia.SectorSize / (1024 * 1024)} MB)");
            }
            return env;
        }
        catch
        {
            env.Dispose();
            throw;
        }
    }

    static byte[] LoadEmbeddedBios()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("bios.bin", StringComparison.OrdinalIgnoreCase));
        if (resource == null) return null;
        using var stream = assembly.GetManifestResourceStream(resource);
        var bios = new byte[stream.Length];
        stream.ReadExactly(bios);
        return bios;
    }
}
