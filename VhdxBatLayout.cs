namespace Emu86;

internal readonly struct VhdxBatLayout
{
    const int Megabyte = 1 << 20;
    const int SectorsPerChunk = 1 << 23;

    public int ChunkRatio { get; }
    public long PayloadBlockCount { get; }
    public int EntryCount { get; }
    public int RegionLength { get; }
    public bool HasParent { get; }

    public VhdxBatLayout(long virtualSize, int blockSize, bool hasParent)
    {
        if (virtualSize <= 0 || virtualSize % DiskImage.SectorSize != 0)
            throw new InvalidDataException("VHDX: invalid virtual disk size");
        if (blockSize < Megabyte || blockSize > 256 * Megabyte || (blockSize & (blockSize - 1)) != 0)
            throw new InvalidDataException("VHDX: invalid block size");
        HasParent = hasParent;
        ChunkRatio = (int)((long)SectorsPerChunk * DiskImage.SectorSize / blockSize);
        PayloadBlockCount = (virtualSize - 1) / blockSize + 1;
        var chunks = (PayloadBlockCount - 1) / ChunkRatio + 1;
        var entries = hasParent ? chunks * (ChunkRatio + 1) : PayloadBlockCount + (PayloadBlockCount - 1) / ChunkRatio;
        var length = (entries * sizeof(ulong) + Megabyte - 1) & ~(Megabyte - 1L);
        if (length > int.MaxValue) throw new InvalidDataException("VHDX: BAT is too large");
        EntryCount = (int)entries;
        RegionLength = (int)length;
    }

    public long PayloadIndex(long block) => block + ChunkIndex(block);

    public long ChunkIndex(long block) => block / ChunkRatio;

    public long BitmapIndex(long chunk)
    {
        if (!HasParent) throw new InvalidDataException("VHDX: non-differencing disk has a partial payload block");
        return chunk * (ChunkRatio + 1) + ChunkRatio;
    }

    public long SectorInChunk(long lba) => lba % SectorsPerChunk;
}
