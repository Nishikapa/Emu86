namespace Emu86;

public class Unit
{
    public static Unit unit = default;
}

static public class Conversions
{
    static public Data ToTypeData(this byte _db) => (0, db: _db, dw: default, dd: default);
    static public Data ToTypeData(this ushort _dw) => (1, db: default, dw: _dw, dd: default);
    static public Data ToTypeData(this uint _dd) => (2, db: default, dw: default, dd: _dd);

    // Data の読み出し(拡張メンバー)。
    extension(Data d)
    {
        // 実値を uint として取り出す。
        public uint Value => d.type switch { 0 => d.db, 1 => d.dw, _ => d.dd };

        // 実値を符号付きで取り出す(IMUL、BT 系のビット番号)。
        public int Signed => d.type switch { 0 => (sbyte)d.db, 1 => (short)d.dw, _ => (int)d.dd };
    }

    // uint を type の幅に丸めて Data にする。
    static public Data ToTypeData(this uint v, int type) => type switch
    {
        0 => ((byte)v).ToTypeData(),
        1 => ((ushort)v).ToTypeData(),
        _ => v.ToTypeData(),
    };

    // type(0=byte, 1=word, 2=dword)ごとのバイト数・ビット幅・マスク・最上位ビット。
    static public int Bytes(int type) => 1 << type;
    static public int Bits(int type) => 8 << type;
    static public uint Mask(int type) => type == 2 ? 0xFFFFFFFF : (1u << Bits(type)) - 1;
    static public uint Msb(int type) => 1u << (Bits(type) - 1);
}
