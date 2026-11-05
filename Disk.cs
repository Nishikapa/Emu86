using System.Buffers.Binary;
using System.Text;
using static Emu86.Fields;

namespace Emu86;

// ベースイメージ + AVHDX 差分オーバーレイによる Copy-on-Write ディスク。
//
// 読み込みは最上位(AVHDX)から親チェーンを辿る: 自分にあるセクタは自分から、
// 無いセクタは親(ベースイメージ)から読む。書き込みは常に最上位 AVHDX にのみ行われ、
// ベースイメージは一切変更されない。
//
// 対応フォーマット:
//   VHD  : 固定(2)/可変長(3)/差分(4)。差分はセクタビットマップ(MSBファースト)で親へ委譲。
//   VHDX : 可変長および差分(AVHDX)。BAT の PARTIALLY_PRESENT ブロックは
//          セクタビットマップ(LSBファースト)で親へ委譲。親はペアレントロケータで解決。
//   生イメージ: そのままセクタ列として扱う。
public partial class DiskImage : IDisposable
{
    public const int SectorSize = 512;

    const uint VhdBatUnallocated = 0xFFFFFFFF;
    const int VhdTypeDifferencing = 4;

    // VHDX BAT エントリ状態
    const int PayloadFullyPresent = 6;
    const int PayloadPartiallyPresent = 7;
    const int BitmapPresent = 6;

    static readonly Guid BatRegionGuid = new("2DC27766-F623-4200-9D64-115E9BFD4A08");
    static readonly Guid MetaRegionGuid = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    static readonly Guid FileParamsGuid = new("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
    static readonly Guid DiskSizeGuid = new("2FA54224-CD1B-4876-B211-5DBED83BF4B8");
    static readonly Guid LogicalSectorGuid = new("8141BF1D-A96F-4709-BA47-F233A8FAAB5F");
    static readonly Guid PhysicalSectorGuid = new("CDA348C7-445D-4471-9CC9-E9885251C556");
    static readonly Guid Page83Guid = new("BECA12AB-B2E6-4523-93EF-C309E000C746");
    static readonly Guid ParentLocatorGuid = new("A8D35F2D-B30B-454D-ABF7-D3D84834AB0C");
    static readonly Guid VhdxParentLocatorType = new("B04AEFB7-D19E-4A81-B789-25B8E9445913");

    readonly FileStream file;
    readonly bool writable;
    readonly DiskImage parent;
    bool disposed;

    public long TotalSectors { get; }

    // このイメージのファイルパス(フルパス)と、解決済みの親イメージのパス(親なしなら null)。
    public string FilePath { get; }
    public string ParentPath => parent?.FilePath;

    // VHD (conectix)
    readonly bool vhd;
    readonly int vhdType;
    readonly uint[] vhdBat = [];
    readonly Dictionary<long, byte[]> vhdBitmapCache = [];

    // VHDX (vhdxfile)
    readonly bool vhdx;
    readonly ulong[] vhdxBat = [];
    readonly long vhdxBatOffset;
    readonly VhdxBatLayout vhdxLayout;
    readonly Guid dataWriteGuid;
    readonly Dictionary<long, byte[]> vhdxBitmapCache = [];
    long nextAlloc; // 書き込み用: 次のブロック割り当て位置(1MB境界)

    // 共通
    readonly int sectorsPerBlock;
    readonly int vhdBitmapSectors;

    // 形式ごとの解析結果(DiskFormat.cs のレイアウトをファイルに当てはめた値)。コンストラクタはこれをフィールドへ写すだけ。
    readonly record struct VhdFormat(int Type, long TotalSectors, int SectorsPerBlock, int BitmapSectors, uint[] Bat, VhdDynamicHeader Header);
    readonly record struct VhdxFormat(Guid DataWriteGuid, long BatOffset, VhdxBatLayout Layout, ulong[] Bat, int SectorsPerBlock, long TotalSectors, VhdxParentLocator Locator);

    public DiskImage(string path, bool writable = false)
    {
        this.writable = writable;
        FilePath = Path.GetFullPath(path);
        file = new FileStream(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);

        try
        {
            var signature = ReadAt(file, 0, 8);
            if (signature.AsSpan().SequenceEqual("conectix"u8))
            {
                vhd = true;
                var format = ReadVhd(file);
                (vhdType, TotalSectors, sectorsPerBlock, vhdBitmapSectors, vhdBat) = (format.Type, format.TotalSectors, format.SectorsPerBlock, format.BitmapSectors, format.Bat);
                if (vhdType == VhdTypeDifferencing)
                    parent = OpenVhdParent(path, format.Header);
            }
            else if (signature.AsSpan().SequenceEqual("vhdxfile"u8))
            {
                vhdx = true;
                var format = ReadVhdx(file);
                (dataWriteGuid, vhdxBatOffset, vhdxLayout, vhdxBat, sectorsPerBlock, TotalSectors) =
                    (format.DataWriteGuid, format.BatOffset, format.Layout, format.Bat, format.SectorsPerBlock, format.TotalSectors);
                nextAlloc = (file.Length + 0xFFFFF) & ~0xFFFFFL;
                if (vhdxLayout.HasParent)
                    parent = OpenVhdxParent(path, format.Locator.Linkage, format.Locator.Paths);
            }
            else
                TotalSectors = file.Length / SectorSize; // 生イメージ: そのままセクタ列として扱う。

            if (writable && (!vhdx || !vhdxLayout.HasParent))
                throw new InvalidOperationException("writable disk must be an AVHDX overlay");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // VHD: 先頭にフッタのコピー(可変長/差分)、動的ヘッダ(offset 512)、BAT。値はビッグエンディアン。
    static VhdFormat ReadVhd(FileStream file)
    {
        var footer = VhdFooterLayout.Parse(ReadAt(file, 0, SectorSize));
        var dynamic = VhdDynamicHeaderLayout.Parse(ReadAt(file, SectorSize, 1024));
        var sectorsPerBlock = dynamic.BlockSize / SectorSize;
        var bat = Count(dynamic.MaxEntries, U32BE).Parse(ReadAt(file, dynamic.TableOffset, dynamic.MaxEntries * 4));
        return new(footer.Type, footer.Size / SectorSize, sectorsPerBlock, (sectorsPerBlock / 8 + SectorSize - 1) / SectorSize, [.. bat], dynamic);
    }

    // VHDX: ヘッダ 1 の DataWriteGuid(親子リンクの検証用)、リージョン表(0x30000)が指す BAT とメタデータの位置、
    // メタデータ領域の項目(GUID で引く: ブロックサイズと親の有無、仮想サイズ、論理セクタ、ペアレントロケータ)、BAT。
    static VhdxFormat ReadVhdx(FileStream file)
    {
        var dataWriteGuid = VhdxDataWriteGuidLayout.Parse(ReadAt(file, 0x10000, 4096));
        var regions = VhdxRegionTableLayout.Parse(ReadAt(file, 0x30000, 0x10000));
        var batRegion = regions.FirstOrDefault(region => region.Guid == BatRegionGuid);
        var metaRegion = regions.FirstOrDefault(region => region.Guid == MetaRegionGuid);

        var meta = ReadAt(file, metaRegion.Offset, (int)metaRegion.Length);
        var entries = VhdxMetadataTableLayout.Parse(meta);
        T Item<T>(Guid id, Field<T> layout, T fallback) =>
            entries.LastOrDefault(entry => entry.Guid == id) is var found && found.Guid == id ? layout.Parse(meta, found.Offset) : fallback;
        var (blockSize, hasParent) = Item(FileParamsGuid, VhdxFileParametersLayout, default);
        var diskSize = Item(DiskSizeGuid, I64, 0L);
        var logicalSector = Item(LogicalSectorGuid, U32, 512u);
        var locator = Item(ParentLocatorGuid, VhdxParentLocatorLayout, new VhdxParentLocator(Guid.Empty, []));
        if (logicalSector != SectorSize)
            throw new InvalidDataException($"VHDX: unsupported logical sector size {logicalSector}");

        // BAT(ペイロード chunkRatio 個ごとにビットマップエントリが挟まる)
        var layout = new VhdxBatLayout(diskSize, blockSize, hasParent);
        var requiredLength = (long)layout.EntryCount * sizeof(ulong);
        if (batRegion.Length < requiredLength || batRegion.Offset < 0 || batRegion.Offset > file.Length - requiredLength)
            throw new InvalidDataException("VHDX: BAT region is too small or truncated");
        var bat = Count(layout.EntryCount, U64).Parse(ReadAt(file, batRegion.Offset, layout.EntryCount * 8));
        return new(dataWriteGuid, batRegion.Offset, layout, [.. bat], blockSize / SectorSize, diskSize / SectorSize, locator);
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { parent?.Dispose(); }
        finally { file.Dispose(); }
    }

    public void Flush() => file.Flush();

    // ファイルの offset から length バイトを読む。
    static byte[] ReadAt(FileStream file, long offset, int length)
    {
        var buffer = new byte[length];
        file.Seek(offset, SeekOrigin.Begin);
        file.ReadExactly(buffer);
        return buffer;
    }

    static byte[] ReadFileAt(string path, long offset, int length)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadAt(file, offset, length);
    }

    // ============ 親解決 ============

    static string DirectoryOf(string path) => Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";

    // 候補のうち最初に存在するパスを親として開く。どれも無ければ警告して null(無いセクタはゼロとして読む)。
    static DiskImage OpenParent(IReadOnlyList<string> candidates, string kind, Action<DiskImage> verify = null)
    {
        if (candidates.FirstOrDefault(File.Exists) is not string path)
        {
            Console.Error.WriteLine($"[disk] WARNING: parent {kind} not found: {string.Join(" / ", candidates)} (missing sectors read as zero)");
            return null;
        }
        Console.Error.WriteLine($"[disk] parent {kind}: {path}");
        var parent = new DiskImage(path);
        verify?.Invoke(parent);
        return parent;
    }

    // VHD の親: 動的ヘッダの親の名前(子と同じディレクトリで探す)と、Windows 形式(W2ku)のロケータが指すパス。
    static DiskImage OpenVhdParent(string childPath, VhdDynamicHeader header)
    {
        var named = header.ParentName.Length > 0 ? [Path.Combine(DirectoryOf(childPath), Path.GetFileName(header.ParentName))] : Enumerable.Empty<string>();
        var located =
            from locator in header.Locators
            where locator.IsWindowsUnicode
            select Encoding.Unicode.GetString(ReadFileAt(childPath, locator.Offset, locator.Length)).TrimEnd('\0');
        return OpenParent([.. named, .. located], "VHD");
    }

    // VHDX の親: ペアレントロケータのパス(相対パスは子のディレクトリから。Windows 区切り)。開けたら DataWriteGuid を照合する。
    static DiskImage OpenVhdxParent(string childPath, Guid linkage, IReadOnlyList<string> paths)
    {
        var candidates = paths
            .Select(p => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(DirectoryOf(childPath), p.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();
        return OpenParent(candidates, "image", parent =>
        {
            if (parent.vhdx && linkage != Guid.Empty && parent.dataWriteGuid != linkage)
                Console.Error.WriteLine($"[disk] WARNING: parent DataWriteGuid mismatch (expected {linkage}, got {parent.dataWriteGuid})");
        });
    }

    // ============ 読み込み ============

    // セクタの所在。Image が null なら明示的にゼロ(VHDX の BLOCK_ZERO)。
    readonly record struct SectorSource(DiskImage Image, long Offset);

    // 自分に有ればそこから、無ければ親を順にたどり、どこにも無ければ null(ゼロとして読む)。
    // 範囲外の LBA はそのイメージでは無いものとして扱う。
    SectorSource? Locate(long lba) =>
        lba < 0 || lba >= TotalSectors ? null : LocateOwn(lba) ?? parent?.Locate(lba);

    SectorSource? LocateOwn(long lba) =>
        vhd ? LocateVhd(lba) : vhdx ? LocateVhdx(lba) : new SectorSource(this, lba * SectorSize); // 生イメージ

    public void ReadSector(long lba, byte[] buf, int offset)
    {
        if (Locate(lba) is { Image: { } image, Offset: var position })
        {
            image.file.Seek(position, SeekOrigin.Begin);
            image.file.ReadExactly(buf, offset, SectorSize);
        }
        else
            Array.Clear(buf, offset, SectorSize);
    }

    SectorSource? LocateVhd(long lba)
    {
        var block = lba / sectorsPerBlock;
        var sector = (int)(lba % sectorsPerBlock);
        var entry = vhdBat[block];

        // 未割り当てブロック、または差分VHDでビットが0のセクタは親から読む。
        // VHD のビットマップは MSB ファースト(bit7 が先頭セクタ)。
        var fromParent = entry == VhdBatUnallocated ||
            (vhdType == VhdTypeDifferencing &&
             (VhdBlockBitmap(block, entry)[sector / 8] >> (7 - sector % 8) & 1) == 0);
        return fromParent ? null : new SectorSource(this, ((long)entry + vhdBitmapSectors + sector) * SectorSize);
    }

    byte[] VhdBlockBitmap(long block, uint entry)
    {
        if (vhdBitmapCache.TryGetValue(block, out var b))
            return b;
        b = new byte[vhdBitmapSectors * SectorSize];
        file.Seek((long)entry * SectorSize, SeekOrigin.Begin);
        file.ReadExactly(b);
        vhdBitmapCache[block] = b;
        return b;
    }

    SectorSource? LocateVhdx(long lba)
    {
        var block = lba / sectorsPerBlock;
        var entry = vhdxBat[vhdxLayout.PayloadIndex(block)];
        var inBlock = new SectorSource(this, (long)(entry & ~0xFFFFFUL) + lba % sectorsPerBlock * SectorSize);
        var position = vhdxLayout.SectorInChunk(lba);
        return (int)(entry & 7) switch
        {
            PayloadFullyPresent => inBlock,
            // VHDX のビットマップは LSB ファースト(bit0 が先頭セクタ)。チャンク単位(1MB)。ビットが 0 なら親。
            PayloadPartiallyPresent => VhdxChunkBitmap(vhdxLayout.ChunkIndex(block)) is { } bitmap && (bitmap[position / 8] >> (int)(position % 8) & 1) != 0 ? inBlock : null,
            2 => new SectorSource(null, 0), // BLOCK_ZERO: 親を見ずにゼロ
            _ => null,                      // NOT_PRESENT / UNDEFINED / UNMAPPED
        };
    }

    // チャンクのセクタビットマップ(1MB)。未割り当てなら null。
    byte[] VhdxChunkBitmap(long chunk)
    {
        if (vhdxBitmapCache.TryGetValue(chunk, out var b))
            return b;
        var entry = vhdxBat[vhdxLayout.BitmapIndex(chunk)];
        if ((entry & 7) != BitmapPresent)
            return null;
        b = new byte[0x100000];
        file.Seek((long)(entry & ~0xFFFFFUL), SeekOrigin.Begin);
        file.ReadExactly(b);
        vhdxBitmapCache[chunk] = b;
        return b;
    }

    // ============ 書き込み(最上位 AVHDX のみ) ============

    public void WriteSector(long lba, byte[] data, int offset)
    {
        if (!writable)
            throw new InvalidOperationException("disk is read-only");
        if (lba < 0 || lba >= TotalSectors)
            return;

        var block = lba / sectorsPerBlock;
        var chunk = vhdxLayout.ChunkIndex(block);
        var payloadIdx = vhdxLayout.PayloadIndex(block);
        var bitmapIdx = vhdxLayout.BitmapIndex(chunk);

        // ペイロードブロック未割り当てなら確保(PARTIALLY_PRESENT)
        var pstate = (int)(vhdxBat[payloadIdx] & 7);
        if (pstate != PayloadPartiallyPresent && pstate != PayloadFullyPresent)
            AllocateBat(payloadIdx, (long)sectorsPerBlock * SectorSize, PayloadPartiallyPresent);

        // ビットマップブロック未割り当てなら確保(1MB)
        if ((vhdxBat[bitmapIdx] & 7) != BitmapPresent)
        {
            AllocateBat(bitmapIdx, 0x100000, BitmapPresent);
            vhdxBitmapCache.Remove(chunk);
        }

        // データ書き込み
        var payloadOffset = (long)(vhdxBat[payloadIdx] & ~0xFFFFFUL);
        file.Seek(payloadOffset + lba % sectorsPerBlock * SectorSize, SeekOrigin.Begin);
        file.Write(data, offset, SectorSize);

        // ビットマップのビットを立てる(LSBファースト)
        var bmp = VhdxChunkBitmap(chunk);
        var pos = vhdxLayout.SectorInChunk(lba);
        bmp[pos / 8] |= (byte)(1 << (int)(pos % 8));
        var bitmapOffset = (long)(vhdxBat[bitmapIdx] & ~0xFFFFFUL);
        file.Seek(bitmapOffset + pos / 8, SeekOrigin.Begin);
        file.WriteByte(bmp[pos / 8]);
        file.Flush();
    }

    // BAT エントリにブロックを割り当て、BAT をファイルへ書き戻す。
    void AllocateBat(long batIdx, long size, int state)
    {
        var off = nextAlloc;
        nextAlloc += (size + 0xFFFFF) & ~0xFFFFFL;
        file.SetLength(nextAlloc); // 拡張部分はゼロで埋まる
        vhdxBat[batIdx] = (ulong)off | (uint)state;

        Span<byte> e = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(e, vhdxBat[batIdx]);
        file.Seek(vhdxBatOffset + batIdx * 8, SeekOrigin.Begin);
        file.Write(e);
    }

    // ============ AVHDX の新規作成(形式の書き込みは DiskFormat.Write.cs) ============

    // この実装が作る AVHDX を識別する Creator 印。書き込みフォーマットを変えたら
    // overlayPath が無ければ、basePath を親とする差分 VHDX(AVHDX)を作成する。
    // 既存 overlay がこの実装の版と一致しなければ退避して作り直す
    // (開発途中の古い形式で書かれた overlay が壊れて読めない事故を防ぐ)。
    static public void EnsureOverlay(string overlayPath, string basePath)
    {
        if (File.Exists(overlayPath))
        {
            if (!OverlayIsCurrent(overlayPath))
                RetireOverlay(overlayPath, "is from an older format");
            else if (!OverlayParentIs(overlayPath, basePath))
                RetireOverlay(overlayPath, $"was created against a different base image (expected {basePath})");
            else
                return;
        }

        using var baseImage = new DiskImage(basePath);
        var blockSize = baseImage.vhdx || baseImage.vhd ? baseImage.sectorsPerBlock * SectorSize : 0x200000;
        CreateAvhdx(overlayPath, basePath, baseImage.TotalSectors * SectorSize, blockSize, baseImage.dataWriteGuid);
        Console.Error.WriteLine($"[disk] created overlay: {overlayPath} (parent: {basePath})");
    }

    // 古い/親違いの overlay を .old へ退避する(既存の .old は上書き)。
    static void RetireOverlay(string overlayPath, string reason)
    {
        var stale = overlayPath + ".old";
        File.Delete(stale);
        File.Move(overlayPath, stale);
        Console.Error.WriteLine($"[disk] overlay {overlayPath} {reason}; moved to {stale} and recreating");
    }

    // overlay のペアレントロケータが basePath を指しているか。
    // (sample.vhd と sample.vhdx を切り替えたときに、別の親向けに作られた差分を使い回さないため)
    static bool OverlayParentIs(string overlayPath, string basePath)
    {
        try
        {
            using var overlay = new DiskImage(overlayPath);
            var parentPath = overlay.ParentPath;
            return parentPath != null
                && string.Equals(parentPath, Path.GetFullPath(basePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // overlay の File Identifier に埋めた Creator 印が現行版と一致するか。
    static bool OverlayIsCurrent(string overlayPath)
    {
        try
        {
            using var f = new FileStream(overlayPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var id = new byte[8 + OverlayCreator.Length * 2];
            f.ReadExactly(id);
            return id.AsSpan(0, 8).SequenceEqual("vhdxfile"u8)
                && System.Text.Encoding.Unicode.GetString(id, 8, OverlayCreator.Length * 2) == OverlayCreator;
        }
        catch
        {
            return false;
        }
    }

    // CRC-32C (Castagnoli)。VHDX のヘッダ/リージョンテーブルのチェックサムに使う。
    static readonly uint[] Crc32CTable =
        [.. Enumerable.Range(0, 256).Select(index => Enumerable.Repeat(0, 8).Aggregate((uint)index, (crc, _) => (crc & 1) != 0 ? 0x82F63B78 ^ (crc >> 1) : crc >> 1))];

    internal static uint Crc32C(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = (crc >> 8) ^ Crc32CTable[(crc ^ b) & 0xFF];
        return ~crc;
    }
}
