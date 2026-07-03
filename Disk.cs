using System.Buffers.Binary;

namespace Emu86;

// ベースイメージ(VHD、読み取り専用)+ 差分ファイルによる Copy-on-Write ディスク。
// 書き込みはすべて差分ファイルへ蓄積し、読み込みは差分にあるセクタを優先、
// 無いセクタはベースイメージから読む。ベースイメージ(と親VHD)は一切変更しない。
//
// VHD は 固定(2)/可変長(3)/差分(4) に対応。差分VHDは各ブロックのセクタビットマップ
// (MSBファースト: bit7 が先頭セクタ)を見て、ビット0のセクタは親VHDへ委譲する。
// 親は動的ヘッダの親ロケータ(同一フォルダ→絶対パスの順)で解決する。
//
// 差分ファイルはジャーナル形式: [LBA(8バイト、リトルエンディアン)][セクタデータ(512バイト)] の繰り返し。
// 起動時に全レコードを読み込み(後勝ち)、書き込み時は末尾に追記する。
public class DiskImage
{
    public const int SectorSize = 512;

    const uint BatUnallocated = 0xFFFFFFFF;
    const int DiskTypeDifferencing = 4;

    readonly FileStream baseImage;
    readonly FileStream diffFile;
    readonly Dictionary<long, byte[]> diff = [];

    public long TotalSectors { get; }

    // Dynamic/Differencing VHD: BAT(Block Allocation Table)でブロック位置を解決する。
    readonly bool dynamic;
    readonly int diskType;
    readonly uint[] bat = [];
    readonly int sectorsPerBlock;
    readonly int bitmapSectors;
    readonly DiskImage parent;                       // 差分VHDの親(見つからなければ null)
    readonly Dictionary<long, byte[]> bitmapCache = [];

    // diffPath=null は親VHDとして開く場合(差分ジャーナルなし、読み取り専用)。
    public DiskImage(string basePath, string diffPath = null)
    {
        baseImage = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var hdr = new byte[SectorSize];
        baseImage.ReadExactly(hdr);
        if (hdr.AsSpan(0, 8).SequenceEqual("conectix"u8))
        {
            // Dynamic/Differencing VHD: 先頭にフッタのコピー、その直後に動的ヘッダ(cxsparse)、BAT が続く。
            // フッタの値はビッグエンディアン。current size(offset 0x30)が仮想ディスクサイズ。
            dynamic = true;
            diskType = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x3C));
            TotalSectors = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(0x30)) / SectorSize;

            var dyn = new byte[1024];
            baseImage.ReadExactly(dyn); // 動的ヘッダ(offset 512)
            var tableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(dyn.AsSpan(0x10));
            var maxEntries = (int)BinaryPrimitives.ReadUInt32BigEndian(dyn.AsSpan(0x1C));
            var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(dyn.AsSpan(0x20));
            sectorsPerBlock = blockSize / SectorSize;
            bitmapSectors = (sectorsPerBlock / 8 + SectorSize - 1) / SectorSize;

            bat = new uint[maxEntries];
            baseImage.Seek(tableOffset, SeekOrigin.Begin);
            var raw = new byte[maxEntries * 4];
            baseImage.ReadExactly(raw);
            for (int i = 0; i < maxEntries; i++)
                bat[i] = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(i * 4));

            if (diskType == DiskTypeDifferencing)
                parent = OpenParent(basePath, dyn);
        }
        else
        {
            // 固定 VHD / 生イメージ: ファイルをそのままセクタ列として扱う。
            TotalSectors = baseImage.Length / SectorSize;
        }

        if (diffPath != null)
        {
            // 差分ファイルを読み込む(存在しなければ新規作成)
            diffFile = new FileStream(diffPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var rec = new byte[8 + SectorSize];
            while (diffFile.Read(rec, 0, rec.Length) == rec.Length)
                diff[BitConverter.ToInt64(rec, 0)] = rec[8..];
            diffFile.Seek(0, SeekOrigin.End);
        }
    }

    // 差分VHDの親を開く。候補: 子と同じフォルダの親ファイル名 → ロケータの絶対パス。
    static DiskImage OpenParent(string childPath, byte[] dyn)
    {
        var candidates = new List<string>();

        // 動的ヘッダの親名(UTF-16BE)からファイル名を取り、子と同じフォルダを探す
        var parentName = System.Text.Encoding.BigEndianUnicode.GetString(dyn, 0x40, 0x200).TrimEnd('\0');
        if (parentName.Length > 0)
            candidates.Add(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(childPath)) ?? ".", Path.GetFileName(parentName)));

        // 親ロケータ W2ku(絶対パス、UTF-16LE)
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

    // ブロックのセクタビットマップ(キャッシュ付き)。
    byte[] BlockBitmap(long block, uint entry)
    {
        if (bitmapCache.TryGetValue(block, out var b))
            return b;
        b = new byte[bitmapSectors * SectorSize];
        baseImage.Seek((long)entry * SectorSize, SeekOrigin.Begin);
        baseImage.ReadExactly(b);
        bitmapCache[block] = b;
        return b;
    }

    // 読み込み: 差分にあればそれを、無ければベースイメージ(と親)から。
    public void ReadSector(long lba, byte[] buf, int offset)
    {
        if (diff.TryGetValue(lba, out var d))
        {
            d.CopyTo(buf, offset);
            return;
        }
        ReadBase(lba, buf, offset);
    }

    // ベースイメージからの読み込み(差分VHDのビットマップ・親チェーン解決込み)。
    void ReadBase(long lba, byte[] buf, int offset)
    {
        if (lba < 0 || lba >= TotalSectors)
        {
            Array.Clear(buf, offset, SectorSize);
            return;
        }

        if (!dynamic)
        {
            baseImage.Seek(lba * SectorSize, SeekOrigin.Begin);
            baseImage.ReadExactly(buf, offset, SectorSize);
            return;
        }

        var block = lba / sectorsPerBlock;
        var sector = (int)(lba % sectorsPerBlock);
        var entry = bat[block];

        // 未割り当てブロック、または差分VHDでビットが0のセクタは親から読む(親が無ければゼロ)
        var fromParent = entry == BatUnallocated ||
            (diskType == DiskTypeDifferencing &&
             (BlockBitmap(block, entry)[sector / 8] >> (7 - sector % 8) & 1) == 0);
        if (fromParent)
        {
            if (parent != null)
                parent.ReadBase(lba, buf, offset);
            else
                Array.Clear(buf, offset, SectorSize);
            return;
        }

        baseImage.Seek(((long)entry + bitmapSectors + sector) * SectorSize, SeekOrigin.Begin);
        baseImage.ReadExactly(buf, offset, SectorSize);
    }

    // 書き込み: ベースイメージには触れず、差分(メモリ+ジャーナル追記)にのみ書く。
    public void WriteSector(long lba, byte[] data, int offset)
    {
        var copy = new byte[SectorSize];
        Array.Copy(data, offset, copy, 0, SectorSize);
        diff[lba] = copy;

        Span<byte> key = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(key, lba);
        diffFile.Write(key);
        diffFile.Write(copy);
        diffFile.Flush();
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
