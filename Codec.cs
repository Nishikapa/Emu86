namespace Emu86;

// スナップショットの双方向コーデック。フィールドを保存形式の並び順に 1 度だけ記述し、
// Save(全フィールド)と Load(スナップショットの版に存在するフィールドだけ)を同じ記述から導出する。
// 固定長の配列は既存の配列へ読み込み、可変長のものは上限付きで長さを前置する。
internal sealed class Codec<T>
{
    readonly List<(int since, Action<BinaryWriter, T> save, Action<BinaryReader, T, int> load)> fields = [];

    Codec<T> Add(int since, Action<BinaryWriter, T> save, Action<BinaryReader, T, int> load)
    {
        fields.Add((since, save, load));
        return this;
    }

    public void Save(BinaryWriter writer, T target)
    {
        foreach (var field in fields) field.save(writer, target);
    }

    // version より新しい版で追加されたフィールドは読まない(呼び出し側が既定値や代替値を用意する)。
    public void Load(BinaryReader reader, T target, int version)
    {
        foreach (var field in fields)
            if (version >= field.since) field.load(reader, target, version);
    }

    Codec<T> Scalar<V>(Func<T, V> get, Action<T, V> set, Action<BinaryWriter, V> write, Func<BinaryReader, V> read, int since) =>
        Add(since, (writer, target) => write(writer, get(target)), (reader, target, _) => set(target, read(reader)));

    public Codec<T> Bool(Func<T, bool> get, Action<T, bool> set, int since = 0) => Scalar(get, set, (w, v) => w.Write(v), r => r.ReadBoolean(), since);
    public Codec<T> U8(Func<T, byte> get, Action<T, byte> set, int since = 0) => Scalar(get, set, (w, v) => w.Write(v), r => r.ReadByte(), since);
    public Codec<T> U16(Func<T, ushort> get, Action<T, ushort> set, int since = 0) => Scalar(get, set, (w, v) => w.Write(v), r => r.ReadUInt16(), since);
    public Codec<T> U32(Func<T, uint> get, Action<T, uint> set, int since = 0) => Scalar(get, set, (w, v) => w.Write(v), r => r.ReadUInt32(), since);
    public Codec<T> U64(Func<T, ulong> get, Action<T, ulong> set, int since = 0) => Scalar(get, set, (w, v) => w.Write(v), r => r.ReadUInt64(), since);
    public Codec<T> I32(Func<T, int> get, Action<T, int> set, int since = 0) => Scalar(get, set, (w, v) => w.Write(v), r => r.ReadInt32(), since);
    public Codec<T> I64(Func<T, long> get, Action<T, long> set, int since = 0) => Scalar(get, set, (w, v) => w.Write(v), r => r.ReadInt64(), since);

    // アクセサ(Lens)経由のフィールド。
    public Codec<T> Bool(Accessor<T, bool> accessor, int since = 0) => Bool(accessor.getter, (target, value) => accessor.setter(target)(value), since);
    public Codec<T> U16(Accessor<T, ushort> accessor, int since = 0) => U16(accessor.getter, (target, value) => accessor.setter(target)(value), since);
    public Codec<T> U32(Accessor<T, uint> accessor, int since = 0) => U32(accessor.getter, (target, value) => accessor.setter(target)(value), since);

    // 固定長の配列(既存の配列へ読み込む)。
    public Codec<T> Bytes(Func<T, byte[]> get, int since = 0) =>
        Add(since, (writer, target) => writer.Write(get(target)), (reader, target, _) => reader.BaseStream.ReadExactly(get(target)));

    public Codec<T> U32s(Func<T, uint[]> get, int since = 0) => Add(since,
        (writer, target) => { foreach (var value in get(target)) writer.Write(value); },
        (reader, target, _) => { var values = get(target); for (var index = 0; index < values.Length; index++) values[index] = reader.ReadUInt32(); });

    public Codec<T> F64s(Func<T, double[]> get, int since = 0) => Add(since,
        (writer, target) => { foreach (var value in get(target)) writer.Write(value); },
        (reader, target, _) => { var values = get(target); for (var index = 0; index < values.Length; index++) values[index] = reader.ReadDouble(); });

    // 長さを前置する可変長バイト列(上限付き)。
    public Codec<T> ByteArray(Func<T, byte[]> get, Action<T, byte[]> set, int maximum, int since = 0) => Add(since,
        (writer, target) => { var values = get(target); writer.Write(values.Length); writer.Write(values); },
        (reader, target, _) =>
        {
            var values = new byte[SnapshotStore.ReadCount(reader, maximum)];
            reader.BaseStream.ReadExactly(values);
            set(target, values);
        });

    public Codec<T> ByteQueue(Func<T, Queue<byte>> get, int maximum, int since = 0) => Add(since,
        (writer, target) => { var queue = get(target); writer.Write(queue.Count); foreach (var value in queue) writer.Write(value); },
        (reader, target, _) =>
        {
            var queue = get(target);
            queue.Clear();
            var count = SnapshotStore.ReadCount(reader, maximum);
            for (var index = 0; index < count; index++) queue.Enqueue(reader.ReadByte());
        });

    // 入れ子の対象(同じ版で読む)。
    public Codec<T> Nested<N>(Func<T, N> get, Codec<N> codec, int since = 0) =>
        Add(since, (writer, target) => codec.Save(writer, get(target)), (reader, target, version) => codec.Load(reader, get(target), version));

    // 定型に収まらない項目。
    public Codec<T> Custom(Action<BinaryWriter, T> save, Action<BinaryReader, T> load, int since = 0) =>
        Add(since, save, (reader, target, _) => load(reader, target));

    // 読み込み直後の検査(保存では何もしない)。
    public Codec<T> Check(Func<T, bool> valid, string message) =>
        Add(0, (_, _) => { }, (_, target, _) => { if (!valid(target)) throw new InvalidDataException(message); });
}
