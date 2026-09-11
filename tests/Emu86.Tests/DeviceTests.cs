using Emu86;
using System.Security.Cryptography;
using static Emu86.Ext;

static class DeviceTests
{
    static int checks;

    static void Equal<T>(T expected, T actual, string label)
    {
        checks++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label}: expected {expected}, got {actual}");
    }

    static void PortSequences()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        Equal((byte)64, env.Cmos[0x15], "CMOS RAM size");
        EnvOutPort(env, 0x70, 0x95);
        Equal((byte)64, EnvInPort(env, 0x71), "CMOS NMI index mask");
        EnvOutPort(env, 0x71, 0xAB);
        Equal((byte)0xAB, env.Cmos[0x15], "CMOS write");
        foreach (var port in new[] { 0x20, 0xA0 })
        {
            EnvOutPort(env, port, 0x11);
            EnvOutPort(env, port + 1, 0x30);
            EnvOutPort(env, port + 1, 4);
            EnvOutPort(env, port + 1, 1);
            EnvOutPort(env, port + 1, 0x5A);
            Equal((byte)0x5A, EnvInPort(env, port + 1), "PIC mask");
            env.PicMasterIsr = env.PicSlaveIsr = 0xFF;
            EnvOutPort(env, port, 0x63);
            Equal((byte)0xF7, EnvInPort(env, port), "PIC specific EOI");
            EnvOutPort(env, port, 0x20);
            Equal((byte)0, EnvInPort(env, port), "PIC nonspecific EOI");
        }
        EnvOutPort(env, 0x43, 0xC0);
        Equal((byte)0x36, EnvInPort(env, 0x40), "PIT status first");
        Equal((byte)0xFF, EnvInPort(env, 0x40), "PIT latched low");
        Equal((byte)0xFF, EnvInPort(env, 0x40), "PIT latched high");
        Equal((ushort)0xFEFF, env.PitCounter, "PIT counter progress");
        EnvOutPort(env, 0x43, 0xB6);
        EnvOutPort(env, 0x42, 0x34);
        EnvOutPort(env, 0x42, 0x12);
        EnvOutPort(env, 0x61, 1);
        Equal((byte)0x11, EnvInPort(env, 0x61), "PIT wait 1");
        Equal((byte)0x01, EnvInPort(env, 0x61), "PIT wait 2");
        Equal((byte)0x31, EnvInPort(env, 0x61), "PIT OUT2");
        EnvOutPort(env, 0x61, 0);
        Equal(false, env.Pit2Armed, "PIT gate disabled");
        EnvOutPort(env, 0x64, 0x60);
        EnvOutPort(env, 0x60, 0x47);
        EnvOutPort(env, 0x64, 0x20);
        Equal((byte)0x15, EnvInPort(env, 0x64), "keyboard OBF");
        Equal((byte)0x47, EnvInPort(env, 0x60), "keyboard command byte");
        Equal((byte)0x14, EnvInPort(env, 0x64), "keyboard empty");
        EnvOutPort(env, 0x60, 0xF2);
        foreach (var value in new byte[] { 0xFA, 0xAB, 0x83, 0x83 })
            Equal(value, EnvInPort(env, 0x60), "keyboard ID and empty read");
        env.Pm1Sts = env.Gpe0Sts = 0xFFFF;
        EnvOutPortN(env, 0xB000, 4, 0x12340F80);
        Equal(0x1234F07Fu, EnvInPortN(env, 0xB000, 4), "PM W1C and enable");
        EnvOutPortN(env, 0xAFE0, 4, 0x5678F00F);
        Equal(0x56780FF0u, EnvInPortN(env, 0xAFE0, 4), "GPE W1C and enable");
        env.Tsc = 1_000_000;
        Equal(3579545u, EnvInPortN(env, 0xB008, 4), "PM timer");
        EnvOutPortN(env, 0x1FFFF, 2, 0x1234);
        Equal(0x1234u, EnvInPortN(env, 0xFFFF, 2), "wrapped fallback ports");
        Equal(0xFFFFFFFFu, EnvInPortN(env, 0x170, 4), "absent secondary IDE");
        using var other = new EmuEnvironment(memory: new byte[0x10000]);
        Equal((byte)0, other.PicMasterMask, "independent PIC state");
        Equal(0, other.KbdOut.Count, "independent keyboard state");
        Equal((ushort)0, other.Pm1En, "independent ACPI state");
    }

    static void Compatibility()
    {
        using var env = new EmuEnvironment(memory: new byte[0x10000]);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        using var diagnostics = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(diagnostics);
        try
        {
            env.PicLog = true;
            int[] ports = [0x20, 0x21, 0xA0, 0xA1, 0x40, 0x42, 0x43, 0x60, 0x61, 0x64, 0x70, 0x71, 0xB2,
                0xAFE0, 0xAFE1, 0xAFE2, 0xAFE3, 0xB000, 0xB001, 0xB002, 0xB003, 0xB004, 0xB005, 0xB008, 0xB100, 0xCF8, 0xCFC, 0xFFFF];
            for (var value = 0; value < 256; value++)
            {
                env.Tsc = (ulong)value * 123456789;
                env.PicMasterIsr = env.PicSlaveIsr = (byte)value;
                env.Pm1Sts = env.Gpe0Sts = (ushort)(value * 257);
                foreach (var port in ports)
                {
                    EnvOutPort(env, port, (byte)value);
                    writer.Write(EnvInPort(env, port));
                    foreach (var size in new[] { 1, 2, 4 })
                    {
                        EnvOutPortN(env, port, size, unchecked((uint)value * 0x01020304));
                        writer.Write(EnvInPortN(env, port, size));
                    }
                }
                env.SaveRuntimeState(writer);
                env.SaveDeviceState(writer);
            }
            env.SaveState(writer);
            writer.Write(diagnostics.ToString().Replace("\r\n", "\n"));
            writer.Flush();
            var hash = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
            Equal("D39764DA276B368FBDE17D616723972D0C721E7F4B8381B90BEC1857CB9209A1", hash, "pre-refactor device behavior");
            Console.WriteLine($"Device compatibility SHA256={hash}");
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    public static void Run()
    {
        PortSequences();
        Compatibility();
        Console.WriteLine($"Device assertions passed: {checks}");
    }
}
