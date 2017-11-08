using System.Collections.Generic;
using System.Linq;
using System;

namespace Emu86
{
    public class Unit
    {
        public static Unit unit = default(Unit);
    }

    static public partial class Ext
    {
        static private Func<byte[], (int type, byte db, ushort dw, uint dd)>[] funcArray ={
            data=>data[0].ToTypeData(),
            data=>BitConverter.ToUInt16(data,0).ToTypeData(),
            data=>BitConverter.ToUInt32(data,0).ToTypeData()
        };

        static public (int type, byte db, ushort dw, uint dd) ToTypeData(this IEnumerable<byte> data, int type)
            => funcArray.ElementAt(type)(data.ToArray());

        static public (int type, byte db, ushort dw, uint dd) ToTypeData(this byte _db) => (0, db: _db, dw: default(ushort), dd: default(uint));
        static public (int type, byte db, ushort dw, uint dd) ToTypeData(this ushort _dw) => (1, db: default(byte), dw: _dw, dd: default(uint));
        static public (int type, byte db, ushort dw, uint dd) ToTypeData(this uint _dd) => (2, db: default(byte), dw: default(ushort), dd: _dd);

        static bool TopBit(uint data) => (0 != (data & 0x80000000));
        static bool TopBit(ushort data) => (0 != (data & 0x8000));
        static bool TopBit(byte data) => (0 != (data & 0x80));

        static public uint ToUint32(this IEnumerable<byte> data) => BitConverter.ToUInt32(data.Take(4).ToArray(), 0);

        static public byte[] ToByteArray(this byte db) => new[] { db };
        static public byte[] ToByteArray(this ushort dw) => BitConverter.GetBytes(dw);
        static public byte[] ToByteArray(this uint dd) => BitConverter.GetBytes(dd);

        static public T Choice<T, K>(K key, params (K key, T state)[] states) => states.ToDictionary(s => s.key, s => s.state)[key];
        static public T Choice_<T>(int index, params T[] states) => states.ElementAt(index);
    }
}
