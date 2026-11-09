using System.Buffers.Binary;
using System.Text;
using static Emu86.Writers;

namespace Emu86;

// バイト列の構造を書くビルダー(Field<T> の対)。buffer の pos に書いて次の位置を返す。順次は Then/Sequence/Count、
// 固定オフセットの項目は At(位置は進めない)で、読み取り側のレイアウトと同じ形に書ける。構造体全体の大きさは Build(size) で決める。
// ディスクの形式のほか、固定レイアウトのバイト列はどれもこれで書く: VGA BIOS の ROM と BIOS データ領域(VgaBiosStub)、
// PCI コンフィグ空間(Pci)、ATA の IDENTIFY の応答(AtaDevice)。
internal delegate int Writer(byte[] buffer, int pos);

internal static class Writers
{
    static Writer Primitive(int size, Action<Span<byte>> write) => (buffer, pos) =>
    {
        write(buffer.AsSpan(pos, size));
        return pos + size;
    };

    public static Writer U8(byte value) => Primitive(1, span => span[0] = value);
    public static Writer U16(ushort value) => Primitive(2, span => BinaryPrimitives.WriteUInt16LittleEndian(span, value));
    public static Writer U32(uint value) => Primitive(4, span => BinaryPrimitives.WriteUInt32LittleEndian(span, value));
    public static Writer U64(ulong value) => Primitive(8, span => BinaryPrimitives.WriteUInt64LittleEndian(span, value));
    public static Writer I64(long value) => Primitive(8, span => BinaryPrimitives.WriteInt64LittleEndian(span, value));
    public static Writer U16BE(ushort value) => Primitive(2, span => BinaryPrimitives.WriteUInt16BigEndian(span, value));
    public static Writer U32BE(uint value) => Primitive(4, span => BinaryPrimitives.WriteUInt32BigEndian(span, value));
    public static Writer U64BE(ulong value) => Primitive(8, span => BinaryPrimitives.WriteUInt64BigEndian(span, value));
    public static Writer Uuid(Guid value) => Primitive(16, span => value.TryWriteBytes(span));
    public static Writer Bytes(params byte[] value) => Primitive(value.Length, span => value.CopyTo(span));
    public static Writer Ascii(string value) => Bytes(Encoding.ASCII.GetBytes(value));
    public static Writer Utf16(string value) => Bytes(Encoding.Unicode.GetBytes(value));

    // 先頭の識別子(位置は進めない: 続く At は構造体の先頭からの位置。Fields.Magic の対)。
    public static Writer Magic(string ascii) => At(0, Ascii(ascii));

    // 構造体の先頭(現在位置)から offset の位置に書く(位置は進めない)。
    public static Writer At(int offset, Writer writer) => (buffer, pos) =>
    {
        writer(buffer, pos + offset);
        return pos;
    };

    public static Writer Skip(int count) => (_, pos) => pos + count;

    public static Writer Then(this Writer first, Writer second) => (buffer, pos) => second(buffer, first(buffer, pos));

    // 順に書く(At を使った項目は位置を進めないので、固定オフセットの構造体はこれで組む)。
    public static Writer Sequence(params Writer[] writers) => Sequence((IEnumerable<Writer>)writers);

    public static Writer Sequence(IEnumerable<Writer> writers) => (buffer, pos) => writers.Aggregate(pos, (next, writer) => writer(buffer, next));

    // 各要素を順に書く。
    public static Writer Count<T>(IEnumerable<T> items, Func<T, Writer> each) => Sequence(items.Select(each));

    // 構造体の先頭から length バイトのチェックサムを offset に書く(他の項目をすべて書いた後、チェックサム欄が 0 のまま計算する)。
    public static Writer Checksum(int offset, int length, Func<ReadOnlySpan<byte>, uint> checksum) => (buffer, pos) =>
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + offset, 4), checksum(buffer.AsSpan(pos, length)));
        return pos;
    };

    public static byte[] Build(this Writer writer, int size)
    {
        var buffer = new byte[size];
        writer(buffer, 0);
        return buffer;
    }
}

// ディスクイメージの形式の記述(差分 VHDX の書き込み側)。
public partial class DiskImage
{
    // 版数を上げると、古い版で作られた overlay は自動的に作り直される。
    const string OverlayCreator = "Emu86-avhdx-1";

    // 新しい空の VHDX(--create-disk)の Creator。
    const string DiskCreator = "Emu86";

    // File Identifier(0): "vhdxfile" と Creator(overlay はフォーマット版数を埋め、旧版 overlay を判別できるようにする)。
    static Writer VhdxFileIdentifier(string creator) => Magic("vhdxfile").Then(At(8, Utf16(creator)));

    // ヘッダ(0x10000 と 0x20000、4096 バイト): 連番、FileWriteGuid、DataWriteGuid、LogGuid = 0(クリーン)、版 1、
    // ログ領域(0x100000 から 1MB)、CRC-32C。2 つのヘッダは連番以外同じ。
    static Writer VhdxHeader(ulong sequence, Guid fileWriteGuid, Guid dataWriteGuid) => Sequence(
        Magic("head"),
        At(8, U64(sequence)),
        At(0x10, Uuid(fileWriteGuid)),
        At(0x20, Uuid(dataWriteGuid)),
        At(0x42, U16(1)),
        At(0x44, U32(0x100000)),
        At(0x48, U64(0x100000)),
        Checksum(4, 4096, Crc32C));

    // このファイルが作る VHDX の BAT の位置(メタデータ領域の直後)。
    const long VhdxBatPosition = 0x300000;

    // リージョン表(0x30000 と 0x40000、64KB): "regi"、項数、(GUID、位置、長さ、必須)× 2、CRC-32C。BAT は 0x300000、メタデータは 0x200000 から 1MB。
    static Writer VhdxRegionEntry(Guid guid, long offset, uint length) => Sequence(Uuid(guid), I64(offset), U32(length), U32(1));

    static Writer VhdxRegionTable(uint batLength) => Sequence(
        Magic("regi"),
        At(8, U32(2)),
        At(16, Sequence(VhdxRegionEntry(BatRegionGuid, VhdxBatPosition, batLength), VhdxRegionEntry(MetaRegionGuid, 0x200000, 0x100000))),
        Checksum(4, 0x10000, Crc32C));

    // メタデータ表: "metadata"、項数(offset 10)、(GUID、領域内の位置、長さ、フラグ)× 項数(offset 32 から)。
    // 実データは領域先頭から 0x10000 以降に 16 バイト境界で順に詰める。
    static Writer VhdxMetadataTable(IReadOnlyList<(Guid Id, byte[] Data, uint Flags)> items)
    {
        var placed = new List<(Guid Id, byte[] Data, uint Flags, int Offset)>();
        var offset = 0x10000;
        foreach (var (id, data, flags) in items)
        {
            placed.Add((id, data, flags, offset));
            offset += (data.Length + 15) & ~15;
        }
        return Sequence(
            Magic("metadata"),
            At(10, U16((ushort)items.Count)),
            At(32, Count(placed, item => Sequence(Uuid(item.Id), U32((uint)item.Offset), U32((uint)item.Data.Length), U32(item.Flags), Skip(4)))),
            Sequence(placed.Select(item => At(item.Offset, Bytes(item.Data)))));
    }

    // 仮想ディスクのメタデータ項目: File Parameters(ブロックサイズ、HasParent = bit1)、仮想サイズ、論理/物理セクタ長、Page 83 の ID。
    static IReadOnlyList<(Guid Id, byte[] Data, uint Flags)> VhdxDiskMetadata(long virtualSize, int blockSize, bool hasParent) =>
    [
        (FileParamsGuid, Sequence(U32((uint)blockSize), U32(hasParent ? 2u : 0u)).Build(8), 0x04),
        (DiskSizeGuid, I64(virtualSize).Build(8), 0x06),
        (LogicalSectorGuid, U32(SectorSize).Build(4), 0x06),
        (PhysicalSectorGuid, U32(4096).Build(4), 0x06),
        (Page83Guid, Uuid(Guid.NewGuid()).Build(16), 0x06),
    ];

    // 差分 VHDX のメタデータ項目: 仮想ディスクの項目(HasParent 付き)とペアレントロケータ。
    static IReadOnlyList<(Guid Id, byte[] Data, uint Flags)> VhdxOverlayMetadata(string path, string parentPath, long virtualSize, int blockSize, Guid parentDataWriteGuid) =>
    [
        .. VhdxDiskMetadata(virtualSize, blockSize, hasParent: true),
        (ParentLocatorGuid, VhdxParentLocatorData(VhdxParentLocatorPairs(parentPath, path, parentDataWriteGuid)), 0x04),
    ];

    // ペアレントロケータ項目: 種別 GUID、キー/値の数(offset 18)、(キー位置、値位置、キー長、値長)× 数(offset 20 から)、続けて UTF-16 の文字列。
    static byte[] VhdxParentLocatorData(IReadOnlyList<(string Key, string Value)> pairs)
    {
        var placed = new List<(string Key, string Value, int KeyOffset, int ValueOffset)>();
        var offset = 20 + pairs.Count * 12;
        foreach (var (key, value) in pairs)
        {
            placed.Add((key, value, offset, offset + key.Length * 2));
            offset += (key.Length + value.Length) * 2;
        }
        return Sequence(
            At(0, Uuid(VhdxParentLocatorType)),
            At(18, U16((ushort)pairs.Count)),
            At(20, Count(placed, pair => Sequence(U32((uint)pair.KeyOffset), U32((uint)pair.ValueOffset), U16((ushort)(pair.Key.Length * 2)), U16((ushort)(pair.Value.Length * 2))))),
            Sequence(placed.Select(pair => At(pair.KeyOffset, Utf16(pair.Key)).Then(At(pair.ValueOffset, Utf16(pair.Value))))))
            .Build(offset);
    }

    // parent_linkage(親の DataWriteGuid)、relative_path(子から見た相対パス、Windows 区切り)、absolute_win32_path。
    static IReadOnlyList<(string Key, string Value)> VhdxParentLocatorPairs(string parentPath, string childPath, Guid parentDataWriteGuid)
    {
        var childDirectory = Path.GetDirectoryName(Path.GetFullPath(childPath)) ?? ".";
        var relative = Path.GetRelativePath(childDirectory, Path.GetFullPath(parentPath)).Replace(Path.DirectorySeparatorChar, '\\');
        if (!relative.StartsWith('.') && !relative.Contains(':'))
            relative = ".\\" + relative;
        return
        [
            ("parent_linkage", parentDataWriteGuid.ToString("B").ToUpperInvariant()),
            ("relative_path", relative),
            ("absolute_win32_path", Path.GetFullPath(parentPath)),
        ];
    }

    // VHDX のファイル全体(ブロックはまだ 1 つも割り当てていない)。
    // 構造: FileID / Header x2 / RegionTable x2 / Log(1MB、空) / Metadata(1MB) / BAT(全エントリ 0 = 未割り当て)。
    static byte[] VhdxImage(string creator, int batLength, IReadOnlyList<(Guid Id, byte[] Data, uint Flags)> metadata)
    {
        var fileWriteGuid = Guid.NewGuid();
        var dataWriteGuid = Guid.NewGuid();
        return Sequence(
            VhdxFileIdentifier(creator),
            At(0x10000, VhdxHeader(1, fileWriteGuid, dataWriteGuid)),
            At(0x20000, VhdxHeader(2, fileWriteGuid, dataWriteGuid)),
            At(0x30000, VhdxRegionTable((uint)batLength)),
            At(0x40000, VhdxRegionTable((uint)batLength)),
            At(0x200000, VhdxMetadataTable(metadata)))
            .Build(checked((int)VhdxBatPosition + batLength));
    }

    // 差分 VHDX を生成する(既存のファイルは置き換える)。
    static void CreateAvhdx(string path, string parentPath, long virtualSize, int blockSize, Guid parentDataWriteGuid)
    {
        var batLength = new VhdxBatLayout(virtualSize, blockSize, hasParent: true).RegionLength;
        File.WriteAllBytes(path, VhdxImage(OverlayCreator, batLength, VhdxOverlayMetadata(path, parentPath, virtualSize, blockSize, parentDataWriteGuid)));
    }

    // 新しい空の可変長 VHDX(親なし)を生成する。どのブロックも未割り当てなので、全セクタがゼロとして読める。
    // 書き込みはこのファイルを親にした差分(AVHDX)へ積まれる(--disk で指定する)。既存のファイルは上書きしない。
    public static void CreateVhdx(string path, long virtualSize, int blockSize = 0x200000)
    {
        var batLength = new VhdxBatLayout(virtualSize, blockSize, hasParent: false).RegionLength;
        var image = VhdxImage(DiskCreator, batLength, VhdxDiskMetadata(virtualSize, blockSize, hasParent: false));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.Write(image);
    }

    // 差分をたどって読める中身を、親を持たない新しい可変長 VHDX に書き出す(--merge-disk: 差分に積まれた書き込みを 1 つのイメージにまとめる)。
    // チェーンのどのイメージもブロックを割り当てていない範囲と、読んだ結果が全部ゼロのブロックは割り当てない(ゼロとして読める)。
    // 割り当てるブロックは全セクタを書いて FULLY_PRESENT にし、ヘッダ・メタデータ・BAT の後ろへ順に置く。
    // 既存のファイルは上書きしない。元のイメージは変えない。書いたブロックの数を返す。
    public long MergeInto(string path, int blockSize = 0x200000)
    {
        var virtualSize = TotalSectors * SectorSize;
        CreateVhdx(path, virtualSize, blockSize);
        var layout = new VhdxBatLayout(virtualSize, blockSize, hasParent: false);
        var sectors = blockSize / SectorSize;
        var data = new byte[blockSize];
        var bat = new byte[layout.EntryCount * sizeof(ulong)];
        using var target = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var next = target.Length;   // BAT の直後(1MB 境界)
        var written = 0L;
        for (var block = 0L; block < layout.PayloadBlockCount; block++)
        {
            var first = block * sectors;
            var count = (int)Math.Min(sectors, TotalSectors - first);
            if (!MayHoldData(first, count)) continue;
            Array.Clear(data);
            for (var sector = 0; sector < count; sector++) ReadSector(first + sector, data, sector * SectorSize);
            if (data.AsSpan().IndexOfAnyExcept((byte)0) < 0) continue;
            target.Seek(next, SeekOrigin.Begin);
            target.Write(data);
            BinaryPrimitives.WriteUInt64LittleEndian(bat.AsSpan((int)layout.PayloadIndex(block) * sizeof(ulong)), (ulong)next | PayloadFullyPresent);
            next += blockSize;
            written++;
        }
        target.Seek(VhdxBatPosition, SeekOrigin.Begin);
        target.Write(bat);
        target.Flush(flushToDisk: true);
        return written;
    }

    // この範囲に、チェーンのどれかのイメージがブロックを割り当てているか(読まずに飛ばすための粗い判定。生イメージは常に真)。
    bool MayHoldData(long lba, long count) => OwnBlocksAllocated(lba, count) || (parent?.MayHoldData(lba, count) ?? false);

    bool OwnBlocksAllocated(long lba, long count) =>
        lba < TotalSectors && (!vhd && !vhdx
            || BlocksOf(lba, Math.Min(count, TotalSectors - lba)).Any(block => vhd
                ? vhdBat[block] != VhdBatUnallocated
                : (int)(vhdxBat[vhdxLayout.PayloadIndex(block)] & 7) is PayloadFullyPresent or PayloadPartiallyPresent));

    // セクタの範囲にかかるブロックの番号。
    IEnumerable<long> BlocksOf(long lba, long count) =>
        Enumerable.Range(0, (int)((lba + count - 1) / sectorsPerBlock - lba / sectorsPerBlock + 1)).Select(offset => lba / sectorsPerBlock + offset);
}
