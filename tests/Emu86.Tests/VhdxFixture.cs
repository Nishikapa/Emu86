using System.Buffers.Binary;

static class VhdxFixture
{
    public const int BatOffset = 0x300000;
    public const byte PayloadValue = 0x37;

    public static void CreateDynamic(string path, long virtualSize, int blockSize = 0x200000)
    {
        var payloadBlocks = (virtualSize - 1) / blockSize + 1;
        var blocksPerChunk = (1L << 32) / blockSize;
        var lastEntry = payloadBlocks - 1 + (payloadBlocks - 1) / blocksPerChunk;
        var batLength = (int)(((lastEntry + 1) * 8 + 0xFFFFF) & ~0xFFFFFL);
        var image = new byte[BatOffset + batLength];
        "vhdxfile"u8.CopyTo(image);
        var regions = image.AsSpan(0x30000, 0x10000);
        "regi"u8.CopyTo(regions);
        BinaryPrimitives.WriteUInt32LittleEndian(regions[8..], 2);
        new Guid("2DC27766-F623-4200-9D64-115E9BFD4A08").TryWriteBytes(regions[16..]);
        BinaryPrimitives.WriteInt64LittleEndian(regions[32..], BatOffset);
        BinaryPrimitives.WriteInt32LittleEndian(regions[40..], batLength);
        new Guid("8B7CA206-4790-4B9A-B8FE-575F050F886E").TryWriteBytes(regions[48..]);
        BinaryPrimitives.WriteInt64LittleEndian(regions[64..], 0x200000);
        BinaryPrimitives.WriteInt32LittleEndian(regions[72..], 0x100000);
        var metadata = image.AsSpan(0x200000, 0x100000);
        "metadata"u8.CopyTo(metadata);
        BinaryPrimitives.WriteUInt16LittleEndian(metadata[10..], 3);
        var offset = 0x10000;
        (Guid Id, byte[] Data)[] items =
        [
            (new Guid("CAA16737-FA36-4D43-B3B6-33F0AA44E76B"), BitConverter.GetBytes((long)blockSize)),
            (new Guid("2FA54224-CD1B-4876-B211-5DBED83BF4B8"), BitConverter.GetBytes(virtualSize)),
            (new Guid("8141BF1D-A96F-4709-BA47-F233A8FAAB5F"), BitConverter.GetBytes(512))
        ];
        for (var index = 0; index < items.Length; index++)
        {
            var entry = metadata[(32 + index * 32)..];
            items[index].Id.TryWriteBytes(entry);
            BinaryPrimitives.WriteInt32LittleEndian(entry[16..], offset);
            BinaryPrimitives.WriteInt32LittleEndian(entry[20..], items[index].Data.Length);
            items[index].Data.CopyTo(metadata[offset..]);
            offset += 16;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(BatOffset + (int)lastEntry * 8), (ulong)image.Length | 6);
        using var file = File.Create(path);
        file.Write(image);
        file.Write(Enumerable.Repeat(PayloadValue, blockSize).ToArray());
    }

    public static void SetBatRegionLength(string path, int length)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
        file.Position = 0x30000 + 40;
        file.Write(BitConverter.GetBytes(length));
    }
}
