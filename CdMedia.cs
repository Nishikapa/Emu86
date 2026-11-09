namespace Emu86;

// CD/DVD の媒体(読み取り専用、2048 バイトセクタ)。ISO イメージのファイルか、ホストの光学ドライブを開く。
// ドライブは "D:" のようにドライブ文字で指定し、ボリュームを直接(\\.\D:)セクタ単位で読む(管理者権限は要らない)。
// 直接読むデバイスはセクタ境界に揃った位置と長さでしか読めないので、読み取りは常にチャンク(64KB)単位で行い、
// 直近のチャンクを覚えておく(順に読まれることが多く、USB 接続のドライブは 1 回の読み取りが遅い)。
public sealed class CdMedia : IDisposable
{
    public const int SectorSize = 2048;
    const int ChunkSectors = 32;

    readonly FileStream file;
    readonly byte[] chunk = new byte[ChunkSectors * SectorSize];
    long chunkStart = -1;   // chunk に入っている先頭セクタ(-1 = 空)
    int chunkLength;        // chunk に入っているセクタ数

    // 指定されたままのパス(スナップショットの再開時に同じ媒体を開き直すための表示用)。
    public string Path { get; }
    public long TotalSectors { get; }

    public CdMedia(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        var drive = DriveOf(path);
        // bufferSize 1 = FileStream 自身のバッファを使わない(揃っていない位置での読み取りを起こさせない)。
        file = new FileStream(drive != null ? @"\\.\" + drive : path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1);
        // ドライブは、デバイスが申告する長さの末尾に読めない領域(ランアウト)を含むことがあるので、ボリュームの大きさを使う。
        TotalSectors = (drive != null ? new DriveInfo(drive).TotalSize : file.Length) / SectorSize;
    }

    // "D:" や "D:\" ならドライブ名("D:")、それ以外(ファイル)は null。
    static string DriveOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        return trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':' ? trimmed : null;
    }

    // lba から count セクタを buffer の offset へ読む。範囲外を含む読み取りは呼び出し側が先に弾く。
    public void Read(long lba, int count, byte[] buffer, int offset)
    {
        if (lba < 0 || count < 0 || lba + count > TotalSectors) throw new ArgumentOutOfRangeException(nameof(lba));
        for (var index = 0; index < count; index++)
        {
            var sector = lba + index;
            if (sector < chunkStart || sector >= chunkStart + chunkLength) Load(sector);
            Buffer.BlockCopy(chunk, (int)(sector - chunkStart) * SectorSize, buffer, offset + index * SectorSize, SectorSize);
        }
    }

    // sector を含むチャンクを読み込む(媒体の末尾では短くなる)。sector まで読めなければ読み取りエラー。
    void Load(long sector)
    {
        var start = sector - sector % ChunkSectors;
        var wanted = (int)Math.Min(ChunkSectors, TotalSectors - start) * SectorSize;
        chunkLength = 0;
        file.Seek(start * SectorSize, SeekOrigin.Begin);
        var total = 0;
        while (total < wanted && file.Read(chunk, total, wanted - total) is > 0 and var read) total += read;
        chunkStart = start;
        chunkLength = total / SectorSize;
        if (sector >= chunkStart + chunkLength) throw new IOException($"CD medium: sector {sector} is not readable");
    }

    public void Dispose() => file.Dispose();
}
