using Emu86;
using System.Security.Cryptography;
using static Emu86.Fields;
using W = Emu86.Writers;
using static Assert;

static class DiskTests
{
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

    // 書き込み側のビルダー: 固定位置と順次、繰り返し、チェックサム、Fields との往復。
    static void WriterCombinators()
    {
        var table = W.Sequence(W.Magic("tbl!"), W.At(4, W.U32(2)), W.At(8, W.Count(new uint[] { 0x44332211, 0xDDCCBBAA }, W.U32)), W.At(16, W.U16(0x0201))).Build(18);
        Equal("74626C210200000011223344AABBCCDD0102", Convert.ToHexString(table), "magic, fixed positions and repetition");
        var parsed = (from _ in Magic("tbl!"u8.ToArray(), "test table") from count in At(4, U32) from items in At(8, Count((int)count, U32)) select items).Parse(table);
        Equal("44332211,DDCCBBAA", string.Join(",", parsed.Select(value => value.ToString("X8"))), "Fields read back what Writers wrote");
        Equal("0102000003", Convert.ToHexString(W.U8(1).Then(W.U8(2)).Then(W.Skip(2)).Then(W.U8(3)).Build(5)), "Then advances, Skip leaves zeros");
        Equal("1122334408000000", Convert.ToHexString(W.Sequence(W.At(0, W.U32BE(0x11223344)), W.Checksum(4, 8, data => (uint)data.Length)).Build(8)), "checksum covers the structure with its field zeroed");
        var id = Guid.NewGuid();
        Equal((id, "ab"), (from guid in Uuid from text in Utf16(4) select (guid, text)).Parse(W.Sequence(W.Uuid(id), W.Utf16("ab")).Build(20)), "GUID and UTF-16 round trip");
        Throws<ArgumentOutOfRangeException>(() => W.U32(1).Build(2), "writing past the buffer");
    }

    // 生成した差分 VHDX の構造: 2 つのヘッダの CRC と連番と共通の GUID、リージョン表の 2 項と複製、メタデータ 6 項(16 バイト境界に順に詰める)、
    // File Parameters の親フラグ、ペアレントロケータの 3 組。
    static void OverlayStructure()
    {
        VhdxFixture.CreateDynamic("structure-base.vhdx", 16L << 20);
        DiskImage.EnsureOverlay("structure.avhdx", "structure-base.vhdx");
        var bytes = File.ReadAllBytes("structure.avhdx");
        Equal("vhdxfile", System.Text.Encoding.ASCII.GetString(bytes, 0, 8), "file identifier");
        foreach (var headerOffset in new[] { 0x10000, 0x20000 })
        {
            var header = bytes.AsSpan(headerOffset, 4096).ToArray();
            var stored = U32.Parse(header, 4);
            header.AsSpan(4, 4).Clear();
            Equal(stored, DiskImage.Crc32C(header), $"header checksum at {headerOffset:x}");
            Equal((ulong)(headerOffset >> 16), U64.Parse(header, 8), "header sequence number");
            Equal((ushort)1, U16.Parse(header, 0x42), "header version");
        }
        Equal(Uuid.Parse(bytes, 0x10010), Uuid.Parse(bytes, 0x20010), "headers share the FileWriteGuid");
        Equal(Uuid.Parse(bytes, 0x10020), Uuid.Parse(bytes, 0x20020), "headers share the DataWriteGuid");
        var regions = (from _ in Magic("regi"u8.ToArray(), "regions") from count in At(8, U32)
                       from entries in At(16, Count((int)count, from guid in Uuid from offset in I64 from length in U32 from required in U32 select (guid, offset, length, required)))
                       select entries).Parse(bytes, 0x30000);
        Equal("2DC27766-F623-4200-9D64-115E9BFD4A08@300000,8B7CA206-4790-4B9A-B8FE-575F050F886E@200000",
            string.Join(",", regions.Select(region => $"{region.guid.ToString().ToUpperInvariant()}@{region.offset:x}")), "region entries");
        Equal(true, regions.All(region => region.required == 1) && regions[1].length == 0x100000, "regions are required and the metadata region is 1MB");
        Equal(true, bytes.AsSpan(0x30000, 0x10000).SequenceEqual(bytes.AsSpan(0x40000, 0x10000)), "region table copies match");
        var metadata = (from _ in Magic("metadata"u8.ToArray(), "metadata") from count in At(10, U16)
                        from entries in At(32, Count(count, from guid in Uuid from offset in U32 from length in U32 from flags in U32 from __ in Skip(4) select (guid, offset, length, flags)))
                        select entries).Parse(bytes, 0x200000);
        Equal(6, metadata.Count, "metadata entries");
        Equal(0x10000u, metadata[0].offset, "first item follows the table area");
        Equal(true, metadata.Zip(metadata.Skip(1)).All(pair => pair.Second.offset == pair.First.offset + ((pair.First.length + 15) & ~15u)), "items are packed in order on 16-byte boundaries");
        Equal(2u, U32.Parse(bytes, 0x200000 + (int)metadata[0].offset + 4), "file parameters mark a parent");
        var locator = metadata[5];
        var pairs = (from count in At(18, U16) from entries in At(20, Count(count, from keyOffset in U32 from valueOffset in U32 from keyLength in U16 from valueLength in U16 select (keyOffset, valueOffset, keyLength, valueLength)))
                     from texts in Sequence(entries.Select(entry => from key in At((int)entry.keyOffset, Utf16(entry.keyLength)) from value in At((int)entry.valueOffset, Utf16(entry.valueLength)) select (key, value)))
                     select texts).Parse(bytes, 0x200000 + (int)locator.offset);
        Equal("parent_linkage,relative_path,absolute_win32_path", string.Join(",", pairs.Select(pair => pair.key)), "parent locator keys");
        Equal(Path.GetFullPath("structure-base.vhdx"), pairs[2].value, "absolute parent path");
        Equal(".\\structure-base.vhdx", pairs[1].value, "relative parent path");
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

    // 名前付きのケース(ランナーが順に実行し、失敗したケース名を報告する)。
    public static (string Name, Action Run)[] Cases =>
    [
        ("field-parsers", FieldParsers),
        ("writers", WriterCombinators),
        ("overlay-structure", OverlayStructure),
        ("small-raw-disk", SmallRawDisk),
        ("layouts", Layouts),
        ("round-trips", RoundTrips),
        ("invalid-regions", InvalidRegions),
        ("ata-commands", AtaCommands),
    ];
}
