namespace Emu86;

internal sealed class CmosDevice
{
    public byte[] Data = new byte[128];
    public int Index;

    public CmosDevice(int memorySize)
    {
        int baseKB = Math.Min(memorySize / 1024, 640);
        int extKB = Math.Clamp((memorySize - 0x100000) / 1024, 0, 15 * 1024);
        int ext64 = memorySize > 0x1000000 ? (memorySize - 0x1000000) / (64 * 1024) : 0;

        Data[0x15] = (byte)baseKB; Data[0x16] = (byte)(baseKB >> 8);
        Data[0x17] = (byte)extKB; Data[0x18] = (byte)(extKB >> 8);
        Data[0x30] = (byte)extKB; Data[0x31] = (byte)(extKB >> 8);
        Data[0x34] = (byte)ext64; Data[0x35] = (byte)(ext64 >> 8);
    }

    public byte Read() => Data[Index];

    public void Select(byte value) => Index = value & 0x7F;

    public void Write(byte value) => Data[Index] = value;
}
