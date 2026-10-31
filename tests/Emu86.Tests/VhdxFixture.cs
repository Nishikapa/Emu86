using static Emu86.Writers;

// 読み込みに必要なリージョン表・メタデータ・BAT と末尾のペイロードだけを持つ最小限の人工 VHDX(ヘッダの CRC は検証されない)。
// BAT の期待値は本体の VhdxBatLayout を使わずに計算する。
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
        (Guid Id, byte[] Data)[] items =
        [
            (new Guid("CAA16737-FA36-4D43-B3B6-33F0AA44E76B"), Sequence(U32((uint)blockSize), U32(0)).Build(8)), // File Parameters(親なし)
            (new Guid("2FA54224-CD1B-4876-B211-5DBED83BF4B8"), I64(virtualSize).Build(8)),                        // Virtual Disk Size
            (new Guid("8141BF1D-A96F-4709-BA47-F233A8FAAB5F"), U32(512).Build(4))                                  // Logical Sector Size
        ];
        var image = Sequence(
            Magic("vhdxfile"),
            At(0x30000, Sequence(
                Magic("regi"),
                At(8, U32(2)),
                At(16, Sequence(Uuid(new Guid("2DC27766-F623-4200-9D64-115E9BFD4A08")), I64(BatOffset), U32((uint)batLength))),
                At(48, Sequence(Uuid(new Guid("8B7CA206-4790-4B9A-B8FE-575F050F886E")), I64(0x200000), U32(0x100000))))),
            At(0x200000, Sequence(
                Magic("metadata"),
                At(10, U16((ushort)items.Length)),
                At(32, Count(items.Index(), item => Sequence(Uuid(item.Item.Id), U32((uint)(0x10000 + item.Index * 16)), U32((uint)item.Item.Data.Length), Skip(8)))),
                Sequence(items.Index().Select(item => At(0x10000 + item.Index * 16, Bytes(item.Item.Data)))))),
            At(BatOffset + (int)lastEntry * 8, U64((ulong)(BatOffset + batLength) | 6)))
            .Build(BatOffset + batLength);
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
