using System.Text;

namespace Emu86;

// オプション ROM のバイト列ビルダ(Writer)。位置を進めながらバイト・ワード・文字列・機械語を書き、
// ラベルへの参照(jmp short の変位、ROM 内オフセット)は Build で解決する。
// 最後の 1 バイトは Checksum で、ROM 全体の和が 0 になるように埋める。
internal sealed class RomBuilder(int size)
{
    readonly byte[] image = new byte[size];
    readonly Dictionary<string, int> labels = [];
    readonly List<(int at, string label, Func<int, int, byte[]> encode)> fixups = [];
    int position;

    public RomBuilder At(int offset)
    {
        position = offset;
        return this;
    }

    public RomBuilder Label(string name)
    {
        labels[name] = position;
        return this;
    }

    public RomBuilder Db(params ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(image.AsSpan(position));
        position += bytes.Length;
        return this;
    }

    public RomBuilder Dw(ushort value) => Db((byte)value, (byte)(value >> 8));

    public RomBuilder Ascii(string text) => Db(Encoding.ASCII.GetBytes(text));

    // ラベルの ROM 内オフセット(16 ビット)。
    public RomBuilder Dw(string label) => Fixup(label, 2, (target, _) => [(byte)target, (byte)(target >> 8)]);

    // jmp short label(EB rel8)。変位は次の命令からの相対。
    public RomBuilder JmpShort(string label)
    {
        Db(0xEB);
        return Fixup(label, 1, (target, at) => [(byte)(target - (at + 1))]);
    }

    RomBuilder Fixup(string label, int length, Func<int, int, byte[]> encode)
    {
        fixups.Add((position, label, encode));
        position += length;
        return this;
    }

    public byte[] Checksum()
    {
        foreach (var (at, label, encode) in fixups)
        {
            if (!labels.TryGetValue(label, out var target)) throw new InvalidOperationException($"undefined label {label}");
            encode(target, at).CopyTo(image, at);
        }
        var sum = 0;
        for (var index = 0; index < size - 1; index++) sum += image[index];
        image[size - 1] = (byte)(-sum);
        return image;
    }
}
