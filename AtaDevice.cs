using System.Text;
using static Emu86.Writers;

namespace Emu86;

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

    // ステータスビット(コマンドはその場で完了するので、BSY(0x80)は立たない)
    const byte DRDY = 0x40, DSC = 0x10, DRQ = 0x08, ERR = 0x01;

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

    // レジスタ(Lens)の表: ポート → 読み出しと書き込み(null なら書けない)。0x1F1 は読むとエラー、書くとフィーチャ。
    // ステータス(0x1F7)は読むと INTRQ を降ろし、書くとコマンド。0x3F6 は代替ステータス(INTRQ は降ろさない)とデバイス制御。
    // スレーブ(不在)を選択中のステータスは 0。表にないポートは 0xFF を返し、書き込みは無視する。
    readonly record struct Register(Func<AtaDevice, byte> Read, Action<AtaDevice, byte> Write = null);

    static readonly Dictionary<int, Register> Registers = new()
    {
        [0x1F1] = new(ata => ata.error, (ata, value) => ata.feature = value),
        [0x1F2] = new(ata => ata.sectorCount, (ata, value) => ata.sectorCount = value),
        [0x1F3] = new(ata => ata.lbaLow, (ata, value) => ata.lbaLow = value),
        [0x1F4] = new(ata => ata.lbaMid, (ata, value) => ata.lbaMid = value),
        [0x1F5] = new(ata => ata.lbaHigh, (ata, value) => ata.lbaHigh = value),
        [0x1F6] = new(ata => ata.drive, (ata, value) => ata.drive = value),
        [0x1F7] = new(ata => { if (ata.SlaveSelected) return 0; ata.IrqPending = false; return ata.status; }, (ata, value) => ata.IssueCommand(value)),
        [0x3F6] = new(ata => ata.SlaveSelected ? (byte)0 : ata.status, (ata, value) => ata.DeviceControl(value)),
    };

    public byte ReadReg(int port)
    {
        if (Log && LogReads && readLogLeft > 0) { readLogLeft--; Console.Error.WriteLine($"[ata] rd {port:x3} (status={status:x2} drive={drive:x2})"); }
        return Registers.TryGetValue(port, out var register) ? register.Read(this) : (byte)0xFF;
    }

    public void WriteReg(int port, byte val)
    {
        if (Registers.TryGetValue(port, out var register)) register.Write?.Invoke(this, val);
    }

    // コマンドレジスタ(0x1F7)への書き込み。スレーブは不在なので無視する。
    void IssueCommand(byte command)
    {
        if (SlaveSelected) { L($"cmd {command:x2} to slave (absent) drive={drive:x2}"); return; }
        L($"cmd {command:x2} feat={feature:x2} cnt={sectorCount:x2} lba={lbaHigh:x2}{lbaMid:x2}{lbaLow:x2} drive={drive:x2}");
        Command(command);
    }

    // デバイス制御(0x3F6)。SRST(bit2)でソフトウェアリセット: 状態を初期化し、ATA デバイスシグネチャを置く。
    void DeviceControl(byte value)
    {
        L($"devctl {value:x2}");
        if ((value & 0x04) == 0) return;
        status = DRDY | DSC;
        error = 1;
        sectorCount = 1; lbaLow = 1; lbaMid = 0; lbaHigh = 0;
        buf = []; bufPos = 0; pendingWrite = false;
    }

    // コマンド表: (コマンド番号の組, 動作)を番号ごとに展開する(オペコード表と同じ形)。
    // 表にないコマンド(DMA 系 0xC8/0x25/0xCA/0x35 等)は abort し、libata は PIO へ後退する。
    static readonly (byte[] codes, Action<AtaDevice> action)[] CommandGroups =
    [
        ([0xEC], ata => ata.Respond(ata.BuildIdentify())),                       // IDENTIFY DEVICE
        ([0xA1], ata => ata.Abort()),                                            // IDENTIFY PACKET DEVICE(ATAPI ではないので abort)
        ([0x20, 0x21, 0x24, 0xC4], ata => ata.BeginRead()),                      // READ SECTORS (PIO) / EXT / MULTIPLE(単一扱い)
        ([0x30, 0x31, 0x34, 0xC5], ata => ata.BeginWrite()),                     // WRITE SECTORS (PIO) / EXT / MULTIPLE(単一扱い)
        // SET FEATURES / FLUSH CACHE(EXT) / READ VERIFY / INIT DEV PARAMS / NOP / IDLE:
        // libata のプローブ・設定で発行される非データコマンド。成功として完了 → INTRQ。
        ([0xEF, 0xE7, 0xEA, 0x40, 0x41, 0x91, 0x00, 0xE1, 0xE0], ata => ata.Complete()),
    ];

    static readonly Dictionary<byte, Action<AtaDevice>> Commands =
        (from entry in CommandGroups
         from code in entry.codes
         select (code, entry.action))
        .ToDictionary(pair => pair.code, pair => pair.action);

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

    // IDENTIFY DEVICE の応答(512 バイト = 256 ワード。Word はワード番号での位置)。LBA 対応・容量・モデル名など最小限の項目のみ。
    byte[] BuildIdentify() => Sequence(
        Word(0, U16(0x0040)),                                                   // ATA デバイス
        Word(1, U16(16383)), Word(3, U16(16)), Word(6, U16(63)),                // レガシー CHS(シリンダ、ヘッド、セクタ)
        Word(10, AtaString("EMU86-0000000000-JA1", 20)),                        // シリアル
        Word(23, AtaString("1.0", 8)),                                          // ファームウェア
        Word(27, AtaString("Emu86 Virtual Disk", 40)),                          // モデル
        Word(49, U16(0x0200)),                                                  // LBA 対応
        Word(53, U16(0x0001)),                                                  // words 54-58 有効
        Word(54, U16(16383)), Word(55, U16(16)), Word(56, U16(63)),             // 現在の CHS
        Word(57, U32((uint)Math.Min(disk.TotalSectors, 16383L * 16 * 63))),     // CHS での総セクタ数
        Word(60, U32((uint)Math.Min(disk.TotalSectors, 0x0FFFFFFF))))           // LBA28 での総セクタ数
        .Build(512);

    // IDENTIFY の項目の位置(ワード番号)。ATAPI の IDENTIFY PACKET DEVICE(AtapiDevice)も同じ形。
    internal static Writer Word(int index, Writer writer) => At(index * 2, writer);

    // ATA の文字列: length 文字まで空白で埋めた ASCII を、ワード(2 バイト)ごとに前後を入れ替えて置く。
    internal static Writer AtaString(string text, int length) => Bytes(
    [
        .. from pair in Encoding.ASCII.GetBytes(text.PadRight(length)).Chunk(2)
           from value in new[] { pair[1], pair[0] }
           select value,
    ]);
}
