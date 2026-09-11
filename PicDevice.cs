namespace Emu86;

internal sealed class PicDevice(byte vectorBase, string name)
{
    public byte Base = vectorBase;
    public byte Mask;
    public int Init;
    public bool Icw4;
    public byte Isr;

    public void WriteCommand(byte value)
    {
        if ((value & 0x10) != 0)
        {
            Init = 1;
            Icw4 = (value & 1) != 0;
        }
        else if ((value & 0x08) == 0 && (value & 0x20) != 0)
            Isr = (value & 0x40) != 0 ? (byte)(Isr & ~(1 << (value & 7))) : (byte)0;
    }

    public void WriteData(byte value, bool log)
    {
        switch (Init)
        {
            case 1: Base = value; Init = 2; break;
            case 2: Init = Icw4 ? 3 : 0; break;
            case 3: Init = 0; break;
            default:
                if (log && Mask != value)
                    Console.Error.WriteLine($"[pic] {name} mask {Mask:x2} -> {value:x2}");
                Mask = value;
                break;
        }
    }
}
