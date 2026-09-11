using System.Reflection;

namespace Emu86;

public static class EnvironmentFactory
{
    public static EmuEnvironment Create(DiskConfiguration disk = null, bool attachDisk = true)
    {
        var env = new EmuEnvironment(bios: LoadEmbeddedBios());
        try
        {
            if (attachDisk && disk != null) env.Ata = new AtaDevice(disk.OpenOverlay());
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
