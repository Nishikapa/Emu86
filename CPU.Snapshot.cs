namespace Emu86;

public partial class CPU
{
    internal void SaveRuntimeState(BinaryWriter writer)
    {
        writer.Write(fpu_top);
        writer.Write(fpu_valid);
        foreach (var value in fpu_st) writer.Write(value);
        writer.Write(tr);
        writer.Write(ldtr);
    }

    internal void LoadRuntimeState(BinaryReader reader, int version)
    {
        if (version >= 5)
        {
            fpu_top = reader.ReadInt32();
            if (fpu_top is < 0 or > 7) throw new InvalidDataException("Invalid FPU stack top");
            fpu_valid = reader.ReadByte();
            for (var index = 0; index < fpu_st.Length; index++)
                fpu_st[index] = reader.ReadDouble();
        }
        if (version >= 6)
        {
            tr = reader.ReadUInt16();
            ldtr = reader.ReadUInt16();
        }
        Decode = default;
    }
}
