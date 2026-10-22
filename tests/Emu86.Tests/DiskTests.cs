using Emu86;
using System.Security.Cryptography;
using static Emu86.Fields;

static class DiskTests
{
    static int assertions;

    static void Equal<T>(T expected, T actual, string name)
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{name}: expected {expected}, got {actual}");
    }

    static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { assertions++; return; }
        throw new Exception(name + ": expected " + typeof(T).Name);
    }

    static void SmallRawDisk()
    {
        const string basePath = "small.vhd";
        const string overlayPath = "small.avhdx";
        using (var file = File.Create(basePath)) file.SetLength(16 * 1024 * 1024);
        DiskImage.EnsureOverlay(overlayPath, basePath);
        var expected = Enumerable.Repeat((byte)0x5A, DiskImage.SectorSize).ToArray();
        using (var disk = new DiskImage(overlayPath, writable: true)) disk.WriteSector(0, expected, 0);
        using var reopened = new DiskImage(overlayPath);
        var actual = new byte[DiskImage.SectorSize];
        reopened.ReadSector(0, actual, 0);
        Equal(true, expected.AsSpan().SequenceEqual(actual), "small disk write/reopen differs");
    }

    static void Layouts()
    {
        (long Size, long Blocks, int Dynamic, int Differencing)[] cases =
        [
            (512, 1, 1, 2049),
            (16L << 20, 8, 8, 2049),
            ((1L << 32) - 512, 2048, 2048, 2049),
            (1L << 32, 2048, 2048, 2049),
            ((1L << 32) + 512, 2049, 2050, 4098),
            (2L << 32, 4096, 4097, 4098),
            ((63L << 32) + (2 << 20), 129025, 129088, 131136)
        ];
        foreach (var item in cases)
        {
            var differencing = new VhdxBatLayout(item.Size, 0x200000, true);
            var dynamic = new VhdxBatLayout(item.Size, 0x200000, false);
            Equal(item.Blocks, differencing.PayloadBlockCount, "payload block count");
            Equal(item.Differencing, differencing.EntryCount, "differencing BAT count");
            Equal(item.Dynamic, dynamic.EntryCount, "dynamic BAT count");
            Equal((long)item.Dynamic - 1, dynamic.PayloadIndex(item.Blocks - 1), "last dynamic entry is payload");
            Equal((long)item.Differencing - 1, differencing.BitmapIndex(differencing.ChunkIndex(item.Blocks - 1)), "last differencing entry is bitmap");
            Equal(true, differencing.RegionLength >= (long)item.Differencing * 8, "region contains entire BAT");
            Equal(0, differencing.RegionLength % 0x100000, "region alignment");
        }
        var layout = new VhdxBatLayout((1L << 32) + 512, 0x200000, true);
        Equal(2047L, layout.PayloadIndex(2047), "last payload before chunk boundary");
        Equal(2048L, layout.BitmapIndex(0), "first bitmap position");
        Equal(2049L, layout.PayloadIndex(2048), "first payload after chunk boundary");
        Equal(0L, layout.SectorInChunk(1L << 23), "bitmap bit resets at chunk boundary");
        Equal((1L << 23) - 1, layout.SectorInChunk((1L << 23) - 1), "last bitmap bit");
        Equal(4097, new VhdxBatLayout(512, 1 << 20, true).EntryCount, "one MB blocks");
        Equal(17, new VhdxBatLayout(512, 256 << 20, true).EntryCount, "256 MB blocks");
        Equal(2 << 20, new VhdxBatLayout((63L << 32) + (2 << 20), 0x200000, true).RegionLength, "partial chunk crosses BAT region boundary");
        foreach (var blockSize in new[] { 0, -1, 512, 3 << 20, 512 << 20 })
            Throws<InvalidDataException>(() => new VhdxBatLayout(16L << 20, blockSize, true), "invalid block size");
        foreach (var size in new long[] { 0, -512, 513, 1L << 62 })
            Throws<InvalidDataException>(() => new VhdxBatLayout(size, 0x200000, true), "invalid or unsupported disk size");
    }

    static void RoundTrips()
    {
        long[] sizes = [512, 16L << 20, (1L << 32) - 512, 1L << 32, (1L << 32) + 512, (2L << 32) + 512, (63L << 32) + (2 << 20)];
        for (var index = 0; index < sizes.Length; index++)
        {
            var basePath = $"base-{index}.vhdx";
            var overlayPath = $"overlay-{index}.avhdx";
            VhdxFixture.CreateDynamic(basePath, sizes[index]);
            var original = SHA256.HashData(File.ReadAllBytes(basePath));
            var totalSectors = sizes[index] / DiskImage.SectorSize;
            using (var parent = new DiskImage(basePath))
            {
                var sector = new byte[DiskImage.SectorSize];
                parent.ReadSector(totalSectors - 1, sector, 0);
                Equal(true, sector.All(value => value == VhdxFixture.PayloadValue), "last dynamic payload reads correctly");
            }
            DiskImage.EnsureOverlay(overlayPath, basePath);
            long[] boundaries = [0, 4095, 4096, (1L << 23) - 1, 1L << 23, totalSectors - 1];
            var sectors = boundaries.Where(lba => lba < totalSectors).Distinct().ToArray();
            using (var disk = new DiskImage(overlayPath, writable: true))
            {
                Equal(totalSectors, disk.TotalSectors, "overlay geometry matches base");
                var inherited = new byte[DiskImage.SectorSize];
                disk.ReadSector(totalSectors - 1, inherited, 0);
                Equal(true, inherited.All(value => value == VhdxFixture.PayloadValue), "unallocated payload inherits from parent");
                for (var sectorIndex = 0; sectorIndex < sectors.Length; sectorIndex++)
                    disk.WriteSector(sectors[sectorIndex], Enumerable.Repeat((byte)(0x80 + sectorIndex), DiskImage.SectorSize).ToArray(), 0);
            }
            using (var disk = new DiskImage(overlayPath))
            {
                for (var sectorIndex = 0; sectorIndex < sectors.Length; sectorIndex++)
                {
                    var actual = new byte[DiskImage.SectorSize];
                    disk.ReadSector(sectors[sectorIndex], actual, 0);
                    Equal(true, actual.All(value => value == 0x80 + sectorIndex), "persisted boundary sector");
                }
                if (totalSectors > 2)
                {
                    var neighbor = totalSectors - 2;
                    while (sectors.Contains(neighbor)) neighbor--;
                    var inherited = new byte[DiskImage.SectorSize];
                    var parentData = new byte[DiskImage.SectorSize];
                    using var parent = new DiskImage(basePath);
                    parent.ReadSector(neighbor, parentData, 0);
                    disk.ReadSector(neighbor, inherited, 0);
                    Equal(true, inherited.AsSpan().SequenceEqual(parentData), "clear bitmap bit still reads parent");
                }
            }
            Equal(true, original.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(basePath))), "writes preserve base file");
        }
    }

    static void InvalidRegions()
    {
        const string basePath = "large-base.vhdx";
        const string overlayPath = "invalid-region.avhdx";
        VhdxFixture.CreateDynamic(basePath, (63L << 32) + (2 << 20));
        DiskImage.EnsureOverlay(overlayPath, basePath);
        VhdxFixture.SetBatRegionLength(overlayPath, 0x100000);
        Throws<InvalidDataException>(() => { using var disk = new DiskImage(overlayPath); }, "undersized BAT region rejected");
        using (var exclusive = new FileStream(overlayPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Equal(true, exclusive.CanWrite, "invalid BAT releases file handle");

        DiskImage.EnsureOverlay("truncated.avhdx", "small.vhd");
        using (var file = new FileStream("truncated.avhdx", FileMode.Open, FileAccess.Write))
            file.SetLength(VhdxFixture.BatOffset + 2049 * 8 - 1);
        Throws<InvalidDataException>(() => { using var disk = new DiskImage("truncated.avhdx"); }, "truncated BAT entries rejected");
        Throws<InvalidOperationException>(() => { using var disk = new DiskImage(basePath, writable: true); }, "direct base writes rejected");
    }

    // バイト列のパーサコンビネータ: 識別子(Magic)、構造体の先頭からの位置(At)、繰り返し(Count)、開始位置、範囲外。
    static void FieldParsers()
    {
        byte[] buffer = [.. "tbl!"u8, 2, 0, 0, 0, 0x11, 0x22, 0x33, 0x44, 0xAA, 0xBB, 0xCC, 0xDD, 0x01, 0x02];
        var table =
            from _ in Magic("tbl!"u8.ToArray(), "test table")
            from count in At(4, U32)
            from items in At(8, Count((int)count, U32))
            select items;
        Equal("44332211,DDCCBBAA", string.Join(",", table.Parse(buffer).Select(value => value.ToString("X8"))), "little-endian items at a fixed position");
        Equal(0x11223344u, U32BE.Parse(buffer, 8), "big-endian at a start position");
        Equal(0x0201, (int)At(8, U16).Parse(buffer, 8), "At is relative to the start position");
        Equal("AABBCCDD", Convert.ToHexString((from _ in Skip(12) from bytes in Bytes(4) select bytes).Parse(buffer)), "Skip then Bytes");
        Throws<InvalidDataException>(() => Magic("xxxx"u8.ToArray(), "x").Parse(buffer), "magic mismatch");
        Throws<InvalidDataException>(() => Count(3, U64).Parse(buffer), "truncated structure");
    }

    // ATA のコマンド表: WRITE SECTORS で 1 セクタ書き、READ SECTORS で読み戻す。IDENTIFY の応答、非データコマンドの完了、
    // 未対応コマンドと IDENTIFY PACKET の abort、ステータス読み出しによる INTRQ の取り下げ。
    static void AtaCommands()
    {
        const string basePath = "ata.vhd";
        const string overlayPath = "ata.avhdx";
        using (var file = File.Create(basePath)) file.SetLength(16 * 1024 * 1024);
        DiskImage.EnsureOverlay(overlayPath, basePath);
        using var ata = new AtaDevice(new DiskImage(overlayPath, writable: true));
        void Issue(byte command, int count = 1, int lba = 5)
        {
            ata.WriteReg(0x1F2, (byte)count); ata.WriteReg(0x1F3, (byte)lba); ata.WriteReg(0x1F4, 0); ata.WriteReg(0x1F5, 0); ata.WriteReg(0x1F6, 0xE0);
            ata.WriteReg(0x1F7, command);
        }
        Issue(0x30);
        Equal((byte)0x58, ata.ReadReg(0x3F6), "WRITE SECTORS requests data (DRDY|DSC|DRQ) without INTRQ");
        Equal(false, ata.IrqPending, "no interrupt before the host writes the block");
        for (var word = 0; word < 256; word++) ata.WriteData(2, (uint)(0x1234 + word));
        Equal((true, (byte)0x50), (ata.IrqPending, ata.ReadReg(0x3F6)), "block written: INTRQ and transfer complete");
        Issue(0x20);
        Equal((true, (byte)0x58), (ata.IrqPending, ata.ReadReg(0x3F6)), "READ SECTORS prepares data with INTRQ");
        Equal(true, Enumerable.Range(0, 256).All(word => ata.ReadData(2) == (uint)(0x1234 + word)), "the sector reads back");
        Equal((byte)0x50, ata.ReadReg(0x1F7), "transfer complete; the status read drops INTRQ");
        Equal(false, ata.IrqPending, "INTRQ dropped by the status register read");
        Issue(0xEC);
        Equal(0x0040u, ata.ReadData(2), "IDENTIFY word 0: ATA device");
        for (var word = 1; word < 60; word++) ata.ReadData(2);
        Equal(16 * 1024 * 1024 / 512u, ata.ReadData(4), "IDENTIFY words 60-61: LBA sectors");
        Issue(0xEF);
        Equal((true, (byte)0x50), (ata.IrqPending, ata.ReadReg(0x3F6)), "SET FEATURES completes");
        Issue(0xC8);
        Equal(((byte)0x51, (byte)0x04), (ata.ReadReg(0x3F6), ata.ReadReg(0x1F1)), "an unsupported (DMA) command aborts");
        Issue(0xA1);
        Equal(((byte)0x51, (byte)0x04), (ata.ReadReg(0x3F6), ata.ReadReg(0x1F1)), "IDENTIFY PACKET DEVICE aborts");
    }

    public static void Run()
    {
        FieldParsers(); SmallRawDisk(); Layouts(); RoundTrips(); InvalidRegions(); AtaCommands();
        Console.WriteLine($"Disk checks passed: {assertions} assertions");
    }
}
