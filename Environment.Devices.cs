namespace Emu86;

public partial class EmuEnvironment
{
    internal CmosDevice CmosRtc { get; }
    internal PicDevice PicMaster { get; } = new(0x08, "master");
    internal PicDevice PicSlave { get; } = new(0x70, "slave");
    internal PitDevice Pit { get; }
    internal KeyboardController Keyboard { get; } = new();
    internal AcpiPmDevice Acpi { get; }
    public VgaDevice Vga { get; } = new();
    // COM1(0x3F8, IRQ4)と COM2(0x2F8, IRQ3)。ホスト側(名前付きパイプ)は起動時の指定で接続する。
    public Uart16550 Com1 { get; } = new();
    public Uart16550 Com2 { get; } = new();

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

    // v10 以前のスナップショット形式に残る旧 PIT の項目は、保存形式(Environment.Snapshot.cs)の予約領域として読み飛ばす。
    public byte Port61 { get => Pit.Port61; set => Pit.Port61 = value; }

    public Queue<byte> KbdOut => Keyboard.Output;
    public MouseDevice Mouse => Keyboard.Mouse;
    public byte KbdLast { get => Keyboard.Last; set => Keyboard.Last = value; }
    public byte KbdCmdByte { get => Keyboard.CommandByte; set => Keyboard.CommandByte = value; }
    public int KbdPendingCmd { get => Keyboard.PendingCommand; set => Keyboard.PendingCommand = value; }

    public ushort Pm1Sts { get => Acpi.Pm1Sts; set => Acpi.Pm1Sts = value; }
    public ushort Pm1En { get => Acpi.Pm1En; set => Acpi.Pm1En = value; }
    public ushort Pm1Cnt { get => Acpi.Pm1Cnt; set => Acpi.Pm1Cnt = value; }
    public ushort Gpe0Sts { get => Acpi.Gpe0Sts; set => Acpi.Gpe0Sts = value; }
    public ushort Gpe0En { get => Acpi.Gpe0En; set => Acpi.Gpe0En = value; }
}
