namespace Emu86;

public class Unit
{
    public static Unit unit = default;
}

static public partial class Ext
{
    private static readonly Func<byte[], Data>[] funcArray =
    [
        data => data[0].ToTypeData(),
        data => BitConverter.ToUInt16(data, 0).ToTypeData(),
        data => BitConverter.ToUInt32(data, 0).ToTypeData()
    ];

    static public Data ToTypeData(this IEnumerable<byte> data, int type)
        => funcArray[type]([.. data]);

    static public Data ToTypeData(this byte _db) => (0, db: _db, dw: default, dd: default);
    static public Data ToTypeData(this ushort _dw) => (1, db: default, dw: _dw, dd: default);
    static public Data ToTypeData(this uint _dd) => (2, db: default, dw: default, dd: _dd);

    // Data の実値を uint として取り出す / uint を type の幅に丸めて Data にする。
    static public uint Value(this Data d) => d.type == 0 ? d.db : d.type == 1 ? d.dw : d.dd;
    static public Data ToTypeData(this uint v, int type) =>
        type == 0 ? ((byte)v).ToTypeData() : type == 1 ? ((ushort)v).ToTypeData() : v.ToTypeData();

    // type ごとのビット幅・マスク・最上位ビット。
    static public int Bits(int type) => type == 0 ? 8 : type == 1 ? 16 : 32;
    static public uint Mask(int type) => type == 2 ? 0xFFFFFFFF : (1u << Bits(type)) - 1;
    static public uint Msb(int type) => 1u << (Bits(type) - 1);

    static bool TopBit(uint data) => (0 != (data & 0x80000000));
    static bool TopBit(ushort data) => (0 != (data & 0x8000));
    static bool TopBit(byte data) => (0 != (data & 0x80));

    static public uint ToUint32(this IEnumerable<byte> data) => BitConverter.ToUInt32([.. data.Take(4)], 0);
    static public ushort ToUint16(this IEnumerable<byte> data) => BitConverter.ToUInt16([.. data.Take(4)], 0);

    static public byte[] ToByteArray(this byte db) => [db];
    static public byte[] ToByteArray(this ushort dw) => BitConverter.GetBytes(dw);
    static public byte[] ToByteArray(this uint dd) => BitConverter.GetBytes(dd);

    static public T Choice<T, K>(K key, params (K key, T state)[] states) => states.ToDictionary(s => s.key, s => s.state)[key];
    static public T Choice_<T>(int index, params T[] states) => states[index];

    // type(0=byte,1=word,2=dword) に応じて幅ごとの関数を適用し、結果を TypeData にまとめる。
    // NOT/NEG/INC/DEC など「型別の値計算 → 書き戻し用 TypeData」を 1 行で書くための共通化。
    static public Data MapType(
        this Data d,
        Func<byte, byte> fb, Func<ushort, ushort> fw, Func<uint, uint> fd) =>
        d.type == 0 ? fb(d.db).ToTypeData()
      : d.type == 1 ? fw(d.dw).ToTypeData()
      : fd(d.dd).ToTypeData();
}
