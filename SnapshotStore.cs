using System.Security.Cryptography;
using System.Text;

namespace Emu86;

public static class SnapshotStore
{
    const string Magic = "EMU86SNP";
    const int Version = 7;
    const int HashSize = 32;

    public static void Save(string path, long count, CPU cpu, EmuEnvironment env)
    {
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes(Magic));
                writer.Write(Version);
                writer.Write(count);
                cpu.WriteTo(writer);
                env.SaveState(writer);
                env.SaveRuntimeState(writer);
                cpu.SaveRuntimeState(writer);
                env.SaveDeviceState(writer);
                if (env.Ata != null)
                {
                    var diskPath = env.Ata.DiskPath ?? throw new InvalidOperationException("ATA disk is not attached");
                    env.Ata.Flush();
                    using var disk = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    writer.Write(disk.Length);
                    writer.Flush();
                    disk.CopyTo(stream);
                }
                else
                    writer.Write(0L);
                writer.Flush();
                var length = stream.Length;
                var hash = HashPrefix(stream, length);
                stream.Position = length;
                writer.Write(hash);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static (long count, CPU cpu) Load(string path, EmuEnvironment env, DiskConfiguration diskConfiguration = null)
    {
        if (env.Ata != null) throw new InvalidOperationException("Load snapshots before attaching a disk");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)) != Magic)
            throw new InvalidDataException("Not an Emu86 snapshot");
        var version = reader.ReadInt32();
        if (version is < 2 or > Version) throw new InvalidDataException("Unsupported snapshot version");
        if (version >= 7)
        {
            var hash = HashPrefix(stream, stream.Length - HashSize);
            var storedHash = reader.ReadBytes(HashSize);
            if (!CryptographicOperations.FixedTimeEquals(hash, storedHash))
                throw new InvalidDataException("Snapshot checksum mismatch");
            stream.Position = Magic.Length + sizeof(int);
        }
        var count = reader.ReadInt64();
        if (count < 0) throw new InvalidDataException("Invalid instruction count");
        var cpu = CPU.ReadFrom(reader);
        env.LoadState(reader);
        env.LoadRuntimeState(reader, version, count);
        cpu.LoadRuntimeState(reader, version);
        if (version >= 7)
        {
            env.LoadDeviceState(reader);
            var diskLength = reader.ReadInt64();
            if (diskLength < 0 || diskLength != stream.Length - HashSize - stream.Position
                || (diskLength > 0) != (env.Ata != null))
                throw new InvalidDataException("Invalid embedded disk length");
            if (diskLength > 0) RestoreDisk(stream, diskLength, env, diskConfiguration);
        }
        else
        {
            if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected snapshot data");
            if (env.Ata != null)
            {
                var saved = path + ".avhdx";
                if (File.Exists(saved))
                {
                    using var disk = new FileStream(saved, FileMode.Open, FileAccess.Read, FileShare.Read);
                    RestoreDisk(disk, disk.Length, env, diskConfiguration);
                }
                else
                {
                    var configuration = diskConfiguration ?? throw new FileNotFoundException("Snapshot requires a base disk image");
                    env.Ata.AttachDisk(configuration.OpenOverlay());
                }
            }
        }
        Ext.EnvSyncPaging(env, cpu);
        return (count, cpu);
    }

    static void RestoreDisk(Stream source, long length, EmuEnvironment env, DiskConfiguration diskConfiguration)
    {
        var configuration = diskConfiguration ?? throw new FileNotFoundException("Snapshot requires a base disk image");
        var basePath = configuration.BaseImagePath;
        var overlay = configuration.OverlayPath;
        var temporary = overlay + ".restore.tmp";
        try
        {
            using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write))
            {
                CopyExactly(source, target, length);
                target.Flush(flushToDisk: true);
            }
            using (var candidate = new DiskImage(temporary, writable: true))
            {
                if (!string.Equals(candidate.ParentPath, Path.GetFullPath(basePath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Snapshot disk has a different base image");
            }
            File.Move(temporary, overlay, overwrite: true);
            env.Ata.AttachDisk(new DiskImage(overlay, writable: true));
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    static byte[] HashPrefix(Stream stream, long length)
    {
        if (length < Magic.Length + sizeof(int) + sizeof(long))
            throw new InvalidDataException("Truncated snapshot");
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        while (length > 0)
        {
            var count = (int)Math.Min(buffer.Length, length);
            stream.ReadExactly(buffer.AsSpan(0, count));
            hash.AppendData(buffer, 0, count);
            length -= count;
        }
        return hash.GetHashAndReset();
    }

    static void CopyExactly(Stream source, Stream target, long length)
    {
        var buffer = new byte[81920];
        while (length > 0)
        {
            var count = (int)Math.Min(buffer.Length, length);
            source.ReadExactly(buffer.AsSpan(0, count));
            target.Write(buffer, 0, count);
            length -= count;
        }
    }

    internal static int ReadCount(BinaryReader reader, int maximum)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum) throw new InvalidDataException("Invalid snapshot collection length");
        return count;
    }
}
