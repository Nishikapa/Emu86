namespace Emu86;

public partial class EmuEnvironment
{
    public long NextIrq;

    internal void SaveRuntimeState(BinaryWriter writer)
    {
        writer.Write(Tsc);
        writer.Write(Msrs.Count);
        foreach (var entry in Msrs.OrderBy(entry => entry.Key))
        {
            writer.Write(entry.Key);
            writer.Write(entry.Value);
        }
        writer.Write(PicMasterBase);
        writer.Write(PicSlaveBase);
        writer.Write(PicMasterMask);
        writer.Write(PicSlaveMask);
    }

    internal void LoadRuntimeState(BinaryReader reader, int version, long count)
    {
        Msrs.Clear();
        Tsc = version >= 3 ? reader.ReadUInt64() : (ulong)count;
        if (version >= 3)
        {
            var entries = SnapshotStore.ReadCount(reader, 1 << 20);
            for (var index = 0; index < entries; index++)
                Msrs.Add(reader.ReadUInt32(), reader.ReadUInt64());
        }
        if (version >= 4)
        {
            PicMasterBase = reader.ReadByte();
            PicSlaveBase = reader.ReadByte();
            PicMasterMask = reader.ReadByte();
            PicSlaveMask = reader.ReadByte();
        }
    }

    internal void SaveDeviceState(BinaryWriter writer)
    {
        Pci.SaveState(writer);
        writer.Write(PicMasterInit); writer.Write(PicSlaveInit);
        writer.Write(PicMasterIcw4); writer.Write(PicSlaveIcw4);
        writer.Write(PicMasterIsr); writer.Write(PicSlaveIsr);
        writer.Write(NextIrq);
        writer.Write(Port61); writer.Write(Port61Refresh);
        writer.Write(Pit2Counter); writer.Write(Pit2WritePhase); writer.Write(Pit2ReadPhase);
        writer.Write(Pit2Armed); writer.Write(Pit2Wait); writer.Write(PitStatusPending);
        writer.Write(KbdOut.Count);
        foreach (var value in KbdOut) writer.Write(value);
        writer.Write(KbdLast); writer.Write(KbdCmdByte); writer.Write(KbdPendingCmd);
        writer.Write(Pm1Sts); writer.Write(Pm1En); writer.Write(Pm1Cnt);
        writer.Write(Gpe0Sts); writer.Write(Gpe0En);
        foreach (var value in Dr) writer.Write(value);
        Ata?.SaveInterruptState(writer);
    }

    internal void LoadDeviceState(BinaryReader reader)
    {
        Pci.LoadState(reader);
        PicMasterInit = reader.ReadInt32(); PicSlaveInit = reader.ReadInt32();
        PicMasterIcw4 = reader.ReadBoolean(); PicSlaveIcw4 = reader.ReadBoolean();
        PicMasterIsr = reader.ReadByte(); PicSlaveIsr = reader.ReadByte();
        NextIrq = reader.ReadInt64();
        Port61 = reader.ReadByte(); Port61Refresh = reader.ReadByte();
        Pit2Counter = reader.ReadUInt16(); Pit2WritePhase = reader.ReadInt32(); Pit2ReadPhase = reader.ReadInt32();
        Pit2Armed = reader.ReadBoolean(); Pit2Wait = reader.ReadInt32(); PitStatusPending = reader.ReadBoolean();
        KbdOut.Clear();
        var queued = SnapshotStore.ReadCount(reader, 1 << 20);
        for (var index = 0; index < queued; index++) KbdOut.Enqueue(reader.ReadByte());
        KbdLast = reader.ReadByte(); KbdCmdByte = reader.ReadByte(); KbdPendingCmd = reader.ReadInt32();
        Pm1Sts = reader.ReadUInt16(); Pm1En = reader.ReadUInt16(); Pm1Cnt = reader.ReadUInt16();
        Gpe0Sts = reader.ReadUInt16(); Gpe0En = reader.ReadUInt16();
        for (var index = 0; index < Dr.Length; index++) Dr[index] = reader.ReadUInt32();
        Ata?.LoadInterruptState(reader);
    }
}
