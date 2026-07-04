using System.Buffers.Binary;

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
public class DiskImage
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

    public long TotalSectors { get; }

    // VHD (conectix)
    readonly bool vhd;
    readonly int vhdType;
    readonly uint[] vhdBat = [];
    readonly Dictionary<long, byte[]> vhdBitmapCache = [];

    // VHDX (vhdxfile)
    readonly bool vhdx;
    readonly ulong[] vhdxBat = [];
    readonly long vhdxBatOffset;
    readonly int vhdxChunkRatio;
    readonly Guid dataWriteGuid;
    readonly Dictionary<long, byte[]> vhdxBitmapCache = [];
    long nextAlloc; // 書き込み用: 次のブロック割り当て位置(1MB境界)

    // 共通
    readonly int sectorsPerBlock;
    readonly int vhdBitmapSectors;

    public DiskImage(string path, bool writable = false)
    {
        this.writable = writable;
        file = new FileStream(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);

        var hdr = new byte[SectorSize];
        file.ReadExactly(hdr);
        if (hdr.AsSpan(0, 8).SequenceEqual("conectix"u8))
        {
            // VHD: 先頭にフッタのコピー(可変長/差分)。値はビッグエンディアン。
            vhd = true;
            vhdType = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x3C));
            TotalSectors = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(0x30)) / SectorSize;

            var dyn = new byte[1024];
            file.ReadExactly(dyn); // 動的ヘッダ(offset 512)
            var tableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(dyn.AsSpan(0x10));
            var maxEntries = (int)BinaryPrimitives.ReadUInt32BigEndian(dyn.AsSpan(0x1C));
            var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(dyn.AsSpan(0x20));
            sectorsPerBlock = blockSize / SectorSize;
            vhdBitmapSectors = (sectorsPerBlock / 8 + SectorSize - 1) / SectorSize;

            vhdBat = new uint[maxEntries];
            var raw = new byte[maxEntries * 4];
            file.Seek(tableOffset, SeekOrigin.Begin);
            file.ReadExactly(raw);
            for (int i = 0; i < maxEntries; i++)
                vhdBat[i] = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(i * 4));

            if (vhdType == VhdTypeDifferencing)
                parent = OpenVhdParent(path, dyn);
        }
        else if (hdr.AsSpan(0, 8).SequenceEqual("vhdxfile"u8))
        {
            vhdx = true;

            // ヘッダ1(0x10000)から DataWriteGuid を得る(親子リンクの検証用)
            var vh = new byte[4096];
            file.Seek(0x10000, SeekOrigin.Begin);
            file.ReadExactly(vh);
            dataWriteGuid = new Guid(vh.AsSpan(0x20, 16));

            // リージョンテーブル(0x30000)
            var rt = new byte[0x10000];
            file.Seek(0x30000, SeekOrigin.Begin);
            file.ReadExactly(rt);
            if (!rt.AsSpan(0, 4).SequenceEqual("regi"u8))
                throw new InvalidDataException("VHDX: region table not found");
            long metaOffset = 0;
            int metaLength = 0;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(rt.AsSpan(8));
            for (int i = 0; i < count; i++)
            {
                var e = rt.AsSpan(16 + i * 32, 32);
                var g = new Guid(e[..16]);
                if (g == BatRegionGuid) vhdxBatOffset = BinaryPrimitives.ReadInt64LittleEndian(e[16..]);
                if (g == MetaRegionGuid) (metaOffset, metaLength) = (BinaryPrimitives.ReadInt64LittleEndian(e[16..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(e[24..]));
            }

            // メタデータ領域
            var meta = new byte[metaLength];
            file.Seek(metaOffset, SeekOrigin.Begin);
            file.ReadExactly(meta);
            if (!meta.AsSpan(0, 8).SequenceEqual("metadata"u8))
                throw new InvalidDataException("VHDX: metadata region not found");
            int blockSize = 0, logicalSector = 512;
            long diskSize = 0;
            var hasParent = false;
            var parentLinkage = Guid.Empty;
            var parentPaths = new List<string>();
            var mcount = BinaryPrimitives.ReadUInt16LittleEndian(meta.AsSpan(10));
            for (int i = 0; i < mcount; i++)
            {
                var e = meta.AsSpan(32 + i * 32, 32);
                var g = new Guid(e[..16]);
                var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
                if (g == FileParamsGuid)
                {
                    blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(off));
                    hasParent = (meta[off + 4] & 2) != 0;
                }
                else if (g == DiskSizeGuid)
                    diskSize = BinaryPrimitives.ReadInt64LittleEndian(meta.AsSpan(off));
                else if (g == LogicalSectorGuid)
                    logicalSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(off));
                else if (g == ParentLocatorGuid)
                    ParseParentLocator(meta.AsSpan(off), out parentLinkage, parentPaths);
            }
            if (logicalSector != SectorSize)
                throw new InvalidDataException($"VHDX: unsupported logical sector size {logicalSector}");

            sectorsPerBlock = blockSize / SectorSize;
            vhdxChunkRatio = (int)((1L << 23) * logicalSector / blockSize);
            TotalSectors = diskSize / SectorSize;

            // BAT(ペイロード chunkRatio 個ごとにビットマップエントリが挟まる)
            var totalBlocks = (diskSize + blockSize - 1) / blockSize;
            var batEntries = (int)(totalBlocks + (totalBlocks + vhdxChunkRatio - 1) / vhdxChunkRatio);
            vhdxBat = new ulong[batEntries];
            var braw = new byte[batEntries * 8];
            file.Seek(vhdxBatOffset, SeekOrigin.Begin);
            file.ReadExactly(braw);
            for (int i = 0; i < batEntries; i++)
                vhdxBat[i] = BinaryPrimitives.ReadUInt64LittleEndian(braw.AsSpan(i * 8));

            nextAlloc = (file.Length + 0xFFFFF) & ~0xFFFFFL;

            if (hasParent)
                parent = OpenVhdxParent(path, parentLinkage, parentPaths);
        }
        else
        {
            // 生イメージ: そのままセクタ列として扱う。
            TotalSectors = file.Length / SectorSize;
        }

        if (writable && !vhdx)
            throw new InvalidOperationException("writable disk must be an AVHDX overlay");
    }

    public void Close()
    {
        parent?.Close();
        file.Close();
    }

    // ============ 親解決 ============

    // 差分VHDの親を開く。候補: 子と同じフォルダの親ファイル名 → ロケータの絶対パス。
    static DiskImage OpenVhdParent(string childPath, byte[] dyn)
    {
        var candidates = new List<string>();

        var parentName = System.Text.Encoding.BigEndianUnicode.GetString(dyn, 0x40, 0x200).TrimEnd('\0');
        if (parentName.Length > 0)
            candidates.Add(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(childPath)) ?? ".", Path.GetFileName(parentName)));

        for (int i = 0; i < 8; i++)
        {
            var e = dyn.AsSpan(0x240 + i * 24, 24);
            if (!e[..4].SequenceEqual("W2ku"u8))
                continue;
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(e[8..12]);
            var off = (long)BinaryPrimitives.ReadUInt64BigEndian(e[16..24]);
            using var f = new FileStream(childPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buf = new byte[len];
            f.Seek(off, SeekOrigin.Begin);
            f.ReadExactly(buf);
            candidates.Add(System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0'));
        }

        foreach (var path in candidates.Where(File.Exists))
        {
            Console.Error.WriteLine($"[disk] parent VHD: {path}");
            return new DiskImage(path);
        }

        Console.Error.WriteLine($"[disk] WARNING: parent VHD not found: {string.Join(" / ", candidates)} (missing sectors read as zero)");
        return null;
    }

    // VHDX ペアレントロケータの解析: parent_linkage(GUID) とパス候補を取り出す。
    static void ParseParentLocator(ReadOnlySpan<byte> loc, out Guid linkage, List<string> paths)
    {
        linkage = Guid.Empty;
        var kvCount = BinaryPrimitives.ReadUInt16LittleEndian(loc[18..]);
        for (int i = 0; i < kvCount; i++)
        {
            var e = loc[(20 + i * 12)..];
            var keyOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(e);
            var valOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(e[8..]);
            var valLen = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
            var key = System.Text.Encoding.Unicode.GetString(loc.Slice(keyOff, keyLen));
            var val = System.Text.Encoding.Unicode.GetString(loc.Slice(valOff, valLen));
            if (key == "parent_linkage")
                linkage = Guid.Parse(val.Trim('{', '}'));
            else if (key is "relative_path" or "absolute_win32_path" or "volume_path")
                paths.Add(val);
        }
    }

    static DiskImage OpenVhdxParent(string childPath, Guid linkage, List<string> paths)
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

    public void ReadSector(long lba, byte[] buf, int offset)
    {
        if (lba < 0 || lba >= TotalSectors)
        {
            Array.Clear(buf, offset, SectorSize);
            return;
        }

        if (vhd) ReadVhd(lba, buf, offset);
        else if (vhdx) ReadVhdx(lba, buf, offset);
        else
        {
            file.Seek(lba * SectorSize, SeekOrigin.Begin);
            file.ReadExactly(buf, offset, SectorSize);
        }
    }

    void ReadFromParentOrZero(long lba, byte[] buf, int offset)
    {
        if (parent != null)
            parent.ReadSector(lba, buf, offset);
        else
            Array.Clear(buf, offset, SectorSize);
    }

    void ReadVhd(long lba, byte[] buf, int offset)
    {
        var block = lba / sectorsPerBlock;
        var sector = (int)(lba % sectorsPerBlock);
        var entry = vhdBat[block];

        // 未割り当てブロック、または差分VHDでビットが0のセクタは親から読む。
        // VHD のビットマップは MSB ファースト(bit7 が先頭セクタ)。
        var fromParent = entry == VhdBatUnallocated ||
            (vhdType == VhdTypeDifferencing &&
             (VhdBlockBitmap(block, entry)[sector / 8] >> (7 - sector % 8) & 1) == 0);
        if (fromParent)
        {
            ReadFromParentOrZero(lba, buf, offset);
            return;
        }

        file.Seek(((long)entry + vhdBitmapSectors + sector) * SectorSize, SeekOrigin.Begin);
        file.ReadExactly(buf, offset, SectorSize);
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

    void ReadVhdx(long lba, byte[] buf, int offset)
    {
        var block = lba / sectorsPerBlock;
        var entry = vhdxBat[block + block / vhdxChunkRatio];
        var state = (int)(entry & 7);
        var fileOffset = (long)(entry & ~0xFFFFFUL);

        switch (state)
        {
            case PayloadFullyPresent:
                file.Seek(fileOffset + lba % sectorsPerBlock * SectorSize, SeekOrigin.Begin);
                file.ReadExactly(buf, offset, SectorSize);
                return;

            case PayloadPartiallyPresent:
                // VHDX のビットマップは LSB ファースト(bit0 が先頭セクタ)。チャンク単位(1MB)。
                var bmp = VhdxChunkBitmap(block / vhdxChunkRatio);
                var pos = lba - block / vhdxChunkRatio * (long)vhdxChunkRatio * sectorsPerBlock;
                if (bmp != null && (bmp[pos / 8] >> (int)(pos % 8) & 1) != 0)
                {
                    file.Seek(fileOffset + lba % sectorsPerBlock * SectorSize, SeekOrigin.Begin);
                    file.ReadExactly(buf, offset, SectorSize);
                    return;
                }
                ReadFromParentOrZero(lba, buf, offset);
                return;

            case 2: // BLOCK_ZERO
                Array.Clear(buf, offset, SectorSize);
                return;

            default: // NOT_PRESENT / UNDEFINED / UNMAPPED
                ReadFromParentOrZero(lba, buf, offset);
                return;
        }
    }

    // チャンクのセクタビットマップ(1MB)。未割り当てなら null。
    byte[] VhdxChunkBitmap(long chunk)
    {
        if (vhdxBitmapCache.TryGetValue(chunk, out var b))
            return b;
        var entry = vhdxBat[chunk * (vhdxChunkRatio + 1) + vhdxChunkRatio];
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
        var chunk = block / vhdxChunkRatio;
        var payloadIdx = block + chunk;
        var bitmapIdx = chunk * (vhdxChunkRatio + 1) + vhdxChunkRatio;

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
        var pos = lba - chunk * (long)vhdxChunkRatio * sectorsPerBlock;
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
            if (OverlayIsCurrent(overlayPath))
                return;
            var stale = overlayPath + ".old";
            File.Delete(stale);
            File.Move(overlayPath, stale);
            Console.Error.WriteLine($"[disk] overlay {overlayPath} is from an older format; moved to {stale} and recreating");
        }

        var p = new DiskImage(basePath);
        var blockSize = p.vhdx || p.vhd ? p.sectorsPerBlock * SectorSize : 0x200000;
        CreateAvhdx(overlayPath, basePath, p.TotalSectors * SectorSize, blockSize, p.dataWriteGuid);
        p.Close();
        Console.Error.WriteLine($"[disk] created overlay: {overlayPath} (parent: {basePath})");
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
        var chunkRatio = (int)((1L << 23) * SectorSize / blockSize);
        var totalBlocks = (virtualSize + blockSize - 1) / blockSize;
        var batEntries = totalBlocks + (totalBlocks + chunkRatio - 1) / chunkRatio;
        var batLength = (batEntries * 8 + 0xFFFFF) & ~0xFFFFFL;

        var image = new byte[0x300000 + batLength];
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
public class AtaDevice(DiskImage disk)
{
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

    // スナップショット保存/復元。接続先の DiskImage 自体は書き込みのたびに
    // ファイルへ反映済みのため、ここでは PIO レジスタとバッファのみを扱う。
    public void SaveState(BinaryWriter w)
    {
        w.Write(feature); w.Write(sectorCount); w.Write(lbaLow); w.Write(lbaMid); w.Write(lbaHigh); w.Write(drive);
        w.Write(status); w.Write(error);
        w.Write(buf.Length);
        w.Write(buf);
        w.Write(bufPos);
        w.Write(pendingWrite);
        w.Write(writeLba);
        w.Write(writeSectorsLeft);
    }

    public void LoadState(BinaryReader r)
    {
        feature = r.ReadByte(); sectorCount = r.ReadByte(); lbaLow = r.ReadByte();
        lbaMid = r.ReadByte(); lbaHigh = r.ReadByte(); drive = r.ReadByte();
        status = r.ReadByte(); error = r.ReadByte();
        buf = r.ReadBytes(r.ReadInt32());
        bufPos = r.ReadInt32();
        pendingWrite = r.ReadBoolean();
        writeLba = r.ReadInt64();
        writeSectorsLeft = r.ReadInt32();
    }

    public byte ReadReg(int port) =>
        port switch
        {
            0x1F1 => error,
            0x1F2 => sectorCount,
            0x1F3 => lbaLow,
            0x1F4 => lbaMid,
            0x1F5 => lbaHigh,
            0x1F6 => drive,
            // スレーブは存在しない: ステータス 0 を返して SeaBIOS に「デバイスなし」と判定させる
            0x1F7 or 0x3F6 => SlaveSelected ? (byte)0 : status,
            _ => 0xFF,
        };

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
            case 0x1F7: if (!SlaveSelected) Command(val); break;
            case 0x3F6:
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

    void Command(byte cmd)
    {
        error = 0;
        switch (cmd)
        {
            case 0xEC: // IDENTIFY DEVICE
                buf = BuildIdentify();
                bufPos = 0;
                status = DRDY | DSC | DRQ;
                break;

            case 0xA1: // IDENTIFY PACKET DEVICE (ATAPI ではないので abort)
                error = 0x04;
                status = DRDY | DSC | ERR;
                break;

            case 0x20 or 0x21: // READ SECTORS (PIO)
                {
                    var count = sectorCount == 0 ? 256 : sectorCount;
                    var lba = CurrentLba();
                    buf = new byte[count * DiskImage.SectorSize];
                    for (int i = 0; i < count; i++)
                        disk.ReadSector(lba + i, buf, i * DiskImage.SectorSize);
                    bufPos = 0;
                    status = DRDY | DSC | DRQ;
                }
                break;

            case 0x30 or 0x31: // WRITE SECTORS (PIO)
                {
                    writeSectorsLeft = sectorCount == 0 ? 256 : sectorCount;
                    writeLba = CurrentLba();
                    buf = new byte[DiskImage.SectorSize];
                    bufPos = 0;
                    pendingWrite = true;
                    status = DRDY | DSC | DRQ;
                }
                break;

            default: // 未対応コマンドは abort
                error = 0x04;
                status = DRDY | DSC | ERR;
                break;
        }
    }

    long CurrentLba() =>
        lbaLow | ((long)lbaMid << 8) | ((long)lbaHigh << 16) | ((long)(drive & 0x0F) << 24);

    // データポート(0x1F0)の読み出し。size バイトをバッファから取り出す。
    public uint ReadData(int size)
    {
        uint v = 0;
        for (int i = 0; i < size; i++)
            v |= (uint)(bufPos < buf.Length ? buf[bufPos++] : 0) << (8 * i);
        if (bufPos >= buf.Length && !pendingWrite)
            status = DRDY | DSC; // 転送完了
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
