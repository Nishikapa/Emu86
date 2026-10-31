namespace Emu86;

public partial class EmuEnvironment
{
    public long NextIrq;

    // タイマ割り込み(IRQ0)の要求が立っていてまだ配送できていない(IF=0・マスク・in-service 中など)。
    // 周期ごとに立ち、配送で降りる。スナップショットには含めない(再開時は次の周期から)。
    public bool TimerIrqPending;

    // v2 から: RAM・I/O ポート・CMOS・PIT チャネル 0 と、接続中なら ATA のレジスタとバッファ。
    // ディスクの中身(DiskImage)は書き込みのたびにファイルへ反映済みなのでここでは扱わない。
    static readonly Codec<EmuEnvironment> Memory = new Codec<EmuEnvironment>()
        .Bytes(e => e.Ram)
        .Bytes(e => e.IoPort)
        .Bytes(e => e.Cmos)
        .I32(e => e.CmosIndex, (e, v) => e.CmosIndex = v)
        .U16(e => e.PitCounter, (e, v) => e.PitCounter = v)
        .U16(e => e.PitLatched, (e, v) => e.PitLatched = v)
        .I32(e => e.PitReadPhase, (e, v) => e.PitReadPhase = v)
        .Custom(
            (writer, e) => { writer.Write(e.Ata != null); e.Ata?.SaveState(writer); },
            (reader, e) =>
            {
                if (reader.ReadBoolean())
                {
                    e.Ata ??= new AtaDevice(null);
                    e.Ata.LoadState(reader);
                }
                else
                {
                    e.Ata?.Dispose();
                    e.Ata = null;
                }
            });

    // v3 で TSC と MSR、v4 で PIC のベクタベースとマスクを追加。
    static readonly Codec<EmuEnvironment> Runtime = new Codec<EmuEnvironment>()
        .U64(e => e.Tsc, (e, v) => e.Tsc = v, since: 3)
        .Custom(
            (writer, e) =>
            {
                writer.Write(e.Msrs.Count);
                foreach (var entry in e.Msrs.OrderBy(entry => entry.Key))
                {
                    writer.Write(entry.Key);
                    writer.Write(entry.Value);
                }
            },
            (reader, e) =>
            {
                var entries = SnapshotStore.ReadCount(reader, 1 << 20);
                for (var index = 0; index < entries; index++)
                    e.Msrs.Add(reader.ReadUInt32(), reader.ReadUInt64());
            },
            since: 3)
        .U8(e => e.PicMasterBase, (e, v) => e.PicMasterBase = v, since: 4)
        .U8(e => e.PicSlaveBase, (e, v) => e.PicSlaveBase = v, since: 4)
        .U8(e => e.PicMasterMask, (e, v) => e.PicMasterMask = v, since: 4)
        .U8(e => e.PicSlaveMask, (e, v) => e.PicSlaveMask = v, since: 4);

    // v7 から(SnapshotStore が版で切り替える): PCI、PIC の初期化状態と in-service、次のタイマ時刻、
    // PIT チャネル 2 とポート 0x61、8042、ACPI PM、デバッグレジスタ、ATA の INTRQ。
    static readonly Codec<EmuEnvironment> Devices = new Codec<EmuEnvironment>()
        .Nested(e => e.Pci, PciHost.Layout)
        .I32(e => e.PicMasterInit, (e, v) => e.PicMasterInit = v)
        .I32(e => e.PicSlaveInit, (e, v) => e.PicSlaveInit = v)
        .Bool(e => e.PicMasterIcw4, (e, v) => e.PicMasterIcw4 = v)
        .Bool(e => e.PicSlaveIcw4, (e, v) => e.PicSlaveIcw4 = v)
        .U8(e => e.PicMasterIsr, (e, v) => e.PicMasterIsr = v)
        .U8(e => e.PicSlaveIsr, (e, v) => e.PicSlaveIsr = v)
        .I64(e => e.NextIrq, (e, v) => e.NextIrq = v)
        .U8(e => e.Port61, (e, v) => e.Port61 = v)
        .U8(e => e.Port61Refresh, (e, v) => e.Port61Refresh = v)
        .U16(e => e.Pit2Counter, (e, v) => e.Pit2Counter = v)
        .I32(e => e.Pit2WritePhase, (e, v) => e.Pit2WritePhase = v)
        .I32(e => e.Pit2ReadPhase, (e, v) => e.Pit2ReadPhase = v)
        .Bool(e => e.Pit2Armed, (e, v) => e.Pit2Armed = v)
        .I32(e => e.Pit2Wait, (e, v) => e.Pit2Wait = v)
        .Bool(e => e.PitStatusPending, (e, v) => e.PitStatusPending = v)
        .ByteQueue(e => e.KbdOut, 1 << 20)
        .U8(e => e.KbdLast, (e, v) => e.KbdLast = v)
        .U8(e => e.KbdCmdByte, (e, v) => e.KbdCmdByte = v)
        .I32(e => e.KbdPendingCmd, (e, v) => e.KbdPendingCmd = v)
        .U16(e => e.Pm1Sts, (e, v) => e.Pm1Sts = v)
        .U16(e => e.Pm1En, (e, v) => e.Pm1En = v)
        .U16(e => e.Pm1Cnt, (e, v) => e.Pm1Cnt = v)
        .U16(e => e.Gpe0Sts, (e, v) => e.Gpe0Sts = v)
        .U16(e => e.Gpe0En, (e, v) => e.Gpe0En = v)
        .U32s(e => e.Dr)
        .Custom((writer, e) => e.Ata?.SaveInterruptState(writer), (reader, e) => e.Ata?.LoadInterruptState(reader));

    public void SaveState(BinaryWriter writer) => Memory.Save(writer, this);

    public void LoadState(BinaryReader reader)
    {
        Memory.Load(reader, this, SnapshotStore.Version);
        Translations.InvalidateAll(); // RAM が差し替わった
    }

    internal void SaveRuntimeState(BinaryWriter writer) => Runtime.Save(writer, this);

    internal void LoadRuntimeState(BinaryReader reader, int version, long count)
    {
        Msrs.Clear();
        Tsc = (ulong)count; // v3 より前に TSC は無いので、命令数で代用する
        Runtime.Load(reader, this, version);
    }

    internal void SaveDeviceState(BinaryWriter writer) => Devices.Save(writer, this);

    internal void LoadDeviceState(BinaryReader reader) => Devices.Load(reader, this, SnapshotStore.Version);
}
