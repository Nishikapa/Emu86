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

    public DiskImage(string path, bool writable = false)
    {
        this.writable = writable;
        FilePath = Path.GetFullPath(path);
        file = new FileStream(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);

        try
        {
            // 形式の記述(各レイアウト)は DiskFormat.cs。ここではファイルの位置を読んで当てはめるだけ。
            var header = ReadAt(0, SectorSize);
            if (header.AsSpan(0, 8).SequenceEqual("conectix"u8))
            {
                // VHD: 先頭にフッタのコピー(可変長/差分)。値はビッグエンディアン。
                vhd = true;
                var footer = VhdFooterLayout.Parse(header);
                vhdType = footer.Type;
                TotalSectors = footer.Size / SectorSize;

                var dynamic = VhdDynamicHeaderLayout.Parse(ReadAt(SectorSize, 1024)); // 動的ヘッダ(offset 512)
                sectorsPerBlock = dynamic.BlockSize / SectorSize;
                vhdBitmapSectors = (sectorsPerBlock / 8 + SectorSize - 1) / SectorSize;
                vhdBat = [.. Count(dynamic.MaxEntries, U32BE).Parse(ReadAt(dynamic.TableOffset, dynamic.MaxEntries * 4))];

                if (vhdType == VhdTypeDifferencing)
                    parent = OpenVhdParent(path, dynamic);
            }
            else if (header.AsSpan(0, 8).SequenceEqual("vhdxfile"u8))
            {
                vhdx = true;
                dataWriteGuid = VhdxDataWriteGuidLayout.Parse(ReadAt(0x10000, 4096)); // ヘッダ 1(親子リンクの検証用)

                // リージョン表(0x30000)から BAT とメタデータの位置を得る。
                var regions = VhdxRegionTableLayout.Parse(ReadAt(0x30000, 0x10000));
                var batRegion = regions.FirstOrDefault(region => region.Guid == BatRegionGuid);
                var metaRegion = regions.FirstOrDefault(region => region.Guid == MetaRegionGuid);
                vhdxBatOffset = batRegion.Offset;

                // メタデータ領域: ブロックサイズと親の有無、仮想サイズ、論理セクタ、ペアレントロケータ。
                var meta = ReadAt(metaRegion.Offset, (int)metaRegion.Length);
                int blockSize = 0, logicalSector = 512;
                long diskSize = 0;
                var hasParent = false;
                var locator = new VhdxParentLocator(Guid.Empty, []);
                foreach (var entry in VhdxMetadataTableLayout.Parse(meta))
                {
                    if (entry.Guid == FileParamsGuid) (blockSize, hasParent) = VhdxFileParametersLayout.Parse(meta, entry.Offset);
                    else if (entry.Guid == DiskSizeGuid) diskSize = I64.Parse(meta, entry.Offset);
                    else if (entry.Guid == LogicalSectorGuid) logicalSector = (int)U32.Parse(meta, entry.Offset);
                    else if (entry.Guid == ParentLocatorGuid) locator = VhdxParentLocatorLayout.Parse(meta, entry.Offset);
                }
                if (logicalSector != SectorSize)
                    throw new InvalidDataException($"VHDX: unsupported logical sector size {logicalSector}");

                vhdxLayout = new VhdxBatLayout(diskSize, blockSize, hasParent);
                sectorsPerBlock = blockSize / SectorSize;
                TotalSectors = diskSize / SectorSize;

                // BAT(ペイロード chunkRatio 個ごとにビットマップエントリが挟まる)
                var batEntries = vhdxLayout.EntryCount;
                var requiredLength = (long)batEntries * sizeof(ulong);
                if (batRegion.Length < requiredLength || vhdxBatOffset < 0 || vhdxBatOffset > file.Length - requiredLength)
                    throw new InvalidDataException("VHDX: BAT region is too small or truncated");
                vhdxBat = [.. Count(batEntries, U64).Parse(ReadAt(vhdxBatOffset, batEntries * 8))];

                nextAlloc = (file.Length + 0xFFFFF) & ~0xFFFFFL;

                if (hasParent)
                    parent = OpenVhdxParent(path, locator.Linkage, locator.Paths);
            }
            else
            {
                // 生イメージ: そのままセクタ列として扱う。
                TotalSectors = file.Length / SectorSize;
            }

            if (writable && (!vhdx || !vhdxLayout.HasParent))
                throw new InvalidOperationException("writable disk must be an AVHDX overlay");
        }
        catch
        {
            Dispose();
            throw;
        }
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

    // ============ 親解決 ============

    // 差分VHDの親を開く。候補: 子と同じフォルダの親ファイル名 → ロケータの絶対パス。
    byte[] ReadAt(long offset, int length)
    {
        var buffer = new byte[length];
        file.Seek(offset, SeekOrigin.Begin);
        file.ReadExactly(buffer);
        return buffer;
    }

    // VHD の親: 動的ヘッダの親の名前(子と同じディレクトリで探す)と、Windows 形式(W2ku)のロケータが指すパス。
    static DiskImage OpenVhdParent(string childPath, VhdDynamicHeader header)
    {
        var candidates = new List<string>();
        if (header.ParentName.Length > 0)
            candidates.Add(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(childPath)) ?? ".", Path.GetFileName(header.ParentName)));

        foreach (var locator in header.Locators.Where(locator => locator.IsWindowsUnicode))
        {
            using var f = new FileStream(childPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buf = new byte[locator.Length];
            f.Seek(locator.Offset, SeekOrigin.Begin);
            f.ReadExactly(buf);
            candidates.Add(Encoding.Unicode.GetString(buf).TrimEnd('\0'));
        }

        foreach (var path in candidates.Where(File.Exists))
        {
            Console.Error.WriteLine($"[disk] parent VHD: {path}");
            return new DiskImage(path);
        }

        Console.Error.WriteLine($"[disk] WARNING: parent VHD not found: {string.Join(" / ", candidates)} (missing sectors read as zero)");
        return null;
    }

    static DiskImage OpenVhdxParent(string childPath, Guid linkage, IReadOnlyList<string> paths)
    {
        var childDir = Path.GetDirectoryName(Path.GetFullPath(childPath)) ?? ".";
        var candidates = paths
            .Select(p => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(childDir, p.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();

        foreach (var path in candidates.Where(File.Exists))
        {
            Console.Error.WriteLine($"[disk] parent image: {path}");
            var p = new DiskImage(path);
            if (p.vhdx && linkage != Guid.Empty && p.dataWriteGuid != linkage)
                Console.Error.WriteLine($"[disk] WARNING: parent DataWriteGuid mismatch (expected {linkage}, got {p.dataWriteGuid})");
            return p;
        }

        Console.Error.WriteLine($"[disk] WARNING: parent image not found: {string.Join(" / ", candidates)} (missing sectors read as zero)");
        return null;
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
        var state = (int)(entry & 7);
        var inBlock = new SectorSource(this, (long)(entry & ~0xFFFFFUL) + lba % sectorsPerBlock * SectorSize);

        switch (state)
        {
            case PayloadFullyPresent:
                return inBlock;

            case PayloadPartiallyPresent:
                // VHDX のビットマップは LSB ファースト(bit0 が先頭セクタ)。チャンク単位(1MB)。ビットが 0 なら親。
                var bmp = VhdxChunkBitmap(vhdxLayout.ChunkIndex(block));
                var pos = vhdxLayout.SectorInChunk(lba);
                return bmp != null && (bmp[pos / 8] >> (int)(pos % 8) & 1) != 0 ? inBlock : null;

            case 2: // BLOCK_ZERO: 親を見ずにゼロ
                return new SectorSource(null, 0);

            default: // NOT_PRESENT / UNDEFINED / UNMAPPED
                return null;
        }
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

    // ============ AVHDX の新規作成 ============

    // この実装が作る AVHDX を識別する Creator 印。書き込みフォーマットを変えたら
    // 版数を上げると、古い版で作られた overlay は自動的に作り直される。
    const string OverlayCreator = "Emu86-avhdx-1";

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

    // 差分 VHDX を生成する。構造: FileID / Header x2 / RegionTable x2 / Log(1MB) / Metadata(1MB) / BAT。
    static void CreateAvhdx(string path, string parentPath, long virtualSize, int blockSize, Guid parentDataWriteGuid)
    {
        var layout = new VhdxBatLayout(virtualSize, blockSize, hasParent: true);
        var batLength = layout.RegionLength;

        var image = new byte[checked(0x300000 + batLength)];
        var w = image.AsSpan();

        // File Identifier(Creator にフォーマット版数を埋め、旧版 overlay を判別できるようにする)
        "vhdxfile"u8.CopyTo(w);
        System.Text.Encoding.Unicode.GetBytes(OverlayCreator).CopyTo(w[8..]);

        // Header x2 (0x10000, 0x20000)
        for (int i = 0; i < 2; i++)
        {
            var h = w.Slice(0x10000 + i * 0x10000, 4096);
            "head"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt64LittleEndian(h[8..], (ulong)(i + 1)); // SequenceNumber
            Guid.NewGuid().TryWriteBytes(h[0x10..]);                          // FileWriteGuid
            Guid.NewGuid().TryWriteBytes(h[0x20..]);                          // DataWriteGuid
            // LogGuid = 0 (クリーン)
            BinaryPrimitives.WriteUInt16LittleEndian(h[0x42..], 1);           // Version
            BinaryPrimitives.WriteUInt32LittleEndian(h[0x44..], 0x100000);    // LogLength
            BinaryPrimitives.WriteUInt64LittleEndian(h[0x48..], 0x100000);    // LogOffset
            BinaryPrimitives.WriteUInt32LittleEndian(h[4..], Crc32C(h));      // Checksum
        }

        // Region Table x2 (0x30000, 0x40000)
        {
            var rt = w.Slice(0x30000, 0x10000);
            "regi"u8.CopyTo(rt);
            BinaryPrimitives.WriteUInt32LittleEndian(rt[8..], 2);
            var e0 = rt[16..];
            BatRegionGuid.TryWriteBytes(e0);
            BinaryPrimitives.WriteInt64LittleEndian(e0[16..], 0x300000);
            BinaryPrimitives.WriteUInt32LittleEndian(e0[24..], (uint)batLength);
            BinaryPrimitives.WriteUInt32LittleEndian(e0[28..], 1); // Required
            var e1 = rt[48..];
            MetaRegionGuid.TryWriteBytes(e1);
            BinaryPrimitives.WriteInt64LittleEndian(e1[16..], 0x200000);
            BinaryPrimitives.WriteUInt32LittleEndian(e1[24..], 0x100000);
            BinaryPrimitives.WriteUInt32LittleEndian(e1[28..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(rt[4..], Crc32C(rt));
            rt.CopyTo(w.Slice(0x40000, 0x10000));
        }

        // Metadata (0x200000): テーブル + 実データ(領域先頭から 0x10000 以降)
        {
            const int metaBase = 0x200000;
            "metadata"u8.CopyTo(w[metaBase..]);
            var dataOff = 0x10000;
            var entryIdx = 0;

            // ローカル関数は Span をキャプチャできないため、image + 絶対オフセットで書く
            void AddEntry(Guid id, byte[] data, uint flags)
            {
                var e = image.AsSpan(metaBase + 32 + entryIdx * 32, 32);
                id.TryWriteBytes(e);
                BinaryPrimitives.WriteUInt32LittleEndian(e[16..], (uint)dataOff);
                BinaryPrimitives.WriteUInt32LittleEndian(e[20..], (uint)data.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(e[24..], flags);
                data.CopyTo(image.AsSpan(metaBase + dataOff));
                dataOff += (data.Length + 15) & ~15;
                entryIdx++;
            }

            // File Parameters: BlockSize + HasParent(bit1)
            var fp = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(fp, (uint)blockSize);
            BinaryPrimitives.WriteUInt32LittleEndian(fp.AsSpan(4), 2);
            AddEntry(FileParamsGuid, fp, 0x04);

            var vds = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(vds, virtualSize);
            AddEntry(DiskSizeGuid, vds, 0x06);

            var lss = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lss, SectorSize);
            AddEntry(LogicalSectorGuid, lss, 0x06);

            var pss = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(pss, 4096);
            AddEntry(PhysicalSectorGuid, pss, 0x06);

            AddEntry(Page83Guid, Guid.NewGuid().ToByteArray(), 0x06);

            AddEntry(ParentLocatorGuid, BuildParentLocator(parentPath, path, parentDataWriteGuid), 0x04);

            BinaryPrimitives.WriteUInt16LittleEndian(w[(metaBase + 10)..], (ushort)entryIdx);
        }

        File.WriteAllBytes(path, image);
    }

    // ペアレントロケータ項目: parent_linkage / relative_path / absolute_win32_path
    static byte[] BuildParentLocator(string parentPath, string childPath, Guid parentDataWriteGuid)
    {
        var childDir = Path.GetDirectoryName(Path.GetFullPath(childPath)) ?? ".";
        var relative = Path.GetRelativePath(childDir, Path.GetFullPath(parentPath)).Replace(Path.DirectorySeparatorChar, '\\');
        if (!relative.StartsWith('.') && !relative.Contains(':'))
            relative = ".\\" + relative;
        var kv = new (string key, string val)[]
        {
            ("parent_linkage", parentDataWriteGuid.ToString("B").ToUpperInvariant()),
            ("relative_path", relative),
            ("absolute_win32_path", Path.GetFullPath(parentPath)),
        };

        var headerLen = 20 + kv.Length * 12;
        var total = headerLen + kv.Sum(x => (x.key.Length + x.val.Length) * 2);
        var buf = new byte[total];
        var s = buf.AsSpan();
        VhdxParentLocatorType.TryWriteBytes(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[18..], (ushort)kv.Length);
        var off = headerLen;
        for (int i = 0; i < kv.Length; i++)
        {
            var e = s[(20 + i * 12)..];
            var kb = System.Text.Encoding.Unicode.GetBytes(kv[i].key);
            var vb = System.Text.Encoding.Unicode.GetBytes(kv[i].val);
            BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)off);
            kb.CopyTo(s[off..]);
            off += kb.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)off);
            vb.CopyTo(s[off..]);
            off += vb.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(e[8..], (ushort)kb.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(e[10..], (ushort)vb.Length);
        }
        return buf;
    }

    // CRC-32C (Castagnoli)。VHDX のヘッダ/リージョンテーブルのチェックサムに使う。
    static readonly uint[] Crc32CTable = BuildCrc32CTable();

    static uint[] BuildCrc32CTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0x82F63B78 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    static uint Crc32C(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = (crc >> 8) ^ Crc32CTable[(crc ^ b) & 0xFF];
        return ~crc;
    }
}

// 最小限の ATA (PIO) デバイス。プライマリチャネル(ポート 0x1F0-0x1F7, 0x3F6)のマスタとして応答し、
// SeaBIOS の検出(IDENTIFY DEVICE)と READ/WRITE SECTORS (PIO) を処理する。割り込みは使わない(ポーリング前提)。
public class AtaDevice(DiskImage disk) : IDisposable
{
    bool disposed;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        disk?.Dispose();
    }

    // ステータスビット
    const byte BSY = 0x80, DRDY = 0x40, DSC = 0x10, DRQ = 0x08, ERR = 0x01;

    byte feature, sectorCount, lbaLow, lbaMid, lbaHigh, drive;
    byte status = DRDY | DSC;
    byte error;

    // PIO データバッファ(IDENTIFY / READ の応答、WRITE の受け口)
    byte[] buf = [];
    int bufPos;
    bool pendingWrite;
    long writeLba;
    int writeSectorsLeft;

    bool SlaveSelected => (drive & 0x10) != 0;

    // INTRQ 相当。コマンド完了/データ準備で立ち、ステータスレジスタ(0x1F7)読み出しで降りる。
    public bool IrqPending;

    public void Flush() => disk.Flush();

    public string DiskPath => disk?.FilePath;

    internal void AttachDisk(DiskImage image)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(image);
        if (disk != null) throw new InvalidOperationException("ATA disk is already attached");
        disk = image;
    }

    // INTRQ はデバイス状態(v7 から)の末尾に別途保存する。
    static readonly Codec<AtaDevice> Interrupt = new Codec<AtaDevice>()
        .Bool(a => a.IrqPending, (a, v) => a.IrqPending = v);

    internal void SaveInterruptState(BinaryWriter writer) => Interrupt.Save(writer, this);

    internal void LoadInterruptState(BinaryReader reader) => Interrupt.Load(reader, this, SnapshotStore.Version);

    // --atalog: コマンド/制御レジスタ書き込みとデータ転送完了を標準エラーへ記録する(OS のプローブ手順の調査用)。
    public bool Log;
    // ページング有効後(OS 稼働中)のレジスタ読み出しも記録する(上限付き)。ランナーが LogReads を更新する。
    public bool LogReads;
    int readLogLeft = 400;
    void L(string message) { if (Log) Console.Error.WriteLine("[ata] " + message); }

    // スナップショット保存/復元。接続先の DiskImage 自体は書き込みのたびに
    // ファイルへ反映済みのため、ここでは PIO レジスタとバッファのみを扱う。
    static readonly Codec<AtaDevice> Layout = new Codec<AtaDevice>()
        .U8(a => a.feature, (a, v) => a.feature = v)
        .U8(a => a.sectorCount, (a, v) => a.sectorCount = v)
        .U8(a => a.lbaLow, (a, v) => a.lbaLow = v)
        .U8(a => a.lbaMid, (a, v) => a.lbaMid = v)
        .U8(a => a.lbaHigh, (a, v) => a.lbaHigh = v)
        .U8(a => a.drive, (a, v) => a.drive = v)
        .U8(a => a.status, (a, v) => a.status = v)
        .U8(a => a.error, (a, v) => a.error = v)
        .ByteArray(a => a.buf, (a, v) => a.buf = v, 256 * DiskImage.SectorSize)
        .I32(a => a.bufPos, (a, v) => a.bufPos = v)
        .Bool(a => a.pendingWrite, (a, v) => a.pendingWrite = v)
        .I64(a => a.writeLba, (a, v) => a.writeLba = v)
        .I32(a => a.writeSectorsLeft, (a, v) => a.writeSectorsLeft = v)
        .Check(a => a.bufPos >= 0 && a.bufPos <= a.buf.Length && a.writeSectorsLeft is >= 0 and <= 256, "Invalid ATA transfer state");

    public void SaveState(BinaryWriter writer) => Layout.Save(writer, this);

    public void LoadState(BinaryReader reader) => Layout.Load(reader, this, SnapshotStore.Version);

    public byte ReadReg(int port)
    {
        if (Log && LogReads && readLogLeft > 0) { readLogLeft--; Console.Error.WriteLine($"[ata] rd {port:x3} (status={status:x2} drive={drive:x2})"); }
        switch (port)
        {
            case 0x1F1: return error;
            case 0x1F2: return sectorCount;
            case 0x1F3: return lbaLow;
            case 0x1F4: return lbaMid;
            case 0x1F5: return lbaHigh;
            case 0x1F6: return drive;
            // ステータスレジスタ読み出しは INTRQ を降ろす(0x1F7 のみ。0x3F6 は代替ステータスで降ろさない)。
            case 0x1F7:
                if (SlaveSelected) return 0;
                IrqPending = false;
                return status;
            case 0x3F6:
                return SlaveSelected ? (byte)0 : status;
            default: return 0xFF;
        }
    }

    public void WriteReg(int port, byte val)
    {
        switch (port)
        {
            case 0x1F1: feature = val; break;
            case 0x1F2: sectorCount = val; break;
            case 0x1F3: lbaLow = val; break;
            case 0x1F4: lbaMid = val; break;
            case 0x1F5: lbaHigh = val; break;
            case 0x1F6: drive = val; break;
            case 0x1F7:
                if (SlaveSelected) { L($"cmd {val:x2} to slave (absent) drive={drive:x2}"); break; }
                L($"cmd {val:x2} feat={feature:x2} cnt={sectorCount:x2} lba={lbaHigh:x2}{lbaMid:x2}{lbaLow:x2} drive={drive:x2}");
                Command(val);
                break;
            case 0x3F6:
                L($"devctl {val:x2}");
                if ((val & 0x04) != 0) // SRST: ソフトウェアリセット
                {
                    status = DRDY | DSC;
                    error = 1;
                    // ATA デバイスシグネチャ
                    sectorCount = 1; lbaLow = 1; lbaMid = 0; lbaHigh = 0;
                    buf = []; bufPos = 0; pendingWrite = false;
                }
                break;
        }
    }

    // コマンド表: コマンド番号 → 動作。表にないコマンド(DMA 系 0xC8/0x25/0xCA/0x35 等)は abort し、libata は PIO へ後退する。
    static readonly Dictionary<byte, Action<AtaDevice>> Commands = BuildCommands();

    static Dictionary<byte, Action<AtaDevice>> BuildCommands()
    {
        var table = new Dictionary<byte, Action<AtaDevice>>
        {
            [0xEC] = ata => ata.Respond(ata.BuildIdentify()), // IDENTIFY DEVICE
            [0xA1] = ata => ata.Abort(),                      // IDENTIFY PACKET DEVICE(ATAPI ではないので abort)
        };
        foreach (var read in new byte[] { 0x20, 0x21, 0x24, 0xC4 }) table[read] = ata => ata.BeginRead();    // READ SECTORS (PIO) / EXT / MULTIPLE(単一扱い)
        foreach (var write in new byte[] { 0x30, 0x31, 0x34, 0xC5 }) table[write] = ata => ata.BeginWrite(); // WRITE SECTORS (PIO) / EXT / MULTIPLE(単一扱い)
        // SET FEATURES / FLUSH CACHE(EXT) / READ VERIFY / INIT DEV PARAMS / NOP / IDLE:
        // libata のプローブ・設定で発行される非データコマンド。成功として完了 → INTRQ。
        foreach (var nonData in new byte[] { 0xEF, 0xE7, 0xEA, 0x40, 0x41, 0x91, 0x00, 0xE1, 0xE0 }) table[nonData] = ata => ata.Complete();
        return table;
    }

    void Command(byte cmd)
    {
        error = 0;
        if (Commands.TryGetValue(cmd, out var action)) action(this);
        else
        {
            L($"  -> unsupported command {cmd:x2}: abort");
            Abort();
        }
    }

    int RequestedSectors => sectorCount == 0 ? 256 : sectorCount;

    // データを用意して DRQ: データ準備完了 → INTRQ。
    void Respond(byte[] data)
    {
        buf = data;
        bufPos = 0;
        status = DRDY | DSC | DRQ;
        IrqPending = true;
    }

    // 非データコマンドの完了 → INTRQ。
    void Complete()
    {
        status = DRDY | DSC;
        IrqPending = true;
    }

    void Abort()
    {
        error = 0x04;
        status = DRDY | DSC | ERR;
        IrqPending = true;
    }

    void BeginRead()
    {
        var count = RequestedSectors;
        var lba = CurrentLba();
        var data = new byte[count * DiskImage.SectorSize];
        for (int i = 0; i < count; i++)
            disk.ReadSector(lba + i, data, i * DiskImage.SectorSize);
        Respond(data);
    }

    // WRITE の最初のブロック要求は DRQ を立てるが INTRQ は上げない(ホストが先にデータを書く)。
    void BeginWrite()
    {
        writeSectorsLeft = RequestedSectors;
        writeLba = CurrentLba();
        buf = new byte[DiskImage.SectorSize];
        bufPos = 0;
        pendingWrite = true;
        status = DRDY | DSC | DRQ;
    }

    long CurrentLba() =>
        lbaLow | ((long)lbaMid << 8) | ((long)lbaHigh << 16) | ((long)(drive & 0x0F) << 24);

    // データポート(0x1F0)の読み出し。size バイトをバッファから取り出す。
    public uint ReadData(int size)
    {
        uint v = 0;
        var before = bufPos;
        for (int i = 0; i < size; i++)
            v |= (uint)(bufPos < buf.Length ? buf[bufPos++] : 0) << (8 * i);
        if (bufPos >= buf.Length && !pendingWrite)
            status = DRDY | DSC; // 転送完了
        else if (!pendingWrite && bufPos / DiskImage.SectorSize != before / DiskImage.SectorSize)
        {
            // READ SECTORS(PIO)は 1 セクタ転送し終えるごとに次のセクタで INTRQ を上げる
            // (Windows の atapi.sys はセクタごとの割り込みを待つ。無いとタイムアウト→リセット→再試行を繰り返す)。
            IrqPending = true;
        }
        return v;
    }

    // データポート(0x1F0)への書き込み。1セクタ貯まるたびにディスク(差分)へ書き出す。
    public void WriteData(int size, uint val)
    {
        if (!pendingWrite)
            return;
        for (int i = 0; i < size && bufPos < buf.Length; i++)
            buf[bufPos++] = (byte)(val >> (8 * i));
        if (bufPos >= buf.Length)
        {
            disk.WriteSector(writeLba, buf, 0);
            writeLba++;
            writeSectorsLeft--;
            bufPos = 0;
            // 1セクタ処理完了ごとに INTRQ を上げる(次ブロック要求 or 全完了)。
            IrqPending = true;
            if (writeSectorsLeft <= 0)
            {
                pendingWrite = false;
                status = DRDY | DSC;
            }
        }
    }

    // IDENTIFY DEVICE の応答(512バイト)。LBA 対応・容量・モデル名など最小限のフィールドのみ。
    byte[] BuildIdentify()
    {
        var w = new ushort[256];
        w[0] = 0x0040;                       // ATA デバイス
        w[1] = 16383; w[3] = 16; w[6] = 63;  // レガシー CHS
        SetAtaString(w, 10, 10, "EMU86-0000000000-JA1"); // シリアル(20文字)
        SetAtaString(w, 23, 4, "1.0     ");              // ファームウェア(8文字)
        SetAtaString(w, 27, 20, "Emu86 Virtual Disk".PadRight(40)); // モデル(40文字)
        w[49] = 0x0200;                      // LBA 対応
        w[53] = 0x0001;                      // words 54-58 有効
        w[54] = 16383; w[55] = 16; w[56] = 63;
        var chsSectors = (uint)Math.Min(disk.TotalSectors, 16383L * 16 * 63);
        w[57] = (ushort)chsSectors; w[58] = (ushort)(chsSectors >> 16);
        var lba28 = (uint)Math.Min(disk.TotalSectors, 0x0FFFFFFF);
        w[60] = (ushort)lba28; w[61] = (ushort)(lba28 >> 16);

        var bytes = new byte[512];
        for (int i = 0; i < 256; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), w[i]);
        return bytes;
    }

    // ATA 文字列はワード内でバイトスワップされた ASCII。
    static void SetAtaString(ushort[] w, int start, int words, string s)
    {
        for (int i = 0; i < words; i++)
        {
            var c0 = i * 2 < s.Length ? s[i * 2] : ' ';
            var c1 = i * 2 + 1 < s.Length ? s[i * 2 + 1] : ' ';
            w[start + i] = (ushort)((c0 << 8) | c1);
        }
    }
}
