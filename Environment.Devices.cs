namespace Emu86;

public partial class EmuEnvironment
{
    internal CmosDevice CmosRtc { get; }
    internal PicDevice PicMaster { get; } = new(0x08, "master");
    internal PicDevice PicSlave { get; } = new(0x70, "slave");
    internal PitDevice Pit { get; } = new();
    internal KeyboardController Keyboard { get; } = new();
    internal AcpiPmDevice Acpi { get; } = new();

    public byte[] Cmos { get => CmosRtc.Data; set => CmosRtc.Data = value; }
    public int CmosIndex { get => CmosRtc.Index; set => CmosRtc.Index = value; }

    public byte PicMasterBase { get => PicMaster.Base; set => PicMaster.Base = value; }
    public byte PicMasterMask { get => PicMaster.Mask; set => PicMaster.Mask = value; }
    public int PicMasterInit { get => PicMaster.Init; set => PicMaster.Init = value; }
    public bool PicMasterIcw4 { get => PicMaster.Icw4; set => PicMaster.Icw4 = value; }
    public byte PicMasterIsr { get => PicMaster.Isr; set => PicMaster.Isr = value; }

    public byte PicSlaveBase { get => PicSlave.Base; set => PicSlave.Base = value; }
    public byte PicSlaveMask { get => PicSlave.Mask; set => PicSlave.Mask = value; }
    public int PicSlaveInit { get => PicSlave.Init; set => PicSlave.Init = value; }
    public bool PicSlaveIcw4 { get => PicSlave.Icw4; set => PicSlave.Icw4 = value; }
    public byte PicSlaveIsr { get => PicSlave.Isr; set => PicSlave.Isr = value; }

    public ushort PitCounter { get => Pit.Counter; set => Pit.Counter = value; }
    public ushort PitLatched { get => Pit.Latched; set => Pit.Latched = value; }
    public int PitReadPhase { get => Pit.ReadPhase; set => Pit.ReadPhase = value; }
    public byte Port61 { get => Pit.Port61; set => Pit.Port61 = value; }
    public byte Port61Refresh { get => Pit.Refresh; set => Pit.Refresh = value; }
    public ushort Pit2Counter { get => Pit.Channel2Counter; set => Pit.Channel2Counter = value; }
    public int Pit2WritePhase { get => Pit.Channel2WritePhase; set => Pit.Channel2WritePhase = value; }
    public int Pit2ReadPhase { get => Pit.Channel2ReadPhase; set => Pit.Channel2ReadPhase = value; }
    public bool Pit2Armed { get => Pit.Channel2Armed; set => Pit.Channel2Armed = value; }
    public int Pit2Wait { get => Pit.Channel2Wait; set => Pit.Channel2Wait = value; }
    public bool PitStatusPending { get => Pit.StatusPending; set => Pit.StatusPending = value; }

    public Queue<byte> KbdOut => Keyboard.Output;
    public byte KbdLast { get => Keyboard.Last; set => Keyboard.Last = value; }
    public byte KbdCmdByte { get => Keyboard.CommandByte; set => Keyboard.CommandByte = value; }
    public int KbdPendingCmd { get => Keyboard.PendingCommand; set => Keyboard.PendingCommand = value; }

    public ushort Pm1Sts { get => Acpi.Pm1Sts; set => Acpi.Pm1Sts = value; }
    public ushort Pm1En { get => Acpi.Pm1En; set => Acpi.Pm1En = value; }
    public ushort Pm1Cnt { get => Acpi.Pm1Cnt; set => Acpi.Pm1Cnt = value; }
    public ushort Gpe0Sts { get => Acpi.Gpe0Sts; set => Acpi.Gpe0Sts = value; }
    public ushort Gpe0En { get => Acpi.Gpe0En; set => Acpi.Gpe0En = value; }
    public uint PmTimer => AcpiPmDevice.Timer(Tsc);
}
