using System.Buffers.Binary;
using System.Text;
using static Emu86.Fields;

namespace Emu86;

// バイト列の構造(ディスクイメージのヘッダや表)を読むパーサコンビネータ。buffer の pos から読み、値と次の位置を返す。
// 形式の不正(範囲外、マジックの不一致)は InvalidDataException。固定オフセットの構造体は At で順不同に書ける。
internal delegate (T value, int next) Field<T>(byte[] buffer, int pos);

internal static class Fields
{
    public static Field<T> Return<T>(T value) => (_, pos) => (value, pos);

    public static Field<B> Select<A, B>(this Field<A> field, Func<A, B> selector) => (buffer, pos) =>
    {
        var (value, next) = field(buffer, pos);
        return (selector(value), next);
    };

    public static Field<C> SelectMany<A, B, C>(this Field<A> field, Func<A, Field<B>> selector, Func<A, B, C> projector) => (buffer, pos) =>
    {
        var (a, next) = field(buffer, pos);
        var (b, next2) = selector(a)(buffer, next);
        return (projector(a, b), next2);
    };

    static Field<T> Primitive<T>(int size, Func<byte[], int, T> read) => (buffer, pos) =>
        pos >= 0 && pos + size <= buffer.Length ? (read(buffer, pos), pos + size) : throw new InvalidDataException("structure is truncated");

    public static readonly Field<byte> U8 = Primitive(1, (b, p) => b[p]);
    public static readonly Field<ushort> U16 = Primitive(2, (b, p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)));
    public static readonly Field<uint> U32 = Primitive(4, (b, p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)));
    public static readonly Field<ulong> U64 = Primitive(8, (b, p) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)));
    public static readonly Field<long> I64 = Primitive(8, (b, p) => BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p)));
    public static readonly Field<uint> U32BE = Primitive(4, (b, p) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)));
    public static readonly Field<ulong> U64BE = Primitive(8, (b, p) => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p)));
    public static readonly Field<Guid> Uuid = Primitive(16, (b, p) => new Guid(b.AsSpan(p, 16)));
    public static Field<byte[]> Bytes(int count) => Primitive(count, (b, p) => b.AsSpan(p, count).ToArray());
    public static Field<string> Utf16(int bytes, bool bigEndian = false) =>
        Primitive(bytes, (b, p) => (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(b, p, bytes));

    // 構造体の先頭(現在位置)から offset の位置を読む(位置は進めない)。
    public static Field<T> At<T>(int offset, Field<T> field) => (buffer, pos) => (field(buffer, pos + offset).value, pos);

    public static Field<Unit> Skip(int count) => (_, pos) => (Unit.unit, pos + count);

    // count 回続けて読む。
    public static Field<IReadOnlyList<T>> Count<T>(int count, Field<T> field) => (buffer, pos) =>
    {
        var values = new List<T>(count);
        for (var index = 0; index < count; index++)
        {
            var (value, next) = field(buffer, pos);
            values.Add(value);
            pos = next;
        }
        return (values, pos);
    };

    // それぞれを同じ位置から読む(At で位置を指定した読み取りの集まり)。
    public static Field<IReadOnlyList<T>> Sequence<T>(IEnumerable<Field<T>> fields) => (buffer, pos) =>
        (fields.Select(field => field(buffer, pos).value).ToList(), pos);

    // 先頭の識別子(位置は進めない: 続く At は構造体の先頭からの位置)。一致しなければ形式の不正。
    public static Field<Unit> Magic(byte[] expected, string what) => (buffer, pos) =>
        pos <= buffer.Length && buffer.AsSpan(pos).StartsWith(expected) ? (Unit.unit, pos) : throw new InvalidDataException($"{what} not found");

    public static T Parse<T>(this Field<T> field, byte[] buffer, int pos = 0) => field(buffer, pos).value;
}

// ディスクイメージの形式の記述(VHD と VHDX の読み取り側)。
public partial class DiskImage
{
    readonly record struct VhdFooter(int Type, long Size);
    readonly record struct VhdLocator(bool IsWindowsUnicode, int Length, long Offset);
    readonly record struct VhdDynamicHeader(long TableOffset, int MaxEntries, int BlockSize, string ParentName, IReadOnlyList<VhdLocator> Locators);
    readonly record struct VhdxRegion(Guid Guid, long Offset, uint Length);
    readonly record struct VhdxMetadataEntry(Guid Guid, int Offset, int Length);
    readonly record struct VhdxFileParameters(int BlockSize, bool HasParent);
    readonly record struct VhdxParentLocator(Guid Linkage, IReadOnlyList<string> Paths);

    // VHD フッタ(先頭のコピー、512 バイト、ビッグエンディアン): 種別(2 固定/3 可変長/4 差分)と仮想サイズ。
    static readonly Field<VhdFooter> VhdFooterLayout =
        from type in At(0x3C, U32BE)
        from size in At(0x30, U64BE)
        select new VhdFooter((int)type, (long)size);

    // VHD 動的ヘッダ(offset 512、1024 バイト): BAT の位置と大きさ、ブロックサイズ、親の名前と 8 個のロケータ。
    static readonly Field<VhdLocator> VhdLocatorLayout =
        from code in Bytes(4)
        from _1 in Skip(4)
        from length in U32BE
        from _2 in Skip(4)
        from offset in U64BE
        select new VhdLocator(code.AsSpan().SequenceEqual("W2ku"u8), (int)length, (long)offset);

    static readonly Field<VhdDynamicHeader> VhdDynamicHeaderLayout =
        from tableOffset in At(0x10, U64BE)
        from maxEntries in At(0x1C, U32BE)
        from blockSize in At(0x20, U32BE)
        from parentName in At(0x40, Utf16(0x200, bigEndian: true))
        from locators in At(0x240, Count(8, VhdLocatorLayout))
        select new VhdDynamicHeader((long)tableOffset, (int)maxEntries, (int)blockSize, parentName.TrimEnd('\0'), locators);

    // VHDX ヘッダ 1(0x10000): DataWriteGuid(親子リンクの検証用)。
    static readonly Field<Guid> VhdxDataWriteGuidLayout = At(0x20, Uuid);

    // VHDX リージョン表(0x30000): "regi"、項数、(GUID、位置、長さ、必須フラグ)× 項数。
    static readonly Field<VhdxRegion> VhdxRegionLayout =
        from guid in Uuid
        from offset in I64
        from length in U32
        from _ in Skip(4)
        select new VhdxRegion(guid, offset, length);

    static readonly Field<IReadOnlyList<VhdxRegion>> VhdxRegionTableLayout =
        from _ in Magic("regi"u8.ToArray(), "VHDX: region table")
        from count in At(8, U32)
        from regions in At(16, Count((int)count, VhdxRegionLayout))
        select regions;

    // VHDX メタデータ表: "metadata"、項数(offset 10)、(GUID、領域内の位置、長さ、フラグ)× 項数(offset 32 から)。
    static readonly Field<VhdxMetadataEntry> VhdxMetadataEntryLayout =
        from guid in Uuid
        from offset in U32
        from length in U32
        from _ in Skip(8)
        select new VhdxMetadataEntry(guid, (int)offset, (int)length);

    static readonly Field<IReadOnlyList<VhdxMetadataEntry>> VhdxMetadataTableLayout =
        from _ in Magic("metadata"u8.ToArray(), "VHDX: metadata region")
        from count in At(10, U16)
        from entries in At(32, Count(count, VhdxMetadataEntryLayout))
        select entries;

    // メタデータ項目。
    static readonly Field<VhdxFileParameters> VhdxFileParametersLayout =
        from blockSize in U32
        from flags in U32
        select new VhdxFileParameters((int)blockSize, (flags & 2) != 0);

    // ペアレントロケータ: 種別 GUID、キー/値の数(offset 18)、(キー位置、値位置、キー長、値長)× 数(offset 20 から)、文字列は位置で引く。
    static readonly Field<(int KeyOffset, int ValueOffset, int KeyLength, int ValueLength)> VhdxKeyValueLayout =
        from keyOffset in U32
        from valueOffset in U32
        from keyLength in U16
        from valueLength in U16
        select ((int)keyOffset, (int)valueOffset, (int)keyLength, (int)valueLength);

    static readonly Field<VhdxParentLocator> VhdxParentLocatorLayout =
        from count in At(18, U16)
        from entries in At(20, Count(count, VhdxKeyValueLayout))
        from pairs in Sequence(entries.Select(entry =>
            from key in At(entry.KeyOffset, Utf16(entry.KeyLength))
            from value in At(entry.ValueOffset, Utf16(entry.ValueLength))
            select (key, value)))
        select new VhdxParentLocator(
            pairs.Where(pair => pair.key == "parent_linkage").Select(pair => Guid.Parse(pair.value.Trim('{', '}'))).FirstOrDefault(Guid.Empty),
            [.. pairs.Where(pair => pair.key is "relative_path" or "absolute_win32_path" or "volume_path").Select(pair => pair.value)]);
}
