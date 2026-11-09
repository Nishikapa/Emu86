using static Emu86.Writers;

namespace Emu86;

// ATAPI の CD/DVD-ROM ドライブ(PIO)。第 2 IDE チャネル(ポート 0x170-0x177 と 0x376、IRQ15)のマスタとして応答する。
// レジスタの並びは ATA と同じで、媒体へのコマンドは PACKET(0xA0)に続けてデータポートへ書かれる 12 バイトの
// SCSI/MMC コマンドとして受ける。応答のデータは、ホストがバイト数レジスタ(シリンダレジスタ)で指定した上限ごとに区切って
// DRQ を立て、最後に状態(割り込み理由 = IO|CoD)を返す。区切りごとと完了時に INTRQ を上げる(パケットの受け付けでは上げない)。
// SeaBIOS の検出・El Torito ブートと、OS の ATAPI ドライバ(INQUIRY、READ、MODE SENSE、GET CONFIGURATION など)に応える。
public sealed class AtapiDevice(CdMedia media) : IDisposable
{
    public void Dispose() => media?.Dispose();

    public string MediaPath => media?.Path;

    // ステータスビット
    const byte DRDY = 0x40, DSC = 0x10, DRQ = 0x08, ERR = 0x01;

    // 割り込み理由(セクタ数レジスタ): bit0 CoD(1 = コマンドか状態、0 = データ)、bit1 IO(1 = デバイスからホストへ)。
    const byte ReasonPacket = 0x01, ReasonDataIn = 0x02, ReasonStatus = 0x03;

    // センスキーと追加センスコード
    const byte NotReady = 0x02, MediumError = 0x03, IllegalRequest = 0x05;
    const byte InvalidCommand = 0x20, LbaOutOfRange = 0x21, InvalidField = 0x24, IncompatibleFormat = 0x30,
        SavingNotSupported = 0x39, MediumNotPresent = 0x3A, RemovalPrevented = 0x53, ReadError = 0x11;

    // 1 コマンドで読めるセクタ数の上限(READ(10) の上限)。
    const int MaxReadSectors = 0xFFFF;

    // リセット直後は ATAPI のシグネチャ(セクタ数 1、LBA 下位 1、バイト数 0xEB14)。
    byte features, reason = 1, lbaLow = 1, countLow = 0x14, countHigh = 0xEB, drive;
    byte status = DRDY | DSC;
    byte error = 1;
    byte control;       // デバイス制御(bit1 = nIEN、bit2 = SRST)
    bool interrupt;     // 割り込み要求(nIEN が立っているあいだは線に出ない)

    // 転送中のデータ: buffer の [position, chunkEnd) が今の DRQ で読める範囲、length が応答全体の長さ。
    byte[] buffer = [];
    int position, chunkEnd, length;
    int limit;              // PACKET のときにホストが指定した、1 回の DRQ で送る上限
    bool packetTransfer;    // パケットコマンドの応答(区切りごとに割り込み、最後に状態)か、ATA の 1 ブロック(IDENTIFY)か

    // PACKET を受けた後、12 バイトのコマンドを待っている。
    bool awaitingPacket;
    readonly byte[] packet = new byte[12];
    int packetLength;

    // 直前のコマンドのセンス(REQUEST SENSE が返す)と、取り出しの禁止。
    byte senseKey, senseCode, senseQualifier;
    bool locked;

    bool SlaveSelected => (drive & 0x10) != 0;

    // INTRQ 相当。ステータスレジスタ(0x177)の読み出しと、配送で降りる。
    public bool IrqPending => interrupt && (control & 0x02) == 0;

    public void AcknowledgeIrq() => interrupt = false;

    // --atalog: コマンドを標準エラーへ記録する(OS のプローブ手順の調査用)。
    public bool Log;
    void L(string message) { if (Log) Console.Error.WriteLine("[atapi] " + message); }

    static readonly Codec<AtapiDevice> Layout = new Codec<AtapiDevice>()
        .U8(d => d.features, (d, v) => d.features = v)
        .U8(d => d.reason, (d, v) => d.reason = v)
        .U8(d => d.lbaLow, (d, v) => d.lbaLow = v)
        .U8(d => d.countLow, (d, v) => d.countLow = v)
        .U8(d => d.countHigh, (d, v) => d.countHigh = v)
        .U8(d => d.drive, (d, v) => d.drive = v)
        .U8(d => d.status, (d, v) => d.status = v)
        .U8(d => d.error, (d, v) => d.error = v)
        .U8(d => d.control, (d, v) => d.control = v)
        .Bool(d => d.interrupt, (d, v) => d.interrupt = v)
        .ByteArray(d => d.buffer, (d, v) => d.buffer = v, MaxReadSectors * CdMedia.SectorSize)
        .I32(d => d.position, (d, v) => d.position = v)
        .I32(d => d.chunkEnd, (d, v) => d.chunkEnd = v)
        .I32(d => d.length, (d, v) => d.length = v)
        .I32(d => d.limit, (d, v) => d.limit = v)
        .Bool(d => d.packetTransfer, (d, v) => d.packetTransfer = v)
        .Bool(d => d.awaitingPacket, (d, v) => d.awaitingPacket = v)
        .Bytes(d => d.packet)
        .I32(d => d.packetLength, (d, v) => d.packetLength = v)
        .U8(d => d.senseKey, (d, v) => d.senseKey = v)
        .U8(d => d.senseCode, (d, v) => d.senseCode = v)
        .U8(d => d.senseQualifier, (d, v) => d.senseQualifier = v)
        .Bool(d => d.locked, (d, v) => d.locked = v)
        .Check(d => d.position >= 0 && d.position <= d.chunkEnd && d.chunkEnd <= d.length && d.length <= d.buffer.Length
            && d.packetLength is >= 0 and <= 12, "Invalid ATAPI transfer state");

    public void SaveState(BinaryWriter writer) => Layout.Save(writer, this);

    public void LoadState(BinaryReader reader) => Layout.Load(reader, this, SnapshotStore.Version);

    // レジスタ(Lens)の表: ポート → 読み出しと書き込み。0x171 は読むとエラー、書くとフィーチャ。0x172 は割り込み理由、
    // 0x174/0x175 は転送バイト数。ステータス(0x177)は読むと INTRQ を降ろし、書くとコマンド。0x376 は代替ステータスとデバイス制御。
    // スレーブ(不在)を選択中のステータスは 0。
    readonly record struct Register(Func<AtapiDevice, byte> Read, Action<AtapiDevice, byte> Write);

    static readonly Dictionary<int, Register> Registers = new()
    {
        [0x171] = new(d => d.error, (d, value) => d.features = value),
        [0x172] = new(d => d.reason, (d, value) => d.reason = value),
        [0x173] = new(d => d.lbaLow, (d, value) => d.lbaLow = value),
        [0x174] = new(d => d.countLow, (d, value) => d.countLow = value),
        [0x175] = new(d => d.countHigh, (d, value) => d.countHigh = value),
        [0x176] = new(d => d.drive, (d, value) => d.drive = value),
        [0x177] = new(d => { if (d.SlaveSelected) return 0; d.interrupt = false; return d.status; }, (d, value) => d.IssueCommand(value)),
        [0x376] = new(d => d.SlaveSelected ? (byte)0 : d.status, (d, value) => d.DeviceControl(value)),
    };

    public byte ReadReg(int port) => Registers.TryGetValue(port, out var register) ? register.Read(this) : (byte)0xFF;

    public void WriteReg(int port, byte value)
    {
        if (Registers.TryGetValue(port, out var register)) register.Write(this, value);
    }

    // デバイス制御(0x376)。SRST(bit2)でソフトウェアリセット: 転送を捨て、シグネチャを置く(ATAPI は DRDY を立てない)。
    void DeviceControl(byte value)
    {
        L($"devctl {value:x2}");
        control = value;
        if ((value & 0x04) != 0) Reset();
    }

    void Signature()
    {
        reason = 1; lbaLow = 1; countLow = 0x14; countHigh = 0xEB;
        drive &= 0xF0;
    }

    void Reset()
    {
        Signature();
        StopTransfer();
        status = 0;
        error = 1;
        interrupt = false;
    }

    void StopTransfer()
    {
        buffer = [];
        position = chunkEnd = length = 0;
        packetTransfer = awaitingPacket = false;
        packetLength = 0;
    }

    // ATA コマンド(0x177 への書き込み)。スレーブは不在なので無視する。
    void IssueCommand(byte command)
    {
        if (SlaveSelected) { L($"cmd {command:x2} to slave (absent)"); return; }
        L($"cmd {command:x2} feat={features:x2} count={countHigh:x2}{countLow:x2}");
        interrupt = false;
        if (AtaCommands.TryGetValue(command, out var action)) action(this);
        else
        {
            L($"  -> unsupported ATA command {command:x2}: abort");
            Abort();
        }
    }

    // ATA コマンドの表。ATA のディスク向けコマンド(IDENTIFY DEVICE、READ SECTORS)はシグネチャを置いて abort する
    // (ホストはこれで ATAPI デバイスと見分ける)。表にないコマンドは abort。
    static readonly (byte[] codes, Action<AtapiDevice> action)[] AtaCommandGroups =
    [
        ([0xA0], d => d.BeginPacket()),                                             // PACKET
        ([0xA1], d => d.SendBlock(Identify)),                                       // IDENTIFY PACKET DEVICE
        ([0x08], d => d.Reset()),                                                   // DEVICE RESET(割り込みは上げない)
        ([0xEC, 0x20, 0x21], d => { d.Signature(); d.Abort(); }),                   // IDENTIFY DEVICE / READ SECTORS
        ([0x90], d => { d.Signature(); d.error = 1; d.status = 0; d.interrupt = true; }),   // EXECUTE DEVICE DIAGNOSTIC
        ([0xE5], d => { d.reason = 0xFF; d.Complete(); }),                          // CHECK POWER MODE: 動作中
        ([0xEF, 0xE0, 0xE1, 0xE2, 0xE3, 0xE6], d => d.Complete()),                  // SET FEATURES / STANDBY / IDLE / SLEEP
    ];

    static readonly Dictionary<byte, Action<AtapiDevice>> AtaCommands =
        (from entry in AtaCommandGroups
         from code in entry.codes
         select (code, entry.action))
        .ToDictionary(pair => pair.code, pair => pair.action);

    void Complete()
    {
        error = 0;
        status = DRDY | DSC;
        interrupt = true;
    }

    void Abort()
    {
        StopTransfer();
        error = 0x04;
        status = DRDY | ERR;
        interrupt = true;
    }

    // ATA の 1 ブロックのデータ(IDENTIFY PACKET DEVICE の 512 バイト): 読み終えたら状態だけ戻す。
    void SendBlock(byte[] data)
    {
        StopTransfer();
        buffer = data;
        chunkEnd = length = data.Length;
        error = 0;
        status = DRDY | DSC | DRQ;
        interrupt = true;
    }

    // IDENTIFY PACKET DEVICE の応答(256 ワード。Word はワード番号での位置)。DMA は持たない。
    static readonly byte[] Identify = Sequence(
        AtaDevice.Word(0, U16(0x85C0)),                                     // ATAPI、CD-ROM、リムーバブル、DRQ まで 50µs、12 バイトのパケット
        AtaDevice.Word(10, AtaDevice.AtaString("EMU86-0000000000-CD1", 20)),    // シリアル
        AtaDevice.Word(20, U16(3)), AtaDevice.Word(21, U16(512)), AtaDevice.Word(22, U16(4)),
        AtaDevice.Word(23, AtaDevice.AtaString("1.0", 8)),                  // ファームウェア
        AtaDevice.Word(27, AtaDevice.AtaString("Emu86 Virtual DVD-ROM", 40)),   // モデル
        AtaDevice.Word(48, U16(1)),                                         // 32 ビット I/O
        AtaDevice.Word(49, U16(0x0200)),                                    // LBA 対応、DMA なし
        AtaDevice.Word(53, U16(0x0003)),                                    // words 54-58、64-70 有効
        AtaDevice.Word(64, U16(0x0003)),                                    // PIO モード 3、4
        AtaDevice.Word(65, U16(0xB4)), AtaDevice.Word(66, U16(0xB4)), AtaDevice.Word(67, U16(0x12C)), AtaDevice.Word(68, U16(0xB4)),
        AtaDevice.Word(71, U16(30)), AtaDevice.Word(72, U16(30)),
        AtaDevice.Word(80, U16(0x001E)))                                    // ATA/ATAPI-4 まで
        .Build(512);

    // PACKET: 続く 12 バイトのコマンドを待つ。応答の 1 回分の上限はバイト数レジスタから取る。
    void BeginPacket()
    {
        if ((features & 1) != 0) { L("  -> PACKET with DMA: abort"); Abort(); return; }
        StopTransfer();
        limit = countLow | (countHigh << 8);
        awaitingPacket = true;
        reason = ReasonPacket;
        error = 0;
        status = DRDY | DSC | DRQ;
    }

    // データポート(0x170)の読み出し。今の DRQ の範囲を読み切ったら次の区切りへ進む。
    public uint ReadData(int size)
    {
        uint value = 0;
        for (var index = 0; index < size; index++)
            value |= (uint)(position < chunkEnd ? buffer[position++] : 0) << (8 * index);
        if (position >= chunkEnd && (status & DRQ) != 0 && !awaitingPacket)
        {
            if (packetTransfer) NextChunk();
            else { StopTransfer(); status = DRDY | DSC; }
        }
        return value;
    }

    // データポート(0x170)への書き込み。受け付けるのは PACKET に続く 12 バイトのコマンドだけ。
    public void WriteData(int size, uint value)
    {
        for (var index = 0; index < size && awaitingPacket; index++)
        {
            packet[packetLength++] = (byte)(value >> (8 * index));
            if (packetLength == packet.Length)
            {
                awaitingPacket = false;
                ExecutePacket();
            }
        }
    }

    void ExecutePacket()
    {
        L($"packet {Convert.ToHexString(packet)} limit={limit:x}");
        // REQUEST SENSE 以外のコマンドは、前のコマンドのセンスを消す。
        if (packet[0] != 0x03) (senseKey, senseCode, senseQualifier) = (0, 0, 0);
        if (Packets.TryGetValue(packet[0], out var action)) action(this);
        else Fail(IllegalRequest, InvalidCommand);
    }

    // SCSI/MMC コマンドの表(番号 → 動作)。表にないコマンドは ILLEGAL REQUEST(無効なコマンド)で終わる。
    static readonly Dictionary<byte, Action<AtapiDevice>> Packets = new()
    {
        [0x00] = d => d.WhenLoaded(d.Done),                                         // TEST UNIT READY
        [0x03] = d => d.RequestSense(),                                             // REQUEST SENSE
        [0x12] = d => d.Reply(Inquiry, d.packet[4]),                                // INQUIRY
        [0x1B] = d => d.StartStop(),                                                // START STOP UNIT
        [0x1E] = d => { d.locked = (d.packet[4] & 1) != 0; d.Done(); },             // PREVENT ALLOW MEDIUM REMOVAL
        [0x25] = d => d.WhenLoaded(() => d.Reply(d.Capacity(), 8)),                 // READ CAPACITY
        [0x28] = d => d.Read(d.Be32(2), d.Be16(7)),                                 // READ(10)
        [0xA8] = d => d.Read(d.Be32(2), d.Be32(6)),                                 // READ(12)
        [0x2B] = d => d.WhenLoaded(d.Seek),                                         // SEEK(10)
        [0x43] = d => d.WhenLoaded(d.ReadToc),                                      // READ TOC/PMA/ATIP
        [0x46] = d => d.GetConfiguration(),                                         // GET CONFIGURATION
        [0x4A] = d => d.GetEventStatus(),                                           // GET EVENT STATUS NOTIFICATION
        [0x51] = d => d.WhenLoaded(d.ReadDiscInformation),                          // READ DISC INFORMATION
        [0x5A] = d => d.ModeSense(),                                                // MODE SENSE(10)
        [0xAD] = d => d.WhenLoaded(d.ReadDvdStructure),                             // READ DVD STRUCTURE
        [0xBB] = d => d.Done(),                                                     // SET CD SPEED
        [0xBD] = d => d.Reply([0, 0, 0, 0, 0, 1, 0, 0], (int)d.Be16(8)),            // MECHANISM STATUS
        [0xBE] = d => d.ReadCd(),                                                   // READ CD
    };

    // パケット中のビッグエンディアンの値。
    uint Be16(int offset) => (uint)(packet[offset] << 8 | packet[offset + 1]);
    uint Be32(int offset) => (uint)(packet[offset] << 24 | packet[offset + 1] << 16 | packet[offset + 2] << 8 | packet[offset + 3]);

    // 媒体が要るコマンド: 無ければ NOT READY(媒体なし)。
    void WhenLoaded(Action action)
    {
        if (media == null) Fail(NotReady, MediumNotPresent);
        else action();
    }

    // DVD か(CD に収まらないセクタ数。80 分の CD は 360,000 セクタ)。
    bool IsDvd => media is { TotalSectors: > 80 * 60 * 75 };

    // コマンド完了: 状態だけを返す(割り込み理由 = IO|CoD)。
    void Done()
    {
        StopTransfer();
        error = 0;
        status = DRDY | DSC;
        reason = ReasonStatus;
        interrupt = true;
    }

    // コマンド失敗: センスを覚え、CHECK CONDITION(エラーレジスタの上位 4 ビットがセンスキー)で終わる。
    void Fail(byte key, byte code)
    {
        L($"  -> check condition key={key:x} asc={code:x2}");
        StopTransfer();
        (senseKey, senseCode, senseQualifier) = (key, code, 0);
        error = (byte)(key << 4);
        status = DRDY | ERR;
        reason = ReasonStatus;
        interrupt = true;
    }

    // データを返す(allocation = ホストが受け取れる長さ。超える分は送らない)。
    void Reply(byte[] data, int allocation)
    {
        StopTransfer();
        buffer = data;
        length = Math.Min(data.Length, allocation);
        packetTransfer = true;
        error = 0;
        NextChunk();
    }

    // 残りのデータの次の区切りを DRQ で出す(バイト数レジスタに今回の長さ)。残りが無ければ完了。
    void NextChunk()
    {
        if (position >= length) { Done(); return; }
        var count = length - position;
        var most = limit is 0 or 0xFFFF ? 0xFFFE : limit;
        if (count > most) count = most & ~1;    // 途中の区切りは偶数バイト
        chunkEnd = position + count;
        countLow = (byte)count; countHigh = (byte)(count >> 8);
        reason = ReasonDataIn;
        status = DRDY | DSC | DRQ;
        interrupt = true;
    }

    void RequestSense()
    {
        byte[] data = [0x70, 0, senseKey, 0, 0, 0, 0, 10, 0, 0, 0, 0, senseCode, senseQualifier, 0, 0, 0, 0];
        (senseKey, senseCode, senseQualifier) = (0, 0, 0);
        Reply(data, packet[4]);
    }

    // INQUIRY: CD/DVD(種別 5)、リムーバブル、ATAPI(応答形式 2)、追加長 31、ベンダ・製品・版。
    static readonly byte[] Inquiry = Sequence(
        Bytes(0x05, 0x80, 0x00, 0x21, 31, 0, 0, 0),
        Ascii("EMU86   "), Ascii("Virtual DVD-ROM "), Ascii("1.0 ")).Build(36);

    // START STOP UNIT: トレイは開かない(媒体は入ったまま)。取り出しが禁止されているあいだの排出要求は拒む。
    void StartStop()
    {
        var eject = (packet[4] & 0x03) == 0x02;
        if (eject && locked) Fail(NotReady, RemovalPrevented);
        else Done();
    }

    // READ CAPACITY: 最後のセクタの番号とセクタ長。
    byte[] Capacity() => Sequence(U32BE((uint)(media.TotalSectors - 1)), U32BE(CdMedia.SectorSize)).Build(8);

    void Read(uint lba, uint count)
    {
        if (media == null) { Fail(NotReady, MediumNotPresent); return; }
        if (count == 0) { Done(); return; }
        if (count > MaxReadSectors) { Fail(IllegalRequest, InvalidField); return; }
        if (lba + (long)count > media.TotalSectors) { Fail(IllegalRequest, LbaOutOfRange); return; }
        var data = new byte[count * CdMedia.SectorSize];
        try { media.Read(lba, (int)count, data, 0); }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"[atapi] read error at sector {lba} (+{count}): {exception.Message}");
            Fail(MediumError, ReadError);
            return;
        }
        Reply(data, data.Length);
    }

    void Seek()
    {
        if (Be32(2) >= media.TotalSectors) Fail(IllegalRequest, LbaOutOfRange);
        else Done();
    }

    // READ CD: 利用者データ(2048 バイト)だけを返す形(フラグ 0x10)に応える。データを求めない形は何もせず完了。
    void ReadCd()
    {
        var count = Be32(5) & 0xFFFFFF;
        var flags = packet[9] & 0xF8;
        if (count == 0 || flags == 0) Done();
        else if (flags == 0x10) Read(Be32(2), count);
        else Fail(IllegalRequest, InvalidField);
    }

    // セクタ番号を MSF(分・秒・フレーム。先頭に 2 秒のずれ)にする。
    static byte[] Msf(long lba) => [0, (byte)((lba + 150) / 75 / 60), (byte)((lba + 150) / 75 % 60), (byte)((lba + 150) % 75)];

    byte[] Address(long lba, bool msf) => msf ? Msf(lba) : U32BE((uint)lba).Build(4);

    // READ TOC: データトラック 1 本とリードアウトだけの TOC。形式 0 = トラックの一覧、1 = セッション情報、2 = 生の TOC。
    void ReadToc()
    {
        var msf = (packet[1] & 0x02) != 0;
        var format = (packet[2] & 0x0F) != 0 ? packet[2] & 0x0F : packet[9] >> 6;
        var startTrack = packet[6];
        var total = media.TotalSectors;
        byte[] body;
        switch (format)
        {
            case 0:
                if (startTrack > 1 && startTrack != 0xAA) { Fail(IllegalRequest, InvalidField); return; }
                body =
                [
                    1, 1,                                                           // 最初と最後のトラック
                    .. startTrack <= 1 ? (byte[])[0, 0x14, 1, 0, .. Address(0, msf)] : [],  // トラック 1(データ)
                    0, 0x16, 0xAA, 0, .. Address(total, msf),                       // リードアウト
                ];
                break;
            case 1:
                body = [1, 1, 0, 0x14, 1, 0, 0, 0, 0, 0];                           // 最後のセッションの最初のトラック
                break;
            case 2:
                body =
                [
                    1, 1,
                    1, 0x14, 0, 0xA0, 0, 0, 0, 0, 1, 0, 0,                          // 最初のトラック
                    1, 0x14, 0, 0xA1, 0, 0, 0, 0, 1, 0, 0,                          // 最後のトラック
                    1, 0x14, 0, 0xA2, 0, 0, 0, .. Address(total, msf),              // リードアウト
                    1, 0x14, 0, 1, 0, 0, 0, .. Address(0, msf),                     // トラック 1
                ];
                break;
            default:
                Fail(IllegalRequest, InvalidField);
                return;
        }
        Reply([(byte)(body.Length >> 8), (byte)body.Length, .. body], (int)Be16(7));
    }

    // 機能の記述子: 番号、版・常設・現在の有無、追加データ。
    static byte[] Feature(ushort code, byte flags, params byte[] data) => [(byte)(code >> 8), (byte)code, flags, (byte)data.Length, .. data];

    // GET CONFIGURATION: プロファイルの一覧(DVD-ROM、CD-ROM)、コア、リムーバブル媒体。現在のプロファイルは媒体の大きさで決める。
    // 要求の種別(RT): 0 = 開始番号以降の全部、1 = そのうち現在有効なもの、2 = 開始番号の機能だけ。
    void GetConfiguration()
    {
        var current = media == null ? 0 : IsDvd ? 0x10 : 0x08;
        (ushort code, byte[] descriptor)[] features =
        [
            (0x0000, Feature(0x0000, 0x03, 0x00, 0x10, (byte)(current == 0x10 ? 1 : 0), 0x00, 0x00, 0x08, (byte)(current == 0x08 ? 1 : 0), 0x00)),
            (0x0001, Feature(0x0001, 0x0B, 0x00, 0x00, 0x00, 0x02, 0x01, 0x00, 0x00, 0x00)),    // 物理インターフェース = ATAPI
            (0x0003, Feature(0x0003, 0x03, 0x29, 0x00, 0x00, 0x00)),                            // トレイ式、排出可、ロック可
        ];
        var start = Be16(2);
        var only = (packet[1] & 3) == 2;
        byte[] body = [.. features.Where(feature => only ? feature.code == start : feature.code >= start).SelectMany(feature => feature.descriptor)];
        Reply([.. U32BE((uint)(body.Length + 4)).Build(4), 0, 0, (byte)(current >> 8), (byte)current, .. body], (int)Be16(7));
    }

    // GET EVENT STATUS NOTIFICATION(ポーリングのみ): 媒体クラスの事象。変化なし、媒体あり。
    void GetEventStatus()
    {
        if ((packet[1] & 1) == 0) { Fail(IllegalRequest, InvalidField); return; }
        var mediaClass = (packet[4] & 0x10) != 0;
        byte[] data = mediaClass
            ? [0, 4, 0x04, 0x10, 0x00, (byte)(media != null ? 0x02 : 0x00), 0, 0]
            : [0, 0, 0x80, 0x10];
        Reply(data, (int)Be16(7));
    }

    // READ DISC INFORMATION(標準の形式だけ): 完結した 1 セッション 1 トラックのディスク。
    void ReadDiscInformation()
    {
        if ((packet[1] & 7) != 0) { Fail(IllegalRequest, InvalidField); return; }
        var data = new byte[34];
        data[1] = 32;       // 以降の長さ
        data[2] = 0x0E;     // 最後のセッションは完結、ディスクは確定済み
        data[3] = 1;        // 最初のトラック
        data[4] = 1;        // セッション数
        data[5] = 1;        // 最後のセッションの最初のトラック
        data[6] = 1;        // 最後のセッションの最後のトラック
        data[7] = 0x20;     // 用途の制限なし
        Reply(data, (int)Be16(7));
    }

    // MODE SENSE(10): 現在値のうち、読み取りエラー回復(0x01)、オーディオ制御(0x0E)、CD の能力と機構の状態(0x2A)。
    void ModeSense()
    {
        var page = packet[2] & 0x3F;
        var pageControl = packet[2] >> 6;       // 0 = 現在値、1 = 変更可能、2 = 既定値、3 = 保存値
        if (pageControl == 3) { Fail(IllegalRequest, SavingNotSupported); return; }
        byte[] body = pageControl != 0 ? null : page switch
        {
            0x01 => [0x01, 6, 0, 0x05, 0, 0, 0, 0],                                 // 再試行 5 回
            0x0E => [0x0E, 14, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            0x2A =>
            [
                0x2A, 20,
                0x3B, 0x00,                                                         // CD-R/RW、DVD-ROM/R/RAM を読める
                0x71, 0x60,                                                         // オーディオ再生など
                (byte)(0x29 | (locked ? 0x02 : 0)), 0x00,                           // ロック可、排出可、トレイ式
                0x02, 0xC0, 0, 2, 0x02, 0x00, 0x02, 0xC0,                           // 最大 4 倍速(704 KB/s)、音量 2 段、バッファ 512KB、現在 4 倍速
                0, 0, 0, 0, 0, 0,
            ],
            _ => null,
        };
        if (body == null) { Fail(IllegalRequest, InvalidField); return; }
        var size = 8 + body.Length;
        Reply([(byte)((size - 2) >> 8), (byte)(size - 2), 0x70, 0, 0, 0, 0, 0, .. body], (int)Be16(7));
    }

    // READ DVD STRUCTURE(DVD の媒体だけ): 物理フォーマット(0)、著作権(1)、製造情報(4)。
    void ReadDvdStructure()
    {
        if (!IsDvd) { Fail(IllegalRequest, IncompatibleFormat); return; }
        var layer = packet[6];
        var last = (uint)(media.TotalSectors - 1);
        byte[] data = packet[7] switch
        {
            0x00 when layer == 0 => Sequence(
                At(0, U16BE(2048 + 2)),
                At(4, Bytes(0x01, 0x0F, 0x01, 0x00)),                               // DVD-ROM、120mm、1 層、読み取り専用
                At(8, Sequence(U32BE(0), U32BE(last), U32BE(last)))).Build(2048 + 4),   // 開始、終了、0 層の終了
            0x01 => U16BE(4 + 2).Build(4 + 4),                                      // 著作権保護なし、地域の制限なし
            0x04 => U16BE(2048 + 2).Build(2048 + 4),
            _ => null,
        };
        if (data == null) { Fail(IllegalRequest, InvalidField); return; }
        Reply(data, (int)Be16(8));
    }
}
